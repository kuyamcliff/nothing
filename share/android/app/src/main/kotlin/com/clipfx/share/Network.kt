package com.clipfx.share

import android.annotation.SuppressLint
import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSpecifier
import android.os.Build
import android.os.Handler
import android.os.Looper
import com.clipfx.share.core.SocketPreparer
import java.net.Inet4Address
import java.net.InetAddress
import java.net.Socket

/**
 * Keeps transfers on the local network even when mobile data is on: a socket to an address
 * on a Wi-Fi subnet is bound to that Wi-Fi network, so Android can't route it over cellular.
 */
class LocalRouting(ctx: Context) : SocketPreparer {
    private val cm = ctx.getSystemService(ConnectivityManager::class.java)
    @Volatile var pinned: Network? = null // a network joined by QR code

    private fun matches(lp: LinkProperties, target: InetAddress): Boolean = lp.linkAddresses.any { la ->
        val a = la.address
        if (a !is Inet4Address || target !is Inet4Address) return@any false
        val prefix = la.prefixLength
        val mask = if (prefix == 0) 0 else (-1 shl (32 - prefix))
        val x = a.address.fold(0) { acc, b -> (acc shl 8) or (b.toInt() and 0xff) }
        val y = target.address.fold(0) { acc, b -> (acc shl 8) or (b.toInt() and 0xff) }
        (x and mask) == (y and mask)
    }

    @Suppress("DEPRECATION")
    fun networkFor(target: InetAddress): Network? {
        pinned?.let { n -> cm.getLinkProperties(n)?.let { if (matches(it, target)) return n } }
        return cm.allNetworks.firstOrNull { n ->
            val caps = cm.getNetworkCapabilities(n) ?: return@firstOrNull false
            !caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) && (cm.getLinkProperties(n)?.let { matches(it, target) } ?: false)
        }
    }

    override fun prepare(socket: Socket, target: InetAddress) {
        // Our own hotspot's clients aren't a "network" object; the kernel routes those directly.
        networkFor(target)?.let { runCatching { it.bindSocket(socket) } }
    }
}

/** Phone-to-phone without any Wi-Fi: one phone hosts a private hotspot, the other joins by QR code. */
class Hotspot(private val ctx: Context) {
    private val wifi = ctx.applicationContext.getSystemService(WifiManager::class.java)
    private var reservation: WifiManager.LocalOnlyHotspotReservation? = null

    data class Info(val ssid: String, val password: String)

    @SuppressLint("MissingPermission")
    fun start(onReady: (Info) -> Unit, onError: (String) -> Unit) {
        stop()
        try {
            wifi.startLocalOnlyHotspot(object : WifiManager.LocalOnlyHotspotCallback() {
                override fun onStarted(r: WifiManager.LocalOnlyHotspotReservation) {
                    reservation = r
                    val info = if (Build.VERSION.SDK_INT >= 30) {
                        val c = r.softApConfiguration
                        @Suppress("DEPRECATION")
                        Info(c.wifiSsid?.toString()?.trim('"') ?: c.ssid ?: "", c.passphrase ?: "")
                    } else {
                        @Suppress("DEPRECATION") val c = r.wifiConfiguration
                        @Suppress("DEPRECATION")
                        Info(c?.SSID?.trim('"') ?: "", c?.preSharedKey?.trim('"') ?: "")
                    }
                    onReady(info)
                }
                override fun onFailed(reason: Int) = onError(when (reason) {
                    ERROR_TETHERING_DISALLOWED -> "Hotspot isn't allowed on this phone."
                    ERROR_INCOMPATIBLE_MODE -> "Turn off the phone's mobile hotspot and try again."
                    else -> "Couldn't start the hotspot."
                })
                override fun onStopped() { reservation = null }
            }, Handler(Looper.getMainLooper()))
        } catch (e: Exception) { onError(e.message ?: "Couldn't start the hotspot.") }
    }

    fun stop() { runCatching { reservation?.close() }; reservation = null }
    val active get() = reservation != null
}

/** Joins a hotspot from a QR code (Android 10+ asks the user once). */
class Joiner(ctx: Context, private val routing: LocalRouting) {
    private val cm = ctx.getSystemService(ConnectivityManager::class.java)
    private var callback: ConnectivityManager.NetworkCallback? = null

    fun join(ssid: String, password: String, onJoined: (Network) -> Unit, onError: (String) -> Unit) {
        leave()
        if (Build.VERSION.SDK_INT < 29) { onError("Connect to \"$ssid\" in Wi-Fi settings, then scan again."); return }
        val spec = WifiNetworkSpecifier.Builder().setSsid(ssid).apply { if (password.isNotEmpty()) setWpa2Passphrase(password) }.build()
        val req = NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).setNetworkSpecifier(spec).build()
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) { routing.pinned = network; onJoined(network) }
            override fun onUnavailable() = onError("Couldn't join \"$ssid\".")
            override fun onLost(network: Network) { if (routing.pinned == network) routing.pinned = null }
        }
        callback = cb
        try { cm.requestNetwork(req, cb, 60_000) } catch (e: Exception) { onError(e.message ?: "Couldn't join.") }
    }

    fun leave() {
        callback?.let { runCatching { cm.unregisterNetworkCallback(it) } }
        callback = null
        routing.pinned = null
    }
}

/** QR payload: CFS:<host>:<port>;N:<name>;S:<ssid>;P:<password>; */
data class QrCode(val host: String, val port: Int, val name: String, val ssid: String?, val password: String?) {
    fun encode(): String = buildString {
        append("CFS:").append(host).append(':').append(port).append(';')
        append("N:").append(esc(name)).append(';')
        if (ssid != null) { append("S:").append(esc(ssid)).append(';'); append("P:").append(esc(password ?: "")).append(';') }
    }

    companion object {
        private fun esc(s: String) = s.replace("\\", "\\\\").replace(";", "\\;")

        fun parse(text: String): QrCode? {
            if (!text.startsWith("CFS:")) return null
            val fields = ArrayList<String>()
            val sb = StringBuilder(); var i = 4
            while (i < text.length) {
                val c = text[i]
                when {
                    c == '\\' && i + 1 < text.length -> { sb.append(text[i + 1]); i++ }
                    c == ';' -> { fields.add(sb.toString()); sb.clear() }
                    else -> sb.append(c)
                }
                i++
            }
            if (sb.isNotEmpty()) fields.add(sb.toString())
            val first = fields.firstOrNull() ?: return null
            val host = first.substringBeforeLast(':'); val port = first.substringAfterLast(':').toIntOrNull() ?: return null
            val map = fields.drop(1).mapNotNull { f -> f.indexOf(':').takeIf { it > 0 }?.let { f.substring(0, it) to f.substring(it + 1) } }.toMap()
            return QrCode(host, port, map["N"] ?: host, map["S"], map["P"])
        }
    }
}
