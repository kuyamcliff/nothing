package com.clipfx.share.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.*
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import java.util.zip.CRC32

data class SourceItem(val info: ItemInfo, val ref: String?)
data class SendOptions(val streams: Int, val chunkBytes: Int, val verify: Boolean)

@Serializable
data class OutgoingSaved(
    val id: String, val target: DeviceInfo, val address: String? = null, val port: Int = 0,
    val items: List<ItemInfo>, val refs: List<String?>, val acked: List<List<List<Long>>> = emptyList(),
    val state: TransferState, val started: Long, val finished: Long? = null, val error: String? = null,
)

class SourceError(message: String) : Exception(message)
private class PeerPaused : Exception()
private class PeerCancelled : Exception()

/** A run of the sender; cancelling it closes its sockets so blocked calls return at once. */
private class Run {
    @Volatile var cancelled = false
    val conns = CopyOnWriteArrayList<Conn>()
    fun track(c: Conn): Conn { conns.add(c); if (cancelled) c.close(); return c }
    fun cancel() { cancelled = true; conns.forEach { runCatching { it.close() } } }
}

/** A transfer to one device. Re-offers and resumes after disconnects. */
class OutgoingSession(
    val id: String,
    val target: DeviceInfo,
    @Volatile var lastEndpoint: InetSocketAddress?,
    sources: List<SourceItem>,
    private val self: () -> DeviceInfo,
    private val resolve: (String) -> InetSocketAddress?,
    private val options: () -> SendOptions,
    private val opener: SourceOpener,
    private val preparer: SocketPreparer?,
) {
    companion object {
        const val SMALL_FILE = 1024 * 1024
        private const val MAX_PARTS = 128
        private const val GIVE_UP_MS = 30 * 60 * 1000L

        fun fromSaved(s: OutgoingSaved, self: () -> DeviceInfo, resolve: (String) -> InetSocketAddress?, options: () -> SendOptions,
                      opener: SourceOpener, preparer: SocketPreparer?): OutgoingSession {
            val ep = s.address?.let { runCatching { InetSocketAddress(InetAddress.getByName(it), s.port) }.getOrNull() }
            val o = OutgoingSession(s.id, s.target, ep, s.items.mapIndexed { k, it -> SourceItem(it, s.refs.getOrNull(k)) }, self, resolve, options, opener, preparer)
            o.started = s.started; o.finished = s.finished; o.error = s.error
            s.acked.forEachIndexed { i, r -> if (i < o.acked.size) r.forEach { o.acked[i].add(it[0], it[1]) } }
            o.live.set(o.done)
            o.state = if (s.state.isFinal) s.state else TransferState.Interrupted
            return o
        }
    }

    private class Part(val i: Int, val off: Long, val len: Long)
    private class Work(val parts: List<Part>)

    val items: List<ItemInfo> = sources.map { it.info }
    private val refs: List<String?> = sources.map { it.ref }
    val acked = Array(items.size) { RangeSet() }
    private val lock = Any()
    private val live = AtomicLong()
    private var run: Run? = null
    private var thread: Thread? = null
    @Volatile private var key: String? = null
    @Volatile var state = TransferState.Connecting; private set
    var started = System.currentTimeMillis(); private set
    var finished: Long? = null; private set
    var error: String? = null; private set
    @Volatile var currentFile: String? = null; private set
    val rate = RateMeter()
    val totalBytes = items.filter { it.type == ItemType.FILE }.sumOf { it.size }
    var onChanged: (() -> Unit)? = null

    val done: Long get() = items.filter { it.type == ItemType.FILE }.sumOf { acked[it.i].covered }
    val liveBytes: Long get() = minOf(totalBytes, live.get())

    fun toSaved() = synchronized(lock) {
        OutgoingSaved(id, target, lastEndpoint?.address?.hostAddress, lastEndpoint?.port ?: 0, items, refs,
            acked.map { it.toList() }, state, started, finished, error)
    }

    private fun setState(s: TransferState, err: String? = null) {
        synchronized(lock) {
            if (state == s && error == err) return
            state = s; error = if (s == TransferState.Completed) null else err
            if (s.isFinal && finished == null) finished = System.currentTimeMillis()
        }
        onChanged?.invoke()
    }

    fun start() = synchronized(lock) {
        if (state.isFinal) return
        if (run?.cancelled == false && thread?.isAlive == true) return
        val r = Run(); run = r
        val previous = thread
        thread = Thread({
            previous?.join() // let a paused run wind down first
            runLoop(r)
        }, "cfs-send-$id").apply { isDaemon = true; start() }
    }

    fun pause() {
        if (state.isFinal) return
        run?.cancel()
        setState(TransferState.Paused)
        notifyPeer("pause")
    }

    fun resume() {
        if (state.isFinal) return
        setState(TransferState.Connecting)
        start()
    }

    fun cancel() {
        if (state.isFinal) return
        run?.cancel()
        setState(TransferState.Cancelled)
        notifyPeer("cancel")
    }

    fun retry() {
        synchronized(lock) {
            if (state != TransferState.Failed && state != TransferState.Interrupted && state != TransferState.Declined) return
            state = TransferState.Connecting; finished = null; error = null
        }
        onChanged?.invoke()
        start()
    }

    private fun notifyPeer(op: String) {
        val k = key ?: return
        Thread {
            runCatching {
                val ep = endpoint() ?: return@Thread
                Conn.connect(ep.address, ep.port, 3000, preparer).use { c ->
                    c.writeMessage(buildJsonObject { put("op", op); put("session", id); put("key", k) })
                    c.readMessage(4000)
                }
            }
        }.apply { isDaemon = true; start() }
    }

    private fun endpoint(): InetSocketAddress? {
        val ep = resolve(target.id) ?: lastEndpoint
        if (ep != null) lastEndpoint = ep
        return ep
    }

    private fun friendly(e: Throwable): String = when (e) {
        is TimeoutError -> "No response from the other device."
        is java.net.ConnectException -> "The other device isn't accepting connections."
        is java.net.NoRouteToHostException, is java.net.SocketException -> "Network unavailable."
        else -> e.message ?: e.javaClass.simpleName
    }

    private fun runLoop(r: Run) {
        var attempt = 0
        var since = System.currentTimeMillis()
        while (!r.cancelled) {
            try {
                val ep = endpoint() ?: throw IOException("${target.name} isn't on this network.")
                if (state != TransferState.Waiting) setState(if (attempt == 0) TransferState.Connecting else TransferState.Reconnecting, if (attempt == 0) null else error)
                if (!offer(ep, r)) return
                attempt = 0; since = System.currentTimeMillis()
                setState(TransferState.Active)
                sendAll(ep, r)
                if (finish(ep, r)) { currentFile = null; setState(TransferState.Completed); return }
            } catch (e: PeerCancelled) {
                setState(TransferState.Cancelled, "Cancelled by the receiver."); return
            } catch (e: SourceError) {
                setState(TransferState.Failed, e.message); return
            } catch (e: Throwable) {
                if (r.cancelled) return
                attempt++
                if (System.currentTimeMillis() - since > GIVE_UP_MS) { setState(TransferState.Interrupted, "Connection lost."); return }
                setState(TransferState.Reconnecting, friendly(e))
                val delay = minOf(10_000L, (500 * Math.pow(2.0, minOf(attempt, 5).toDouble())).toLong())
                var waited = 0L
                while (waited < delay && !r.cancelled) { Thread.sleep(100); waited += 100 }
            }
        }
    }

    /** Offers or re-offers. False when declined. */
    private fun offer(ep: InetSocketAddress, r: Run): Boolean {
        r.track(Conn.connect(ep.address, ep.port, 5000, preparer)).use { c ->
            c.writeMessage(buildJsonObject {
                put("op", "offer"); put("v", Proto.VERSION); put("session", id)
                put("from", self().toJson()); put("items", items.toJson()); put("totalSize", totalBytes)
            })
            if (key == null) setState(TransferState.Waiting)
            val reply = c.readMessage(30_000) ?: throw IOException("Connection closed.")
            if (!reply.bool("ok")) throw IOException(reply.str("error") ?: "Offer failed.")
            if (!reply.bool("accepted")) {
                val reason = reply.str("reason")
                if (reason == "cancelled") throw PeerCancelled()
                setState(TransferState.Declined, when (reason) {
                    "space" -> "Not enough space on the other device."
                    "fat32" -> "The other device's storage can't hold files over 4 GB (FAT32)."
                    "timeout" -> "No answer from the other device."
                    "busy" -> "The other device is busy."
                    else -> "Declined."
                })
                return false
            }
            key = reply.str("key")
            (reply["have"] as? JsonArray)?.forEach { h ->
                val o = h as? JsonObject ?: return@forEach
                val i = o.long("i").toInt()
                if (i in acked.indices) (o["r"] as? JsonArray)?.forEach { rg ->
                    val a = rg.jsonArray; acked[i].add(a[0].jsonPrimitive.long, a[1].jsonPrimitive.long)
                }
            }
            live.set(done)
            return true
        }
    }

    private fun plan(chunk: Int): List<Work> {
        val works = ArrayList<Work>()
        val batch = ArrayList<Part>(); var batchBytes = 0L
        fun flush() { if (batch.isNotEmpty()) { works.add(Work(ArrayList(batch))); batch.clear(); batchBytes = 0 } }
        for (it in items) {
            if (it.type != ItemType.FILE) continue
            for ((s, e) in acked[it.i].missing(it.size)) {
                if (it.size <= SMALL_FILE) {
                    if (batchBytes + (e - s) > chunk || batch.size >= MAX_PARTS) flush()
                    batch.add(Part(it.i, s, e - s)); batchBytes += e - s
                    continue
                }
                var off = s
                while (off < e) { val n = minOf(chunk.toLong(), e - off); works.add(Work(listOf(Part(it.i, off, n)))); off += n }
            }
        }
        flush()
        return works
    }

    private fun sendAll(ep: InetSocketAddress, r: Run) {
        val opt = options()
        val queue = ConcurrentLinkedQueue(plan(maxOf(256 * 1024, opt.chunkBytes)))
        if (queue.isEmpty()) return
        val streams = minOf(opt.streams.coerceIn(1, 16), queue.size)
        val failure = AtomicReference<Throwable?>()
        val threads = (0 until streams).map {
            Thread({
                try { worker(ep, queue, opt.verify, r, failure) } catch (t: Throwable) {
                    failure.compareAndSet(null, t)
                    r.conns.forEach { } // others notice through the shared failure flag
                }
            }, "cfs-stream-$it").apply { isDaemon = true; start() }
        }
        threads.forEach { it.join() }
        if (r.cancelled) throw IOException("Stopped.")
        failure.get()?.let { throw it }
        if (queue.isNotEmpty()) throw IOException("Transfer stopped.")
    }

    private fun worker(ep: InetSocketAddress, queue: ConcurrentLinkedQueue<Work>, verify: Boolean, r: Run, failure: AtomicReference<Throwable?>) {
        val buf = ByteArray(1024 * 1024)
        var c: Conn? = null
        try {
            while (true) {
                if (r.cancelled || failure.get() != null) return
                val w = queue.poll() ?: return
                try {
                    if (c == null) c = r.track(Conn.connect(ep.address, ep.port, 5000, preparer))
                    sendWork(c!!, w, verify, buf)
                } catch (e: PeerPaused) {
                    queue.add(w)
                    waitWhilePeerPaused(c!!, r)
                } catch (e: Throwable) {
                    queue.add(w)
                    throw e
                }
            }
        } finally { c?.close() }
    }

    private fun sendWork(c: Conn, w: Work, verify: Boolean, buf: ByteArray) {
        c.writeMessage(buildJsonObject {
            put("op", "data"); put("session", id); put("key", key)
            put("parts", JsonArray(w.parts.map { p -> buildJsonObject { put("i", p.i); put("off", p.off); put("len", p.len) } }))
        })
        var sentHere = 0L
        try {
            for (p in w.parts) { currentFile = items[p.i].path; sentHere += sendPart(c, p, verify, buf) }
            val reply = c.readMessage() ?: throw IOException("Connection closed.")
            if (!reply.bool("ok")) {
                live.addAndGet(-sentHere)
                when (reply.str("error")) {
                    "paused" -> throw PeerPaused()
                    "cancelled" -> throw PeerCancelled()
                    "crc" -> throw IOException("Data was damaged in transit; resending.")
                    else -> throw IOException(reply.str("error") ?: "Receiver error.")
                }
            }
        } catch (e: IOException) {
            if (e.message != "Data was damaged in transit; resending.") live.addAndGet(-sentHere)
            throw e
        }
        for (p in w.parts) acked[p.i].add(p.off, p.off + p.len)
        if (state == TransferState.PausedByPeer) setState(TransferState.Active)
    }

    private fun sendPart(c: Conn, p: Part, verify: Boolean, buf: ByteArray): Long {
        val ref = refs[p.i] ?: throw SourceError("A file to send is missing.")
        val reader = try { opener.open(ref) } catch (e: Exception) { throw SourceError("Can't read ${items[p.i].path.substringAfterLast('/')}: ${e.message}") }
        reader.use { src ->
            if (src.length in 0 until p.off + p.len) throw SourceError("${items[p.i].path.substringAfterLast('/')} changed while sending.")
            val crc = if (verify) CRC32() else null
            var pos = 0L
            while (pos < p.len) {
                val n = minOf(buf.size.toLong(), p.len - pos).toInt()
                var read = 0
                while (read < n) {
                    val k = src.read(buf, read, n - read, p.off + pos + read)
                    if (k <= 0) throw SourceError("${items[p.i].path.substringAfterLast('/')} changed while sending.")
                    read += k
                }
                crc?.update(buf, 0, n)
                c.write(buf, 0, n)
                pos += n
                live.addAndGet(n.toLong()); rate.add(n.toLong())
            }
            c.writeU32(crc?.value ?: 0L)
            return p.len
        }
    }

    private fun waitWhilePeerPaused(c: Conn, r: Run) {
        setState(TransferState.PausedByPeer)
        while (!r.cancelled) {
            Thread.sleep(1000)
            c.writeMessage(buildJsonObject { put("op", "status"); put("session", id); put("key", key) })
            val reply = c.readMessage() ?: throw IOException("Connection closed.")
            when (reply.str("state")) {
                "cancelled" -> throw PeerCancelled()
                "paused" -> continue
                else -> { setState(TransferState.Active); return }
            }
        }
    }

    private fun finish(ep: InetSocketAddress, r: Run): Boolean {
        r.track(Conn.connect(ep.address, ep.port, 5000, preparer)).use { c ->
            c.writeMessage(buildJsonObject { put("op", "done"); put("session", id); put("key", key) })
            val reply = c.readMessage(120_000) ?: throw IOException("Connection closed.")
            if (reply.bool("ok")) return true
            return when (reply.str("error")) {
                "cancelled" -> throw PeerCancelled()
                "incomplete" -> false
                else -> throw IOException(reply.str("error") ?: "Finishing failed.")
            }
        }
    }
}
