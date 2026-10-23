package com.clipfx.share.core

import kotlinx.serialization.json.Json as KJson
import kotlinx.serialization.json.JsonObject
import java.io.Closeable
import java.io.EOFException
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException

/** Lets a platform route sockets, e.g. bind them to Wi-Fi while mobile data is on. */
fun interface SocketPreparer { fun prepare(socket: Socket, target: InetAddress) }

class TimeoutError(message: String) : IOException(message)

/** A protocol connection: JSON lines plus raw byte streams with its own read buffer. */
class Conn(private val socket: Socket) : Closeable {
    companion object {
        const val SOCKET_BUFFER = 4 * 1024 * 1024
        const val IDLE_MS = 30_000
        const val MAX_LINE = 64 * 1024 * 1024

        fun tune(s: Socket) {
            s.tcpNoDelay = true
            runCatching { s.sendBufferSize = SOCKET_BUFFER; s.receiveBufferSize = SOCKET_BUFFER }
            runCatching { s.keepAlive = true }
        }

        fun connect(address: InetAddress, port: Int, timeoutMs: Int, preparer: SocketPreparer? = null): Conn {
            val s = Socket()
            try {
                tune(s)
                preparer?.prepare(s, address)
                s.connect(InetSocketAddress(address, port), timeoutMs)
                return Conn(s)
            } catch (e: SocketTimeoutException) {
                runCatching { s.close() }
                throw TimeoutError("Couldn't reach the other device.")
            } catch (e: Exception) {
                runCatching { s.close() }
                throw e
            }
        }
    }

    private val input = socket.getInputStream()
    private val output = socket.getOutputStream()
    private val buf = ByteArray(256 * 1024)
    private var pos = 0
    private var len = 0

    init { tune(socket) }

    val remote: InetAddress? get() = socket.inetAddress

    private fun fill(): Boolean {
        if (pos > 0 && pos == len) { pos = 0; len = 0 }
        if (len == buf.size) { System.arraycopy(buf, pos, buf, 0, len - pos); len -= pos; pos = 0 }
        val n = try { input.read(buf, len, buf.size - len) } catch (e: SocketTimeoutException) { throw TimeoutError("The other device stopped responding.") }
        if (n < 0) return false
        len += n
        return true
    }

    /** Reads one JSON line, skipping keep-alives. Null when the peer closed. */
    fun readMessage(timeoutMs: Int = IDLE_MS): JsonObject? {
        socket.soTimeout = timeoutMs
        var big: java.io.ByteArrayOutputStream? = null
        while (true) {
            var nl = -1
            for (k in pos until len) if (buf[k] == '\n'.code.toByte()) { nl = k; break }
            if (nl >= 0) {
                val start = pos
                pos = nl + 1
                val bytes: ByteArray = if (big != null) { big.write(buf, start, nl - start); big.toByteArray().also { big = null } } else buf.copyOfRange(start, nl)
                val text = String(bytes, Charsets.UTF_8).trimEnd('\r')
                if (text.isEmpty()) continue
                return KJson.parseToJsonElement(text) as? JsonObject ?: throw IOException("Bad message.")
            }
            if (len - pos > 0 && len == buf.size) {
                if (big == null) big = java.io.ByteArrayOutputStream()
                big!!.write(buf, pos, len - pos)
                if (big!!.size() > MAX_LINE) throw IOException("Message too large.")
                pos = 0; len = 0
            }
            if (!fill()) return null
        }
    }

    fun writeMessage(msg: JsonObject) {
        socket.soTimeout = IDLE_MS
        output.write((msg.toString() + "\n").toByteArray(Charsets.UTF_8))
        output.flush()
    }

    fun keepAlive() { output.write('\n'.code); output.flush() }

    fun readFully(dst: ByteArray, off: Int, count: Int) {
        socket.soTimeout = IDLE_MS
        var got = 0
        val buffered = minOf(len - pos, count)
        if (buffered > 0) { System.arraycopy(buf, pos, dst, off, buffered); pos += buffered; got = buffered }
        while (got < count) {
            val n = try { input.read(dst, off + got, count - got) } catch (e: SocketTimeoutException) { throw TimeoutError("The other device stopped sending.") }
            if (n < 0) throw EOFException("Connection closed.")
            got += n
        }
    }

    fun write(src: ByteArray, off: Int, count: Int) {
        socket.soTimeout = IDLE_MS
        output.write(src, off, count)
    }

    fun flush() = output.flush()

    fun readU32(): Long {
        val b = ByteArray(4); readFully(b, 0, 4)
        return ((b[0].toLong() and 0xff) shl 24) or ((b[1].toLong() and 0xff) shl 16) or ((b[2].toLong() and 0xff) shl 8) or (b[3].toLong() and 0xff)
    }

    fun writeU32(v: Long) {
        write(byteArrayOf((v ushr 24).toByte(), (v ushr 16).toByte(), (v ushr 8).toByte(), v.toByte()), 0, 4)
        flush()
    }

    override fun close() {
        runCatching { socket.shutdownOutput() }
        runCatching { socket.close() }
    }
}
