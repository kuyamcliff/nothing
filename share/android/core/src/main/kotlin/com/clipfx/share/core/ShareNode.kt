package com.clipfx.share.core

import kotlinx.serialization.json.*
import java.io.File
import java.io.IOException
import java.net.*
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

/** Platform integration. */
interface ShareHost {
    fun incomingRequest(t: TransferView) {}
    fun transferFinished(t: TransferView) {}
    fun keepAwake(active: Boolean) {}
    fun fileSaved(location: String) {}
    val socketPreparer: SocketPreparer? get() = null
}

/** Everything ClipFx Share does on one device. Mirrors the Windows ShareNode. */
class ShareNode(
    private val dataDir: File,
    val platform: String,
    private val defaultSaveRoot: String,
    private val targets: SaveTargets = FileTargets,
    private val opener: SourceOpener = FileOpener,
    private val host: ShareHost = object : ShareHost {},
) {
    private data class Peer(val dev: DeviceInfo, val addr: InetAddress, val seen: Long, val manual: Boolean)
    private class Decision { val latch = java.util.concurrent.CountDownLatch(1); @Volatile var accept = false; @Volatile var trust = false }

    private val inSessions = ConcurrentHashMap<String, IncomingSession>()
    private val outSessions = ConcurrentHashMap<String, OutgoingSession>()
    private val decisions = ConcurrentHashMap<String, Decision>()
    private val peers = ConcurrentHashMap<String, Peer>()
    private val dirty = ConcurrentHashMap.newKeySet<String>()
    private val pool = Executors.newCachedThreadPool { r -> Thread(r, "cfs-conn").apply { isDaemon = true } }
    private val versionCounter = AtomicLong()
    private var server: ServerSocket? = null
    private var discovery: Discovery? = null
    @Volatile private var running = false
    @Volatile private var awake = false
    @Volatile private var settingsValue: ShareSettings
    @Volatile var saveRoot: String = defaultSaveRoot; private set
    var onChanged: (() -> Unit)? = null
    var port = 0; private set

    private val settingsFile = File(dataDir, "settings.json")
    private val sessionsDir = File(dataDir, "transfers")

    companion object {
        const val DECISION_TIMEOUT_MS = 2 * 60 * 1000L
        private const val PEER_EXPIRY_MS = 12_000L
        private const val FAT32_MAX = 4L * 1024 * 1024 * 1024 - 1

        fun saveJson(f: File, text: String) {
            f.parentFile?.mkdirs()
            val tmp = File(f.path + ".tmp")
            tmp.outputStream().use { it.write(text.toByteArray()); it.fd.sync() }
            if (!tmp.renameTo(f)) { f.delete(); tmp.renameTo(f) }
        }
    }

    init {
        dataDir.mkdirs()
        settingsValue = runCatching { json.decodeFromString<ShareSettings>(settingsFile.readText()) }.getOrElse { ShareSettings() }
        File(dataDir, "save-root.txt").takeIf { it.exists() }?.readText()?.takeIf { it.isNotBlank() }?.let { saveRoot = it }
        saveJson(settingsFile, json.encodeToString(ShareSettings.serializer(), settingsValue))
    }

    val settings: ShareSettings get() = settingsValue
    val version: Long get() = versionCounter.get()

    fun updateSettings(change: (ShareSettings) -> ShareSettings) {
        synchronized(this) {
            var s = change(settingsValue)
            s = s.copy(
                streams = s.streams.coerceIn(1, 16), chunkMB = s.chunkMB.coerceIn(1, 64),
                conflict = if (s.conflict in setOf("rename", "overwrite", "skip")) s.conflict else "rename",
                deviceName = s.deviceName.trim().take(40).ifEmpty { "Android" },
            )
            settingsValue = s
            saveJson(settingsFile, json.encodeToString(ShareSettings.serializer(), s))
        }
        discovery?.announce(false)
        bump()
    }

    fun changeSaveRoot(root: String) {
        saveRoot = root
        saveJson(File(dataDir, "save-root.txt"), root)
        bump()
    }

    val self: DeviceInfo get() = DeviceInfo(settingsValue.deviceId, settingsValue.deviceName, platform, port)

    private fun bump() {
        versionCounter.incrementAndGet()
        onChanged?.invoke()
        val active = inSessions.values.any { it.state == TransferState.Active } ||
            outSessions.values.any { it.state == TransferState.Active || it.state == TransferState.Connecting || it.state == TransferState.Reconnecting }
        if (active != awake) { awake = active; runCatching { host.keepAwake(active) } }
    }

    // ------------------------------------------------------------ lifecycle

    fun start(withDiscovery: Boolean = true) {
        loadSessions()
        for (p in listOf(settingsValue.port, 0)) {
            try {
                val s = ServerSocket()
                s.reuseAddress = true
                s.receiveBufferSize = Conn.SOCKET_BUFFER
                s.bind(InetSocketAddress(p), 256)
                server = s; port = s.localPort
                break
            } catch (e: IOException) { server = null }
        }
        val srv = server ?: throw IOException("Can't open a network port.")
        running = true
        Thread({
            while (running) {
                val sock = try { srv.accept() } catch (e: Exception) { if (!running) return@Thread else continue }
                pool.execute { handle(sock) }
            }
        }, "cfs-accept").apply { isDaemon = true; start() }
        Thread(::housekeeping, "cfs-housekeeping").apply { isDaemon = true; start() }
        if (withDiscovery) {
            discovery = Discovery({ self }, { settingsValue.discoverable }).also {
                it.onSeen = ::onSeen
                it.start()
            }
        }
    }

    fun stop() {
        running = false
        discovery?.stop()
        runCatching { server?.close() }
        outSessions.values.filter { !it.state.isFinal }.forEach { it.pause(); persist(it) }
        inSessions.values.forEach { it.closeFiles(); persist(it) }
        pool.shutdownNow()
    }

    /** Call when the network changes (Wi-Fi joined, hotspot started). */
    fun networkChanged() { discovery?.joinGroups(); discovery?.announce(true) }

    private fun loadSessions() {
        sessionsDir.listFiles()?.forEach { f ->
            runCatching {
                if (f.name.startsWith("in-") && f.name.endsWith(".json"))
                    track(IncomingSession.fromSaved(json.decodeFromString<IncomingSaved>(f.readText()), targets))
                else if (f.name.startsWith("out-") && f.name.endsWith(".json"))
                    track(OutgoingSession.fromSaved(json.decodeFromString<OutgoingSaved>(f.readText()), { self }, ::resolve, ::sendOptions, opener, host.socketPreparer))
            }
        }
    }

    private fun track(s: IncomingSession) { s.onChanged = { dirty.add(s.id); bump() }; inSessions[s.id] = s }

    private fun track(s: OutgoingSession) {
        s.onChanged = {
            persist(s); bump()
            if (s.state == TransferState.Completed || s.state == TransferState.Failed) runCatching { host.transferFinished(view(s)) }
        }
        outSessions[s.id] = s
    }

    private fun persist(s: IncomingSession) = runCatching { saveJson(File(sessionsDir, "in-${s.id}.json"), json.encodeToString(IncomingSaved.serializer(), s.toSaved())) }
    private fun persist(s: OutgoingSession) = runCatching { saveJson(File(sessionsDir, "out-${s.id}.json"), json.encodeToString(OutgoingSaved.serializer(), s.toSaved())) }

    private fun housekeeping() {
        var tick = 0
        while (running) {
            Thread.sleep(1000); tick++
            dirty.toList().forEach { id -> dirty.remove(id); inSessions[id]?.let(::persist) }
            if (tick % 3 == 0) {
                inSessions.values.filter { it.state == TransferState.Active }.forEach(::persist)
                outSessions.values.filter { it.state == TransferState.Active }.forEach(::persist)
            }
            inSessions.values.filter { it.state == TransferState.Active && System.currentTimeMillis() - it.lastActivity > 20_000 }
                .forEach { it.setState(TransferState.Interrupted, "Waiting for the sender.") }
            bump()
        }
    }

    // ------------------------------------------------------------ peers

    private fun onSeen(dev: DeviceInfo, addr: InetAddress) {
        val isNew = !peers.containsKey(dev.id)
        peers.compute(dev.id) { _, old -> Peer(dev, addr, System.currentTimeMillis(), old?.manual ?: false) }
        if (isNew) bump()
    }

    private fun resolve(id: String): InetSocketAddress? = peers[id]?.let { InetSocketAddress(it.addr, it.dev.port) }

    fun peers(): List<PeerView> {
        val trusted = settingsValue.trustedDevices
        val now = System.currentTimeMillis()
        return peers.values.map { PeerView(it.dev.id, it.dev.name, it.dev.platform, it.addr.hostAddress ?: "", it.dev.port, now - it.seen < PEER_EXPIRY_MS, it.dev.id in trusted, it.manual, it.seen) }
            .sortedWith(compareByDescending<PeerView> { it.online }.thenBy { it.name.lowercase() })
    }

    /** Adds a device by address ("ip" or "ip:port"). Blocking. */
    fun addManual(address: String): PeerView? {
        var host = address.trim(); var p = Proto.TCP_PORT
        val colon = host.lastIndexOf(':')
        if (colon > 0 && host.count { it == ':' } == 1) { p = host.substring(colon + 1).toIntOrNull() ?: p; host = host.substring(0, colon) }
        val ip = runCatching { InetAddress.getByName(host) }.getOrNull() ?: return null
        val dev = Discovery.hello(ip, p, 3000, this.host.socketPreparer) ?: return null
        if (dev.id == settingsValue.deviceId) return null
        peers[dev.id] = Peer(dev, ip, System.currentTimeMillis(), true)
        bump()
        return peers().first { it.id == dev.id }
    }

    fun forgetPeer(id: String) { peers.remove(id); bump() }
    fun refresh() = discovery?.announce(true)
    fun scan() = discovery?.scan(Proto.TCP_PORT, host.socketPreparer)

    fun setTrusted(id: String, trusted: Boolean) = updateSettings { s -> s.copy(trustedDevices = s.trustedDevices.filter { it != id } + if (trusted) listOf(id) else emptyList()) }

    // ------------------------------------------------------------ sending

    private fun sendOptions() = settingsValue.let { SendOptions(it.streams, it.chunkMB * 1024 * 1024, it.verify) }

    fun send(deviceIds: Collection<String>, items: List<SourceItem>): List<String> {
        require(items.isNotEmpty()) { "Nothing to send." }
        val ids = ArrayList<String>()
        for (id in deviceIds.distinct()) {
            val p = peers[id] ?: continue
            val s = OutgoingSession(newId(), p.dev, InetSocketAddress(p.addr, p.dev.port), items, { self }, ::resolve, ::sendOptions, opener, host.socketPreparer)
            track(s); persist(s); s.start(); ids.add(s.id)
        }
        bump()
        return ids
    }

    private fun newId() = java.util.UUID.randomUUID().toString().replace("-", "")

    // ------------------------------------------------------------ control

    fun pause(id: String): Boolean {
        outSessions[id]?.let { it.pause(); return true }
        inSessions[id]?.let { if (it.state == TransferState.Active || it.state == TransferState.Interrupted) { it.setState(TransferState.Paused); return true } }
        return false
    }

    fun resume(id: String): Boolean {
        outSessions[id]?.let { if (it.state == TransferState.Failed || it.state == TransferState.Interrupted) it.retry() else it.resume(); return true }
        inSessions[id]?.let { if (it.state == TransferState.Paused) { it.setState(TransferState.Active); return true } }
        return false
    }

    fun cancel(id: String): Boolean {
        outSessions[id]?.let { it.cancel(); return true }
        inSessions[id]?.let { s ->
            decisions[id]?.let { it.accept = false; it.latch.countDown(); return true }
            if (s.state.isFinal) return false
            s.setState(TransferState.Cancelled, "Cancelled."); s.discard(); persist(s); return true
        }
        return false
    }

    fun accept(id: String, trust: Boolean): Boolean = decisions[id]?.let { it.accept = true; it.trust = trust; it.latch.countDown(); true } ?: false
    fun decline(id: String): Boolean = decisions[id]?.let { it.accept = false; it.latch.countDown(); true } ?: false

    fun remove(id: String): Boolean {
        outSessions[id]?.let { o ->
            if (!o.state.isFinal && o.state != TransferState.Interrupted && o.state != TransferState.Paused) return false
            if (!o.state.isFinal) o.cancel()
            outSessions.remove(id); File(sessionsDir, "out-$id.json").delete(); bump(); return true
        }
        inSessions[id]?.let { i ->
            if (!i.state.isFinal && i.state != TransferState.Interrupted && i.state != TransferState.Paused) return false
            if (!i.state.isFinal) i.discard()
            inSessions.remove(id); File(sessionsDir, "in-$id.json").delete(); bump(); return true
        }
        return false
    }

    fun clearHistory() {
        outSessions.values.filter { it.state.isFinal }.forEach { outSessions.remove(it.id); File(sessionsDir, "out-${it.id}.json").delete() }
        inSessions.values.filter { it.state.isFinal }.forEach { inSessions.remove(it.id); File(sessionsDir, "in-${it.id}.json").delete() }
        bump()
    }

    // ------------------------------------------------------------ views

    private fun peerOf(d: DeviceInfo): PeerView {
        val trusted = d.id in settingsValue.trustedDevices
        val p = peers[d.id] ?: return PeerView(d.id, d.name, d.platform, "", d.port, false, trusted, false, 0)
        return PeerView(d.id, p.dev.name, p.dev.platform, p.addr.hostAddress ?: "", p.dev.port, System.currentTimeMillis() - p.seen < PEER_EXPIRY_MS, trusted, p.manual, p.seen)
    }

    private fun titleOf(items: List<ItemInfo>): String {
        val top = items.filter { !it.path.contains('/') }.ifEmpty { items.take(1) }
        val first = if (top[0].type == ItemType.TEXT) "Text" else top[0].path
        return if (top.size == 1) first else "$first and ${top.size - 1} more"
    }

    private fun previewOf(items: List<ItemInfo>) = items.filter { !it.path.contains('/') }.take(8)
        .map { when (it.type) { ItemType.DIR -> it.path + "/"; ItemType.TEXT -> "Text"; else -> it.path } }

    private fun eta(total: Long, done: Long, rate: Double) = if (rate > 1 && done < total) (total - done) / rate else null

    fun view(s: OutgoingSession): TransferView {
        val st = s.state
        val done = if (st == TransferState.Completed) s.totalBytes else s.liveBytes
        val rate = if (st == TransferState.Active) s.rate.bytesPerSecond else 0.0
        return TransferView(s.id, Direction.Send, peerOf(s.target), st, s.error, titleOf(s.items),
            s.items.count { it.type == ItemType.FILE }, s.items.count { it.type == ItemType.DIR }, s.totalBytes, done, rate, eta(s.totalBytes, done, rate),
            if (st == TransferState.Active) s.currentFile else null, s.started, s.finished, null,
            s.items.firstOrNull { it.type == ItemType.TEXT }?.text, null, previewOf(s.items),
            canPause = st in setOf(TransferState.Active, TransferState.Connecting, TransferState.Reconnecting, TransferState.Waiting, TransferState.PausedByPeer),
            canResume = st in setOf(TransferState.Paused, TransferState.Interrupted, TransferState.Failed),
            canCancel = !st.isFinal, canRetry = st == TransferState.Failed || st == TransferState.Declined)
    }

    fun view(s: IncomingSession): TransferView {
        val st = s.state
        val done = if (st == TransferState.Completed) s.totalBytes else minOf(s.totalBytes, s.liveBytes)
        val rate = if (st == TransferState.Active) s.rate.bytesPerSecond else 0.0
        val root = s.root.ifEmpty { saveRoot }
        val openPath = if (st == TransferState.Completed) listOf(root, s.openRelative()).filter { it.isNotEmpty() }.joinToString("/") else null
        return TransferView(s.id, Direction.Receive, peerOf(s.from), st, s.error, titleOf(s.items),
            s.items.count { it.type == ItemType.FILE }, s.items.count { it.type == ItemType.DIR }, s.totalBytes, done, rate, eta(s.totalBytes, done, rate),
            null, s.started, s.finished, openPath, s.text, warningFor(s, root), previewOf(s.items),
            canPause = st == TransferState.Active, canResume = st == TransferState.Paused, canCancel = !st.isFinal, canRetry = false)
    }

    private fun warningFor(s: IncomingSession, root: String): String? {
        if (s.state.isFinal) return null
        val t = s.target ?: runCatching { targets.forRoot(root) }.getOrNull() ?: return null
        val largest = s.items.filter { it.type == ItemType.FILE }.maxOfOrNull { it.size } ?: 0
        t.maxFileSize()?.let { if (largest > it) return "The save location can't hold files over 4 GB (FAT32)." }
        t.freeBytes()?.let { if (it < s.totalBytes - s.done) return "Not enough free space." }
        return null
    }

    fun transfers(): List<TransferView> = (outSessions.values.map(::view) + inSessions.values.map(::view)).sortedByDescending { it.started }
    fun transfer(id: String): TransferView? = outSessions[id]?.let(::view) ?: inSessions[id]?.let(::view)
    fun addresses(): List<String> = Discovery.localIPv4().map { it.first.hostAddress ?: "" }.distinct()

    // ------------------------------------------------------------ server

    private fun handle(sock: Socket) {
        val c = Conn(sock)
        val buf by lazy { ByteArray(1024 * 1024) }
        try {
            while (running) {
                val msg = c.readMessage(10 * 60 * 1000) ?: return
                val reply: JsonObject = when (msg.str("op")) {
                    "hello" -> ok("device" to self.toJson())
                    "offer" -> handleOffer(msg, c)
                    "data" -> handleData(msg, c, buf)
                    "status" -> withSession(msg) { s -> ok("state" to JsonPrimitive(wireState(s.state)), "have" to s.haveJson()) }
                    "pause" -> withSession(msg) { s -> if (s.state == TransferState.Active || s.state == TransferState.Interrupted) s.setState(TransferState.PausedByPeer); ok() }
                    "resume" -> withSession(msg) { s -> if (s.state == TransferState.PausedByPeer) s.setState(TransferState.Active); ok() }
                    "cancel" -> withSession(msg) { s ->
                        if (!s.state.isFinal) { s.setState(TransferState.Cancelled, "Cancelled by the sender."); s.discard(); persist(s) }
                        ok()
                    }
                    "done" -> withSession(msg, ::handleDone)
                    else -> err("unsupported")
                }
                c.writeMessage(reply)
            }
        } catch (_: Exception) {
        } finally { c.close() }
    }

    private fun wireState(s: TransferState) = when (s) {
        TransferState.Paused -> "paused"
        TransferState.Cancelled, TransferState.Declined -> "cancelled"
        TransferState.Completed -> "done"
        else -> "active"
    }

    private fun withSession(msg: JsonObject, f: (IncomingSession) -> JsonObject): JsonObject {
        val s = inSessions[msg.str("session") ?: ""] ?: return err("unknown")
        if (s.key != msg.str("key")) return err("forbidden")
        return f(s)
    }

    private fun handleOffer(msg: JsonObject, c: Conn): JsonObject {
        val id = msg.str("session")
        val from = runCatching { msg["from"]!!.parse<DeviceInfo>() }.getOrNull()
        var items = runCatching { msg["items"]!!.parse<List<ItemInfo>>() }.getOrNull()
        if (id.isNullOrEmpty() || id.length > 64 || from == null || items.isNullOrEmpty()) return err("bad offer")
        items = items.mapIndexed { k, it -> it.copy(i = k) }
        c.remote?.let { a ->
            val isNew = !peers.containsKey(from.id)
            peers.compute(from.id) { _, o -> Peer(from, a, System.currentTimeMillis(), o?.manual ?: false) }
            if (isNew) bump()
        }

        inSessions[id]?.let { known ->
            if (known.from.id != from.id) return err("forbidden")
            return when (known.state) {
                TransferState.Completed -> ok("accepted" to JsonPrimitive(true), "key" to JsonPrimitive(known.key), "have" to known.haveJson())
                TransferState.Cancelled -> ok("accepted" to JsonPrimitive(false), "reason" to JsonPrimitive("cancelled"))
                TransferState.Declined -> ok("accepted" to JsonPrimitive(false), "reason" to JsonPrimitive("declined"))
                TransferState.Waiting -> err("busy")
                else -> {
                    known.fromAddress = c.remote?.hostAddress
                    if (known.state == TransferState.Interrupted || known.state == TransferState.PausedByPeer) known.setState(TransferState.Active)
                    ok("accepted" to JsonPrimitive(true), "key" to JsonPrimitive(known.key), "have" to known.haveJson())
                }
            }
        }

        val key = ByteArray(16).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02X".format(it) }
        val s = IncomingSession(id, from, c.remote?.hostAddress, items, key)
        track(s); bump()
        val st = settingsValue
        val trusted = from.id in st.trustedDevices
        val warning = warningFor(s, saveRoot)
        if (warning != null && (!st.askBeforeReceive || trusted)) {
            s.setState(TransferState.Declined, warning); persist(s)
            return ok("accepted" to JsonPrimitive(false), "reason" to JsonPrimitive(if (warning.contains("FAT32")) "fat32" else "space"))
        }

        if (st.askBeforeReceive && !trusted) {
            val d = Decision()
            decisions[id] = d
            runCatching { host.incomingRequest(view(s)) }
            val deadline = System.currentTimeMillis() + DECISION_TIMEOUT_MS
            var timedOut = true
            try {
                while (System.currentTimeMillis() < deadline) {
                    if (d.latch.await(5, TimeUnit.SECONDS)) { timedOut = false; break }
                    c.keepAlive()
                }
            } catch (e: Exception) {
                decisions.remove(id)
                s.setState(TransferState.Cancelled, "The sender cancelled."); persist(s)
                throw e
            }
            decisions.remove(id)
            if (d.trust) setTrusted(from.id, true)
            if (!d.accept) {
                s.setState(TransferState.Declined, if (timedOut) "No answer." else null); persist(s)
                return ok("accepted" to JsonPrimitive(false), "reason" to JsonPrimitive(if (timedOut) "timeout" else "declined"))
            }
        }

        val root = saveRoot
        val target = try { targets.forRoot(root) } catch (e: Exception) {
            s.setState(TransferState.Failed, "Can't use the save location: ${e.message}")
            return ok("accepted" to JsonPrimitive(false), "reason" to JsonPrimitive("declined"))
        }
        s.accept(root, target, st.conflict, st.organizeByType)
        if (s.items.none { it.type == ItemType.FILE }) s.finish { host.fileSaved(it) }
        persist(s)
        return ok("accepted" to JsonPrimitive(true), "key" to JsonPrimitive(key), "have" to s.haveJson())
    }

    private fun handleData(msg: JsonObject, c: Conn, buf: ByteArray): JsonObject {
        val parts = (msg["parts"] as? JsonArray)?.map { val o = it.jsonObject; Triple(o.long("i").toInt(), o.long("off"), o.long("len")) }
            ?: throw IOException("Bad data request.")
        val s = inSessions[msg.str("session") ?: ""]
        var error: String? = when {
            s == null -> "unknown"
            s.key != msg.str("key") -> "forbidden"
            s.state == TransferState.Paused -> "paused"
            s.state == TransferState.Cancelled || s.state == TransferState.Declined || s.state == TransferState.Failed -> "cancelled"
            else -> null
        }
        if (error == null && (s!!.state == TransferState.Interrupted || s.state == TransferState.PausedByPeer)) s.setState(TransferState.Active)
        val write = error == null
        for ((i, off, len) in parts) {
            if (s == null || i !in s.items.indices) { drain(c, len, buf); continue }
            if (!s.receivePart(c, i, off, len, write, buf)) if (error == null) error = "crc"
        }
        if (s != null && write) dirty.add(s.id)
        return if (error == null) ok() else err(error)
    }

    private fun drain(c: Conn, len: Long, buf: ByteArray) {
        var left = len
        while (left > 0) { val n = minOf(buf.size.toLong(), left).toInt(); c.readFully(buf, 0, n); left -= n }
        c.readU32()
    }

    private fun handleDone(s: IncomingSession): JsonObject {
        if (s.state == TransferState.Completed) return ok()
        if (s.state == TransferState.Cancelled || s.state == TransferState.Declined) return err("cancelled")
        return try {
            if (s.finish { host.fileSaved(it) }.isNotEmpty()) return err("incomplete")
            persist(s)
            runCatching { host.transferFinished(view(s)) }
            ok()
        } catch (e: Exception) {
            s.setState(TransferState.Failed, e.message); persist(s)
            err(e.message ?: "error")
        }
    }

    // ------------------------------------------------------------ helpers for plain files

    fun collect(paths: List<String>, text: String? = null): List<SourceItem> {
        val out = ArrayList<SourceItem>()
        var n = 0
        for (raw in paths) {
            val f = File(raw)
            if (f.isFile) out.add(SourceItem(ItemInfo(n++, f.name, f.length(), f.lastModified(), ItemType.FILE), f.absolutePath))
            else if (f.isDirectory) {
                val rootName = f.name.ifEmpty { "Folder" }
                out.add(SourceItem(ItemInfo(n++, rootName, 0, 0, ItemType.DIR), null))
                f.walkTopDown().filter { it != f }.forEach { x ->
                    val rel = rootName + "/" + x.relativeTo(f).invariantSeparatorsPath
                    if (x.isDirectory) out.add(SourceItem(ItemInfo(n++, rel, 0, 0, ItemType.DIR), null))
                    else out.add(SourceItem(ItemInfo(n++, rel, x.length(), x.lastModified(), ItemType.FILE), x.absolutePath))
                }
            }
        }
        if (!text.isNullOrEmpty()) out.add(SourceItem(ItemInfo(n++, "Text", text.toByteArray().size.toLong(), System.currentTimeMillis(), ItemType.TEXT, text), null))
        return out
    }
}
