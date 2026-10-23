package com.clipfx.share.core

import java.io.File
import kotlin.system.exitProcess

/**
 * Command line used for interoperability and speed tests.
 *   receive --data DIR --save DIR [--port N] [--organize]
 *   send --data DIR --to HOST:PORT [--streams N] [--text T] PATH...
 */
fun main(args: Array<String>) {
    fun opt(name: String) = args.indexOf(name).takeIf { it >= 0 && it + 1 < args.size }?.let { args[it + 1] }
    val data = File(opt("--data") ?: error("--data required"))
    when (args.firstOrNull()) {
        "receive" -> {
            val save = opt("--save") ?: error("--save required")
            val node = ShareNode(data, "jvm", save)
            node.updateSettings { it.copy(deviceName = "Kotlin receiver", askBeforeReceive = false, port = opt("--port")?.toInt() ?: 0, organizeByType = "--organize" in args) }
            node.changeSaveRoot(save)
            node.start(withDiscovery = false)
            println("READY ${node.port}")
            val reported = HashSet<String>()
            while (true) {
                Thread.sleep(100)
                for (t in node.transfers()) if (t.state.isFinal && reported.add(t.id)) println("DONE ${t.state} ${t.totalBytes}")
            }
        }
        "send" -> {
            val to = opt("--to") ?: error("--to required")
            val skip = setOf("--data", "--to", "--streams", "--text")
            val rest = args.drop(1)
            val paths = rest.filterIndexed { k, a -> !a.startsWith("--") && rest.getOrNull(k - 1) !in skip }
            val node = ShareNode(data, "jvm", File(data, "in").path)
            node.updateSettings { it.copy(deviceName = "Kotlin sender", port = 0, streams = opt("--streams")?.toInt() ?: 4) }
            node.start(withDiscovery = false)
            val peer = node.addManual(to) ?: run { println("UNREACHABLE"); exitProcess(2) }
            val items = node.collect(paths, opt("--text"))
            val started = System.nanoTime()
            val id = node.send(listOf(peer.id), items).single()
            var t = node.transfer(id)!!
            while (!t.state.isFinal) { Thread.sleep(20); t = node.transfer(id)!! }
            val secs = (System.nanoTime() - started) / 1e9
            println("RESULT ${t.state} ${"%.0f".format(t.totalBytes / 1e6 / secs)} MB/s ${t.error ?: ""}")
            node.stop()
            exitProcess(if (t.state == TransferState.Completed) 0 else 1)
        }
        else -> { println("usage: receive|send"); exitProcess(64) }
    }
}
