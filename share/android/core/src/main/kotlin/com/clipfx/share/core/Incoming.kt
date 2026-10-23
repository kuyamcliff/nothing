package com.clipfx.share.core

import kotlinx.serialization.Serializable
import java.io.IOException
import java.util.concurrent.atomic.AtomicLong
import java.util.zip.CRC32

@Serializable
data class IncomingSaved(
    val id: String, val from: DeviceInfo, val fromAddress: String? = null, val items: List<ItemInfo>, val key: String,
    val root: String = "", val have: List<List<List<Long>>> = emptyList(), val finals: List<String?> = emptyList(),
    val skipped: List<Boolean> = emptyList(), val local: List<String>? = null, val state: TransferState,
    val started: Long, val finished: Long? = null, val error: String? = null, val conflict: String = "rename",
)

/** Bytes per second over a sliding window. */
class RateMeter {
    private val samples = ArrayDeque<Pair<Long, Long>>()
    private var sum = 0L

    @Synchronized fun add(n: Long) { val now = System.currentTimeMillis(); samples.addLast(now to n); sum += n; trim(now) }
    private fun trim(now: Long) { while (samples.isNotEmpty() && now - samples.first().first > 2000) sum -= samples.removeFirst().second }

    @get:Synchronized val bytesPerSecond: Double
        get() {
            val now = System.currentTimeMillis(); trim(now)
            if (samples.isEmpty()) return 0.0
            return sum * 1000.0 / maxOf(500L, now - samples.first().first)
        }
}

