package com.clipfx.share

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clipfx.share.core.*
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions

class MainActivity : ComponentActivity() {
    private val permissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { repo.storageChanged() }
    private val hotspotPermission = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { r ->
        if (r.values.all { it }) repo.startHotspot() else repo.message("Hotspot needs the nearby devices permission.")
    }
    private val scanner = registerForActivityResult(ScanContract()) { r -> r.contents?.let { repo.connectQr(it) } }
    private val cameraPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { if (it) launchScanner() }

    val pickFiles = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris -> addUris(uris, "file") }
    val pickMedia = registerForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(500)) { uris -> addUris(uris, "media") }
    val pickFolder = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri -> uri?.let(::addFolder) }
    val pickSaveFolder = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri -> uri?.let { repo.useFolder(it) } }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        ShareService.start(this)
        repo.start()
        val needed = Permissions.startup().filter { !Permissions.granted(this, it) }
        if (needed.isNotEmpty()) permissions.launch(needed.toTypedArray())
        handleShare(intent)
        setContent {
            val state by repo.state.collectAsStateWithLifecycle()
            ShareTheme(state.settings.theme) { Root(this, state) }
        }
    }

    override fun onResume() {
        super.onResume()
        repo.storageChanged()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleShare(intent)
    }

    fun keepScreenOn(on: Boolean) {
        if (on) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    /** "Share → ClipFx Share" from any app. */
    private fun handleShare(intent: Intent?) {
        intent ?: return
        @Suppress("DEPRECATION")
        val uris: List<Uri> = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java) else intent.getParcelableExtra(Intent.EXTRA_STREAM))
            Intent.ACTION_SEND_MULTIPLE -> (if (Build.VERSION.SDK_INT >= 33) intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java) else intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM)) ?: emptyList()
            else -> return
        }
        if (uris.isNotEmpty()) addUris(uris, "file")
        else intent.getStringExtra(Intent.EXTRA_TEXT)?.let { addText(it) }
    }

    fun addUris(uris: List<Uri>, kind: String) {
        val entries = uris.mapNotNull { uri ->
            val p = Pick.describe(this, uri) ?: return@mapNotNull null
            Entry(uri.toString(), p.name, p.size, kind, listOf(SourceItem(ItemInfo(0, p.name, p.size, System.currentTimeMillis(), ItemType.FILE), p.ref)))
        }
        repo.add(entries)
    }

    private fun addFolder(tree: Uri) {
        runCatching { contentResolver.takePersistableUriPermission(tree, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
        repo.io {
            val items = Pick.folder(this@MainActivity, tree, 0)
            val name = items.firstOrNull()?.info?.path ?: "Folder"
            repo.add(listOf(Entry(tree.toString(), name, items.sumOf { it.info.size }, "folder", items)))
        }
    }

    fun addText(text: String) {
        repo.add(listOf(Entry("text-${text.hashCode()}", text.take(60), text.toByteArray().size.toLong(), "text",
            listOf(SourceItem(ItemInfo(0, "Text", text.toByteArray().size.toLong(), System.currentTimeMillis(), ItemType.TEXT, text), null)))))
    }

    fun addApps(apps: List<InstalledApp>) {
        repo.add(apps.map { a ->
            val items = if (a.splits.isEmpty()) listOf(SourceItem(ItemInfo(0, "${a.label}.apk", a.size, 0, ItemType.FILE), a.base))
            else listOf(SourceItem(ItemInfo(0, a.label, 0, 0, ItemType.DIR), null)) +
                (listOf(a.base) + a.splits).map { p -> java.io.File(p).let { f -> SourceItem(ItemInfo(0, "${a.label}/${f.name}", f.length(), 0, ItemType.FILE), p) } }
            Entry("app-${a.packageName}", a.label, a.size, "app", items)
        })
    }

    fun scanQr() {
        if (Permissions.granted(this, android.Manifest.permission.CAMERA)) launchScanner()
        else cameraPermission.launch(android.Manifest.permission.CAMERA)
    }

    private fun launchScanner() = scanner.launch(ScanOptions().setPrompt("").setBeepEnabled(false).setOrientationLocked(true).setDesiredBarcodeFormats(ScanOptions.QR_CODE))

    fun startHotspot() {
        val need = Permissions.hotspot().filter { !Permissions.granted(this, it) }
        if (need.isEmpty()) repo.startHotspot() else hotspotPermission.launch(need.toTypedArray())
    }

    fun openAllFilesSettings() = runCatching { startActivity(Permissions.allFilesIntent(this)) }
}

enum class Tab(val label: String, val icon: ImageVector) {
    Send("Send", Icons.Outlined.SendToMobile),
    Transfers("Transfers", Icons.Outlined.SwapVert),
    Settings("Settings", Icons.Outlined.Tune),
}

@Composable
fun Root(activity: MainActivity, state: UiState) {
    var tab by rememberSaveable { mutableStateOf(Tab.Send) }
    var storageDismissed by rememberSaveable { mutableStateOf(false) }
    val snackbar = remember { SnackbarHostState() }
    val repo = activity.repo
    val waiting = state.transfers.firstOrNull { it.direction == Direction.Receive && it.state == TransferState.Waiting }
    val active = state.transfers.count { !it.state.isFinal && it.state != TransferState.Waiting }

    LaunchedEffect(state.message) {
        state.message?.let { snackbar.showSnackbar(it); repo.message(null) }
    }
    LaunchedEffect(active, state.settings.keepScreenOn) { activity.keepScreenOn(state.settings.keepScreenOn && active > 0) }

    Scaffold(
        snackbarHost = { SnackbarHost(snackbar) },
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.surface, tonalElevation = 0.dp) {
                Tab.entries.forEach { t ->
                    NavigationBarItem(
                        selected = tab == t, onClick = { tab = t },
                        icon = {
                            BadgedBox(badge = { if (t == Tab.Transfers && active > 0) Badge { Text("$active") } }) { Icon(t.icon, null) }
                        },
                        label = { Text(t.label) },
                        colors = NavigationBarItemDefaults.colors(indicatorColor = MaterialTheme.colorScheme.surfaceVariant),
                    )
                }
            }
        },
    ) { pad ->
        Box(Modifier.padding(pad).fillMaxSize()) {
            when (tab) {
                Tab.Send -> SendScreen(activity, state) { tab = Tab.Transfers }
                Tab.Transfers -> TransfersScreen(activity, state)
                Tab.Settings -> SettingsScreen(activity, state)
            }
        }
    }
    if (state.ready && !state.allFiles && !storageDismissed) StoragePrompt(activity) { storageDismissed = true }
    waiting?.let { IncomingDialog(activity, it) }
}
