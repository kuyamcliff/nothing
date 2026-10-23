package com.clipfx.share.core

import java.io.File
import java.nio.file.Files
import java.security.MessageDigest
import kotlin.random.Random
import kotlin.test.*

class TransferTest {
    private val roots = ArrayList<File>()
    private val nodes = ArrayList<ShareNode>()

    private fun temp(name: String) = Files.createTempDirectory("cfs-kt-$name").toFile().also { roots.add(it) }

    private class N(val node: ShareNode, val save: File, val data: File)

    private fun start(name: String, ask: Boolean = false, organize: Boolean = false, chunkMB: Int = 8, data: File? = null, port: Int = 0): N {
        val d = data ?: temp("node-$name")
        val save = File(d, "Received")
        val n = ShareNode(d, "test", save.path)
        n.updateSettings { it.copy(deviceName = name, askBeforeReceive = ask, organizeByType = organize, chunkMB = chunkMB, port = port) }
        n.changeSaveRoot(save.path)
        n.start(withDiscovery = false)
        nodes.add(n)
        return N(n, save, d)
    }

    private fun pair(a: N, b: N) = a.node.addManual("127.0.0.1:${b.node.port}")!!.id

    private fun waitFor(n: ShareNode, id: String, seconds: Int = 60, cond: (TransferView) -> Boolean): TransferView {
        val until = System.currentTimeMillis() + seconds * 1000
        while (true) {
            val t = n.transfer(id)
            if (t != null && cond(t)) return t
            if (System.currentTimeMillis() > until) fail("Timed out: ${t?.state} ${t?.error} ${t?.doneBytes}/${t?.totalBytes}")
            Thread.sleep(20)
        }
    }

    private fun done(n: ShareNode, id: String) = waitFor(n, id, 120) { it.state.isFinal }

    private fun incoming(n: ShareNode, cond: (TransferView) -> Boolean): TransferView {
        val until = System.currentTimeMillis() + 30_000
        while (true) {
            n.transfers().firstOrNull { it.direction == Direction.Receive && cond(it) }?.let { return it }
            if (System.currentTimeMillis() > until) fail("No incoming transfer")
            Thread.sleep(20)
        }
    }

    private fun randomFile(dir: File, rel: String, size: Long, seed: Int = 1): File {
        val f = File(dir, rel); f.parentFile.mkdirs()
        val rnd = Random(seed); val buf = ByteArray(1 shl 20)
        f.outputStream().use { o -> var left = size; while (left > 0) { rnd.nextBytes(buf); val n = minOf(buf.size.toLong(), left).toInt(); o.write(buf, 0, n); left -= n } }
        return f
    }

    private fun hash(f: File): String = MessageDigest.getInstance("SHA-256").let { md ->
        f.inputStream().use { i -> val b = ByteArray(1 shl 20); while (true) { val n = i.read(b); if (n < 0) break; md.update(b, 0, n) } }
        md.digest().joinToString("") { "%02x".format(it) }
    }

    @AfterTest
    fun cleanup() {
        nodes.forEach { runCatching { it.stop() } }
        roots.forEach { it.deleteRecursively() }
    }

    @Test
    fun sortsByTypeAndKeepsFolders() {
        val a = start("A"); val b = start("B", organize = true)
        val src = temp("src")
        val files = listOf("clip.MP4", "photo.jpg", "song.mp3", "report.pdf", "game.apk", "backup.zip", "notes.xyz").mapIndexed { k, n -> randomFile(src, n, 1000L + k, k).path }
        randomFile(src, "Trip/day1/a.jpg", 500, 50)
        val id = a.node.send(listOf(pair(a, b)), a.node.collect(files + File(src, "Trip").path, "hi")).single()
        assertEquals(TransferState.Completed, done(a.node, id).state)
        for ((dir, name) in listOf("Videos" to "clip.MP4", "Images" to "photo.jpg", "Music" to "song.mp3", "Documents" to "report.pdf",
            "Apps" to "game.apk", "Archives" to "backup.zip", "Other" to "notes.xyz"))
            assertTrue(File(b.save, "$dir/$name").exists(), "$dir/$name")
        assertEquals(hash(File(src, "Trip/day1/a.jpg")), hash(File(b.save, "Folders/Trip/day1/a.jpg")))
        assertEquals("hi", incoming(b.node) { it.state == TransferState.Completed }.text)
    }

    @Test
    fun asksThenTrusts() {
        val a = start("A"); val b = start("B", ask = true)
        val f = randomFile(temp("src"), "doc.pdf", 1000)
        val peer = pair(a, b)
        val id1 = a.node.send(listOf(peer), a.node.collect(listOf(f.path))).single()
        val req = incoming(b.node) { it.state == TransferState.Waiting }
        assertTrue(b.node.decline(req.id))
        assertEquals(TransferState.Declined, done(a.node, id1).state)
        val id2 = a.node.send(listOf(peer), a.node.collect(listOf(f.path))).single()
        val req2 = incoming(b.node) { it.state == TransferState.Waiting }
        assertTrue(b.node.accept(req2.id, trust = true))
        assertEquals(TransferState.Completed, done(a.node, id2).state)
        val id3 = a.node.send(listOf(peer), a.node.collect(listOf(f.path))).single()
        assertEquals(TransferState.Completed, done(a.node, id3).state)
        assertTrue(File(b.save, "doc (2).pdf").exists())
    }

