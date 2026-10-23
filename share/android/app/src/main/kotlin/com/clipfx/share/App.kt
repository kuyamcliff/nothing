package com.clipfx.share

import android.annotation.SuppressLint
import android.app.Application
import android.content.Context
import android.content.Intent
import android.media.MediaScannerConnection
import android.net.wifi.WifiManager
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import com.clipfx.share.core.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.io.File

class ShareApp : Application() {
    lateinit var repo: Repo; private set

    override fun onCreate() {
        super.onCreate()
        repo = Repo(this)
    }
}

val Context.repo: Repo get() = (applicationContext as ShareApp).repo

data class UiState(
    val ready: Boolean = false,
    val selfName: String = "",
    val addresses: List<String> = emptyList(),
    val port: Int = 0,
    val peers: List<PeerView> = emptyList(),
    val transfers: List<TransferView> = emptyList(),
    val settings: ShareSettings = ShareSettings(),
    val saveLocation: String = "",
    val allFiles: Boolean = false,
    val hotspot: Hotspot.Info? = null,
    val joinedSsid: String? = null,
    val message: String? = null,
    val scanning: Boolean = false,
)

/** Something picked to send: one file, a folder tree, an app, or text. */
data class Entry(val key: String, val name: String, val size: Long, val kind: String, val items: List<SourceItem>)

/** Owns the transfer engine for the whole app; the service keeps it alive in the background. */
class Repo(private val ctx: Application) : ShareHost {
    val routing = LocalRouting(ctx)
    val hotspot = Hotspot(ctx)
    val joiner = Joiner(ctx, routing)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state
    lateinit var node: ShareNode; private set
    private var started = false
    private var multicast: WifiManager.MulticastLock? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    val selection = MutableStateFlow<List<Entry>>(emptyList())
    val selectedPeers = MutableStateFlow<Set<String>>(emptySet())
    var onIncoming: ((TransferView) -> Unit)? = null
    var onFinished: ((TransferView) -> Unit)? = null

    override val socketPreparer: SocketPreparer get() = routing

    @Synchronized
    fun start() {
        if (started) return
        started = true
        val root = Folders.defaultRoot(ctx)
        Folders.prepare(root)
        node = ShareNode(File(ctx.filesDir, "share"), "android", root.path, AndroidTargets(ctx), AndroidOpener(ctx), this)
        if (node.settings.deviceName == "Android") node.updateSettings { it.copy(deviceName = defaultName()) }
        // Keep the default location in step with the all-files permission.
        if (!node.saveRoot.startsWith("content://") && node.saveRoot != root.path) node.changeSaveRoot(root.path)
        val wifi = ctx.getSystemService(WifiManager::class.java)
        multicast = wifi.createMulticastLock("clipfx-share").apply { setReferenceCounted(false); acquire() }
        scope.launch(Dispatchers.IO) {
            runCatching { node.start() }.onFailure { message("Couldn't start: ${it.message}") }
            refresh()
            var lastVersion = -1L
            while (isActive) {
                delay(250)
                val busy = _state.value.transfers.any { it.state == TransferState.Active }
                if (node.version != lastVersion || busy) { lastVersion = node.version; refresh() }
            }
        }
    }

    private fun defaultName(): String =
        runCatching { Settings.Global.getString(ctx.contentResolver, "device_name") }.getOrNull()?.takeIf { it.isNotBlank() }
            ?: "${Build.MANUFACTURER.replaceFirstChar { it.uppercase() }} ${Build.MODEL}".take(40)

    fun refresh() {
        if (!started || !::node.isInitialized) return
        val root = node.saveRoot
        _state.value = _state.value.copy(
            ready = node.port > 0, selfName = node.settings.deviceName, addresses = node.addresses(), port = node.port,
            peers = node.peers(), transfers = node.transfers(), settings = node.settings,
            saveLocation = if (root.startsWith("content://")) runCatching { SafTarget(ctx, android.net.Uri.parse(root)).display }.getOrDefault("Selected folder") else root.removePrefix("/storage/emulated/0/").let { "Internal storage/$it" },
            allFiles = Permissions.hasAllFiles(ctx),
        )
    }

