package com.clipfx.share.core

import kotlinx.serialization.json.*
import java.net.*
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** Finds devices with UDP multicast and broadcast; a TCP subnet scan covers networks that drop UDP. */
class Discovery(
    private val self: () -> DeviceInfo,
    private val discoverable: () -> Boolean,
    private val udpPort: Int = Proto.UDP_PORT,
) {
    var onSeen: ((DeviceInfo, InetAddress) -> Unit)? = null
    private var socket: MulticastSocket? = null
    @Volatile private var running = false
    private val group = InetAddress.getByName(Proto.MULTICAST_GROUP)

    companion object {
        /** Local IPv4 addresses with prefix length, on interfaces that are up. */
        fun localIPv4(): List<Pair<Inet4Address, Int>> = runCatching {
            NetworkInterface.getNetworkInterfaces().toList().filter { runCatching { it.isUp && !it.isLoopback }.getOrDefault(false) }
                .flatMap { nic -> nic.interfaceAddresses.mapNotNull { a -> (a.address as? Inet4Address)?.let { it to a.networkPrefixLength.toInt() } } }
        }.getOrDefault(emptyList())

        fun hello(address: InetAddress, port: Int, timeoutMs: Int, preparer: SocketPreparer? = null): DeviceInfo? = runCatching {
            Conn.connect(address, port, timeoutMs, preparer).use { c ->
                c.writeMessage(buildJsonObject { put("op", "hello") })
                val r = c.readMessage(timeoutMs) ?: return null
                if (r.bool("ok")) r["device"]!!.parse<DeviceInfo>() else null
            }
        }.getOrNull()
    }

    fun start() {
        running = true
        runCatching {
            val s = MulticastSocket(null)
            s.reuseAddress = true
            s.bind(InetSocketAddress(udpPort))
            s.broadcast = true
            runCatching { s.loopbackMode = true } // true = disable loopback
            socket = s
            joinGroups()
            Thread(::receiveLoop, "cfs-discovery").apply { isDaemon = true; start() }
        }
        Thread(::announceLoop, "cfs-announce").apply { isDaemon = true; start() }
    }

    fun joinGroups() {
        val s = socket ?: return
        runCatching { NetworkInterface.getNetworkInterfaces().toList() }.getOrDefault(emptyList()).forEach { nic ->
            runCatching { if (nic.isUp && nic.supportsMulticast() && !nic.isLoopback) s.joinGroup(InetSocketAddress(group, udpPort), nic) }
        }
    }

    private fun packet(type: String): ByteArray = buildJsonObject {
        put("cfs", Proto.VERSION); put("t", type); put("device", self().toJson())
    }.toString().toByteArray()

    fun announce(query: Boolean) {
        val s = socket ?: return
        if (!discoverable() && !query) return
        val data = packet(if (query) "query" else "announce")
        val targets = linkedSetOf<InetAddress>(group, InetAddress.getByName("255.255.255.255"))
        for ((addr, prefix) in localIPv4()) {
            val ip = addr.address
            val mask = if (prefix == 0) 0 else (-1 shl (32 - prefix))
            val bc = ByteArray(4) { k -> (ip[k].toInt() or (mask.inv() ushr (24 - 8 * k) and 0xff)).toByte() }
            targets.add(InetAddress.getByAddress(bc))
        }
        for (t in targets) runCatching { s.send(DatagramPacket(data, data.size, t, udpPort)) }
    }

    private fun announceLoop() {
        announce(true)
        while (running) {
            Thread.sleep(3000)
            if (running) announce(false)
        }
    }

    private fun receiveLoop() {
        val buf = ByteArray(8192)
        while (running) {
            val s = socket ?: return
            val p = DatagramPacket(buf, buf.size)
            try { s.receive(p) } catch (e: Exception) { if (!running) return else continue }
            runCatching {
                val o = json.parseToJsonElement(String(p.data, 0, p.length)) as? JsonObject ?: return@runCatching
                if (o.long("cfs") < 1) return@runCatching
                val dev = o["device"]!!.parse<DeviceInfo>()
                if (dev.id == self().id || dev.id.isEmpty()) return@runCatching
                onSeen?.invoke(dev, p.address)
                if (o.str("t") == "query" && discoverable()) {
                    val reply = packet("announce")
                    s.send(DatagramPacket(reply, reply.size, p.address, udpPort))
                }
            }
        }
    }

    /** Tries every address on local /24 networks. */
    fun scan(port: Int, preparer: SocketPreparer?) {
        val me = self().id
        val pool = Executors.newFixedThreadPool(64)
        for ((addr, _) in localIPv4()) {
            val b = addr.address
            for (h in 1..254) {
                if (h == (b[3].toInt() and 0xff)) continue
                val ip = InetAddress.getByAddress(byteArrayOf(b[0], b[1], b[2], h.toByte()))
                pool.execute { hello(ip, port, 700, preparer)?.let { if (it.id != me) onSeen?.invoke(it, ip) } }
            }
        }
        pool.shutdown()
        pool.awaitTermination(60, TimeUnit.SECONDS)
    }

    fun stop() {
        running = false
        runCatching { socket?.close() }
    }
}
