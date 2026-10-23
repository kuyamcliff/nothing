package com.clipfx.share

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.clipfx.share.core.*
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import kotlin.random.Random

/** Runs inside the emulator against the .NET engine on the host (10.0.2.2). */
@RunWith(AndroidJUnit4::class)
class DeviceTest {
    private val ctx: Context = ApplicationProvider.getApplicationContext()
    private val args = InstrumentationRegistry.getArguments()

    private fun ready(): Repo {
        val repo = ctx.repo
        repo.start()
        val until = System.currentTimeMillis() + 30_000
        while (!repo.state.value.ready) { assertTrue("engine didn't start", System.currentTimeMillis() < until); Thread.sleep(100) }
        repo.node.updateSettings { it.copy(askBeforeReceive = false, organizeByType = true) }
        return repo
    }

    @Test
    fun sendsToHost() {
        val port = args.getString("hostPort"); assumeTrue(port != null)
        val repo = ready()
        val peer = repo.node.addManual("10.0.2.2:$port")
        assertNotNull("host not reachable", peer)
        val f = File(ctx.cacheDir, "from-phone.bin")
        f.outputStream().use { o -> val r = Random(42); val b = ByteArray(1 shl 20); repeat(50) { r.nextBytes(b); o.write(b) } }
        val t0 = System.nanoTime()
        val id = repo.node.send(listOf(peer!!.id), repo.node.collect(listOf(f.path), "hello from the phone")).single()
        var t = repo.node.transfer(id)!!
        while (!t.state.isFinal) { Thread.sleep(50); t = repo.node.transfer(id)!! }
        println("CFS-RESULT send ${t.state} ${"%.1f".format(50.0 * 1.048576 / ((System.nanoTime() - t0) / 1e9))} MB/s ${t.error ?: ""}")
        assertEquals(t.error, TransferState.Completed, t.state)
    }

    @Test
    fun receivesFromHost() {
        assumeTrue(args.getString("receive") != null)
        val repo = ready()
        println("CFS-READY ${repo.node.port} ${repo.node.saveRoot}")
        val until = System.currentTimeMillis() + 180_000
        while (System.currentTimeMillis() < until) {
            repo.node.transfers().firstOrNull { it.direction == Direction.Receive && it.state.isFinal }?.let { t ->
                println("CFS-RESULT receive ${t.state} ${t.saved.joinToString("|")} ${t.error ?: ""}")
                assertEquals(TransferState.Completed, t.state)
                return
            }
            Thread.sleep(100)
        }
        fail("nothing received")
    }
}