    fun add(entries: List<Entry>) {
        val existing = selection.value.map { it.key }.toSet()
        selection.value = selection.value + entries.filter { it.key !in existing }
    }

    fun send(): Boolean {
        val peers = selectedPeers.value.filter { id -> _state.value.peers.any { it.id == id } }
        val entries = selection.value
        if (peers.isEmpty() || entries.isEmpty()) return false
        var n = 0
        val items = entries.flatMap { e -> e.items.map { SourceItem(it.info.copy(i = n++), it.ref) } }
        io { node.send(peers, items) }
        selection.value = emptyList()
        return true
    }

    fun message(text: String?) { _state.value = _state.value.copy(message = text) }

    fun io(block: suspend CoroutineScope.() -> Unit) = scope.launch(Dispatchers.IO) { runCatching { block() }.onFailure { message(it.message) }; refresh() }

    /** Re-check storage after the user granted or revoked all-files access. */
    fun storageChanged() {
        if (!::node.isInitialized) return
        val root = Folders.defaultRoot(ctx)
        Folders.prepare(root)
        if (!node.saveRoot.startsWith("content://")) node.changeSaveRoot(root.path)
        refresh()
    }

    fun useDefaultLocation() { val root = Folders.defaultRoot(ctx); Folders.prepare(root); node.changeSaveRoot(root.path); refresh() }

    fun useFolder(uri: android.net.Uri) {
        runCatching { ctx.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION) }
        node.changeSaveRoot(uri.toString()); refresh()
    }

    fun scan() {
        _state.value = _state.value.copy(scanning = true)
        scope.launch(Dispatchers.IO) {
            node.refresh()
            runCatching { node.scan() }
            _state.value = _state.value.copy(scanning = false)
            refresh()
        }
    }

    fun startHotspot() = hotspot.start(
        onReady = { info -> _state.value = _state.value.copy(hotspot = info); scope.launch { delay(1500); node.networkChanged(); refresh() } },
        onError = { message(it) },
    )

    fun stopHotspot() { hotspot.stop(); _state.value = _state.value.copy(hotspot = null) }

    /** Connects to a device from its QR code, joining its hotspot first when the code carries one. */
    fun connectQr(text: String) {
        val qr = QrCode.parse(text) ?: return message("That isn't a ClipFx Share code.")
        fun hello() = io {
            var peer: PeerView? = null
            repeat(10) { if (peer == null) { peer = node.addManual("${qr.host}:${qr.port}"); if (peer == null) delay(1000) } }
            if (peer == null) message("Couldn't reach ${qr.name}.") else message("Connected to ${peer!!.name}")
        }
        if (qr.ssid != null) {
            message("Joining ${qr.ssid}…")
            joiner.join(qr.ssid, qr.password ?: "", onJoined = { _state.value = _state.value.copy(joinedSsid = qr.ssid); node.networkChanged(); hello() }, onError = { message(it) })
        } else hello()
    }

    // ------------------------------------------------------------ ShareHost

    override fun incomingRequest(t: TransferView) { onIncoming?.invoke(t) }
    override fun transferFinished(t: TransferView) { onFinished?.invoke(t) }

    override fun fileSaved(location: String) {
        if (!location.startsWith("content://")) MediaScannerConnection.scanFile(ctx, arrayOf(location), null, null)
    }

    @SuppressLint("WakelockTimeout")
    @Suppress("DEPRECATION")
    override fun keepAwake(active: Boolean) {
        if (active) {
            if (wakeLock?.isHeld != true) wakeLock = ctx.getSystemService(PowerManager::class.java)
                .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "clipfx:transfer").apply { acquire() }
            if (wifiLock?.isHeld != true) wifiLock = ctx.getSystemService(WifiManager::class.java)
                .createWifiLock(if (Build.VERSION.SDK_INT >= 29) WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF, "clipfx:transfer")
                .apply { acquire() }
        } else {
            runCatching { wakeLock?.release() }; runCatching { wifiLock?.release() }
        }
    }
}