/** A transfer being received. Data goes into "*.cfpart" files which are renamed when complete. */
class IncomingSession(
    val id: String,
    val from: DeviceInfo,
    @Volatile var fromAddress: String?,
    val items: List<ItemInfo>,
    val key: String,
) {
    private val lock = Any()
    val have = Array(items.size) { RangeSet() }
    private val finals = arrayOfNulls<String>(items.size)
    private val skipped = BooleanArray(items.size)
    private var local: List<String> = items.map { it.path }
    private val writers = LinkedHashMap<Int, PartWriter>(16, 0.75f, true)
    private val live = AtomicLong()
    var target: SaveTarget? = null; private set
    var root: String = ""; private set
    var conflict: String = "rename"; private set
    @Volatile var state: TransferState = TransferState.Waiting; private set
    var started: Long = System.currentTimeMillis(); private set
    var finished: Long? = null; private set
    var error: String? = null; private set
    @Volatile var lastActivity: Long = System.currentTimeMillis(); private set
    val rate = RateMeter()
    val totalBytes: Long = items.filter { it.type == ItemType.FILE }.sumOf { it.size }
    var onChanged: (() -> Unit)? = null

    companion object {
        private const val MAX_OPEN = 64
        /** Benchmarks only: skip the disk. */
        @Volatile var noWrite = false

        fun fromSaved(s: IncomingSaved, targets: SaveTargets): IncomingSession {
            val x = IncomingSession(s.id, s.from, s.fromAddress, s.items, s.key)
            x.root = s.root; x.conflict = s.conflict; x.started = s.started; x.finished = s.finished; x.error = s.error
            if (s.root.isNotEmpty()) x.target = runCatching { targets.forRoot(s.root) }.getOrNull()
            s.have.forEachIndexed { i, r -> if (i < x.have.size) r.forEach { x.have[i].add(it[0], it[1]) } }
            s.finals.forEachIndexed { i, f -> if (i < x.finals.size) x.finals[i] = f }
            s.skipped.forEachIndexed { i, b -> if (i < x.skipped.size) x.skipped[i] = b }
            s.local?.takeIf { it.size == x.items.size }?.let { x.local = it }
            x.live.set(x.done)
            x.state = if (!s.state.isFinal && s.state != TransferState.Waiting) TransferState.Interrupted else s.state
            return x
        }
    }

    fun toSaved(): IncomingSaved = synchronized(lock) {
        IncomingSaved(id, from, fromAddress, items, key, root, have.map { it.toList() }, finals.toList(), skipped.toList(),
            local, state, started, finished, error, conflict)
    }

    val done: Long get() = items.filter { it.type == ItemType.FILE }.sumOf { have[it.i].covered }
    val liveBytes: Long get() = live.get()
    val text: String? get() = items.firstOrNull { it.type == ItemType.TEXT }?.text
    fun localPath(i: Int) = local[i]
    fun finalPath(i: Int) = finals[i]

    fun setState(s: TransferState, err: String? = null) {
        synchronized(lock) {
            if (state == s && err == error) return
            state = s
            if (err != null) error = err
            if (s.isFinal && finished == null) finished = System.currentTimeMillis()
        }
        onChanged?.invoke()
    }

    fun accept(rootPath: String, t: SaveTarget, conflictPolicy: String, organize: Boolean) {
        synchronized(lock) {
            root = rootPath; target = t; conflict = conflictPolicy
            local = items.map { Categories.place(it.path, it.type, organize) }
            if (conflictPolicy == "skip") {
                for (it in items) if (it.type == ItemType.FILE && t.exists(local[it.i])) {
                    skipped[it.i] = true; have[it.i].add(0, it.size); finals[it.i] = local[it.i]
                }
                live.set(done)
            }
            state = TransferState.Active
        }
        onChanged?.invoke()
    }

    fun haveJson() = haveJson(items, have)

    private fun writer(it: ItemInfo): PartWriter = synchronized(lock) {
        writers[it.i]?.let { return it }
        val t = target ?: throw IOException("No save location.")
        val w = t.openPart(local[it.i], it.size)
        writers[it.i] = w
        if (writers.size > MAX_OPEN) {
            val oldest = writers.keys.first()
            writers.remove(oldest)?.close()
        }
        w
    }

    /** Reads one part from the connection into its file. False when the checksum doesn't match. */
    fun receivePart(conn: Conn, index: Int, off: Long, len: Long, write: Boolean, buf: ByteArray): Boolean {
        if (index !in items.indices || items[index].type != ItemType.FILE) throw IOException("Bad item.")
        val it = items[index]
        if (off < 0 || len < 0 || off + len > it.size) throw IOException("Bad range.")
        val w = if (write) writer(it) else null
        val skipDisk = noWrite
        val crc = CRC32()
        var pos = 0L
        while (pos < len) {
            val n = minOf(buf.size.toLong(), len - pos).toInt()
            conn.readFully(buf, 0, n)
            crc.update(buf, 0, n)
            if (!skipDisk) w?.write(buf, 0, n, off + pos)
            if (write) { live.addAndGet(n.toLong()); rate.add(n.toLong()) }
            pos += n
            lastActivity = System.currentTimeMillis()
        }
        val expected = conn.readU32()
        if (!write) return true
        if (expected != 0L && expected != crc.value) { live.addAndGet(-len); return false }
        have[index].add(off, off + len)
        return true
    }

    /** Renames finished parts into place. Returns indexes still missing data. */
    fun finish(onSaved: (String) -> Unit = {}): List<Int> {
        val missing = items.filter { it.type == ItemType.FILE && !have[it.i].isComplete(it.size) }.map { it.i }
        if (missing.isNotEmpty()) return missing
        closeFiles()
        val t = target ?: throw IOException("No save location.")
        for (it in items) when {
            it.type == ItemType.DIR -> t.makeDir(local[it.i])
            it.type == ItemType.FILE && !skipped[it.i] && finals[it.i] == null -> {
                finals[it.i] = t.finish(local[it.i], conflict, it.mtime, it.size)
                onSaved(finals[it.i]!!)
            }
        }
        setState(TransferState.Completed)
        return emptyList()
    }

    fun closeFiles() = synchronized(lock) { writers.values.forEach { runCatching { it.close() } }; writers.clear() }

    fun discard() {
        closeFiles()
        val t = target ?: return
        for (it in items) if (it.type == ItemType.FILE && finals[it.i] == null) runCatching { t.discard(local[it.i]) }
    }

    /** Folder that holds everything received, relative to the root ("" for the root itself). */
    fun openRelative(): String {
        val dirs = items.filter { it.type != ItemType.TEXT }.map {
            val seg = PathSafety.segments(local[it.i]); if (it.type == ItemType.DIR) seg else seg.dropLast(1)
        }
        if (dirs.isEmpty()) return ""
        val common = ArrayList<String>()
        var d = 0
        while (dirs.all { it.size > d } && dirs.map { it[d] }.distinct().size == 1) { common.add(dirs[0][d]); d++ }
        return common.joinToString("/")
    }
}
