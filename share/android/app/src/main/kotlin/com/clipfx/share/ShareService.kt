package com.clipfx.share

import android.app.*
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import com.clipfx.share.core.*
import kotlinx.coroutines.*

/** Keeps the device discoverable and transfers running when the app is in the background. */
class ShareService : Service() {
    companion object {
        const val CH_STATUS = "status"
        const val CH_REQUEST = "requests"
        const val CH_DONE = "done"
        const val ACTION_ACCEPT = "accept"
        const val ACTION_DECLINE = "decline"
        const val ACTION_QUIT = "quit"
        private const val ID_STATUS = 1

        fun start(ctx: Context) {
            val i = Intent(ctx, ShareService::class.java)
            if (Build.VERSION.SDK_INT >= 26) ctx.startForegroundService(i) else ctx.startService(i)
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var nm: NotificationManager

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CH_STATUS, "Status", NotificationManager.IMPORTANCE_LOW))
        nm.createNotificationChannel(NotificationChannel(CH_REQUEST, "Incoming files", NotificationManager.IMPORTANCE_HIGH))
        nm.createNotificationChannel(NotificationChannel(CH_DONE, "Finished transfers", NotificationManager.IMPORTANCE_DEFAULT))
        val type = if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE else 0
        ServiceCompat.startForeground(this, ID_STATUS, status("Ready to receive"), type)
        val repo = repo
        repo.start()
        repo.onIncoming = { t -> scope.launch { request(t) } }
        repo.onFinished = { t -> scope.launch { finished(t) } }
        scope.launch {
            repo.state.collect { s ->
                val active = s.transfers.filter { it.state == TransferState.Active || it.state == TransferState.Reconnecting || it.state == TransferState.Connecting }
                val text = if (active.isEmpty()) "Ready to receive" + (s.addresses.firstOrNull()?.let { " · $it" } ?: "")
                else {
                    val total = active.sumOf { it.totalBytes }; val done = active.sumOf { it.doneBytes }
                    "${active.size} transfer${if (active.size > 1) "s" else ""} · ${Format.bytes(done)} of ${Format.bytes(total)} · ${Format.rate(active.sumOf { it.rate })}"
                }
                val progress = active.takeIf { it.isNotEmpty() }?.let { a -> (a.sumOf { it.doneBytes } * 100 / maxOf(1, a.sumOf { it.totalBytes })).toInt() }
                nm.notify(ID_STATUS, status(text, progress))
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val id = intent?.getStringExtra("id")
        when (intent?.action) {
            ACTION_ACCEPT -> { id?.let { repo.node.accept(it, false); nm.cancel(it.hashCode()) } }
            ACTION_DECLINE -> { id?.let { repo.node.decline(it); nm.cancel(it.hashCode()) } }
            ACTION_QUIT -> { repo.node.stop(); stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); android.os.Process.killProcess(android.os.Process.myPid()) }
        }
        return START_STICKY
    }

    private fun openApp() = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE)

    private fun action(action: String, id: String?, code: Int) = PendingIntent.getService(this, code,
        Intent(this, ShareService::class.java).setAction(action).putExtra("id", id), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    private fun status(text: String, progress: Int? = null): Notification =
        NotificationCompat.Builder(this, CH_STATUS)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle("ClipFx Share")
            .setContentText(text)
            .setOngoing(true).setSilent(true).setShowWhen(false)
            .setContentIntent(openApp())
            .apply { if (progress != null) setProgress(100, progress, false) }
            .addAction(0, "Quit", action(ACTION_QUIT, null, 9))
            .build()

    private fun request(t: TransferView) {
        val n = NotificationCompat.Builder(this, CH_REQUEST)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(t.peer.name)
            .setContentText("${t.title} · ${Format.bytes(t.totalBytes)}")
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_CALL)
            .setContentIntent(openApp())
            .setAutoCancel(true)
            .setTimeoutAfter(ShareNode.DECISION_TIMEOUT_MS)
            .addAction(0, "Decline", action(ACTION_DECLINE, t.id, t.id.hashCode() + 1))
            .addAction(0, "Accept", action(ACTION_ACCEPT, t.id, t.id.hashCode() + 2))
            .build()
        nm.notify(t.id.hashCode(), n)
    }

    private fun finished(t: TransferView) {
        nm.cancel(t.id.hashCode())
        val verb = if (t.direction == Direction.Receive) "Received" else "Sent"
        val n = NotificationCompat.Builder(this, CH_DONE)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(if (t.state == TransferState.Completed) "$verb ${t.title}" else "Transfer failed")
            .setContentText(if (t.state == TransferState.Completed) "${Format.bytes(t.totalBytes)} · ${t.peer.name}" else t.error ?: t.title)
            .setContentIntent(openApp())
            .setAutoCancel(true)
            .build()
        nm.notify(t.id.hashCode(), n)
    }

    override fun onDestroy() { scope.cancel(); super.onDestroy() }
}

object Format {
    private val units = arrayOf("B", "KB", "MB", "GB", "TB")
    fun bytes(n: Long): String {
        if (n <= 0) return "0 B"
        var v = n.toDouble(); var i = 0
        while (v >= 1024 && i < units.size - 1) { v /= 1024; i++ }
        val d = if (i == 0) 0 else if (v < 10) 2 else if (v < 100) 1 else 0
        return "%.${d}f %s".format(v, units[i])
    }
    fun rate(bps: Double) = bytes(bps.toLong()) + "/s"
    fun eta(seconds: Double?): String? {
        if (seconds == null || seconds.isNaN() || seconds.isInfinite()) return null
        val s = seconds.toLong()
        return when { s < 60 -> "${s}s"; s < 3600 -> "${s / 60}m ${s % 60}s"; else -> "${s / 3600}h ${(s % 3600) / 60}m" }
    }
}