    @Test
    fun pauseResumeAndReceiverRestart() {
        val a = start("A", chunkMB = 2)
        val bData = temp("b")
        val bPort = java.net.ServerSocket(0).use { it.localPort }
        var b = start("B", data = bData, port = bPort)
        val f = randomFile(temp("src"), "big.iso", 200_000_000, 7)
        val id = a.node.send(listOf(pair(a, b)), a.node.collect(listOf(f.path))).single()
        waitFor(a.node, id) { it.doneBytes > 20_000_000 }
        a.node.pause(id)
        waitFor(a.node, id) { it.state == TransferState.Paused }
        Thread.sleep(300)
        val at = a.node.transfer(id)!!.doneBytes
        Thread.sleep(500)
        assertEquals(at, a.node.transfer(id)!!.doneBytes)
        a.node.resume(id)
        waitFor(a.node, id) { it.doneBytes > 100_000_000 }
        b.node.stop(); nodes.remove(b.node)        // receiver goes away mid-transfer
        Thread.sleep(300)
        b = start("B", data = bData, port = bPort) // and comes back
        assertEquals(TransferState.Completed, done(a.node, id).state)
        assertEquals(hash(f), hash(File(b.save, "big.iso")))
    }

    @Test
    fun manySmallFiles() {
        val a = start("A"); val b = start("B")
        val src = temp("src")
        val rnd = Random(3)
        repeat(1500) { randomFile(src, "proj/d${it % 23}/f$it.txt", rnd.nextLong(0, 6000), it) }
        val t0 = System.nanoTime()
        val id = a.node.send(listOf(pair(a, b)), a.node.collect(listOf(File(src, "proj").path))).single()
        assertEquals(TransferState.Completed, done(a.node, id).state)
        println("1500 small files in ${(System.nanoTime() - t0) / 1_000_000} ms")
        File(src, "proj").walkTopDown().filter { it.isFile }.forEach {
            assertEquals(hash(it), hash(File(b.save, "proj/" + it.relativeTo(File(src, "proj")).invariantSeparatorsPath)))
        }
    }

    @Test
    fun engineSpeed() {
        IncomingSession.noWrite = true
        try {
            val a = start("A"); val b = start("B")
            val src = temp("src")
            val f = File(src, "speed.bin"); java.io.RandomAccessFile(f, "rw").use { it.setLength(1_000_000_000) }
            val peer = pair(a, b)
            var best = 0.0
            repeat(3) { run ->
                val t0 = System.nanoTime()
                val id = a.node.send(listOf(peer), a.node.collect(listOf(f.path))).single()
                assertEquals(TransferState.Completed, done(a.node, id).state)
                val mbps = 1000.0 / ((System.nanoTime() - t0) / 1e9)
                println("run $run: ${"%.0f".format(mbps)} MB/s")
                best = maxOf(best, mbps)
            }
            assertTrue(best > 300, "Too slow: $best MB/s")
        } finally { IncomingSession.noWrite = false }
    }
}

class UnitTest {
    @Test fun ranges() {
        val r = RangeSet()
        r.add(10, 20); r.add(30, 40); r.add(20, 30); r.add(50, 60); r.add(0, 5)
        assertEquals(listOf(listOf(0L, 5L), listOf(10L, 40L), listOf(50L, 60L)), r.toList())
        assertEquals(listOf(5L to 10L, 40L to 50L, 60L to 100L), r.missing(100))
    }

    @Test fun paths() {
        assertEquals(listOf("evil.txt"), PathSafety.segments("../../evil.txt"))
        assertEquals(listOf("C_", "Windows", "x.dll"), PathSafety.segments("C:\\Windows\\x.dll"))
        assertEquals(listOf("_CON.txt"), PathSafety.segments("CON.txt"))
        assertEquals("a (3).txt", PathSafety.unique("a.txt") { it == "a.txt" || it == "a (2).txt" })
    }

    @Test fun categoriesMatchWindows() {
        assertEquals("Videos/x.MKV", Categories.place("x.MKV", ItemType.FILE, true))
        assertEquals("Apps/app.apk", Categories.place("app.apk", ItemType.FILE, true))
        assertEquals("Archives/a.tar.gz", Categories.place("a.tar.gz", ItemType.FILE, true))
        assertEquals("Folders/Trip/a.jpg", Categories.place("Trip/a.jpg", ItemType.FILE, true))
        assertEquals("Other/README", Categories.place("README", ItemType.FILE, true))
        assertEquals("x.mkv", Categories.place("x.mkv", ItemType.FILE, false))
    }
}
