package com.smsgateway.app

import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.BatteryManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.io.IOException

/**
 * Foreground service: heartbeat -> claim messages -> send via SmsManager -> report status.
 * Runs until the user presses Stop. Survives screen off; ask the user to disable battery optimisation.
 */
class SmsGatewayService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var loop: Job? = null
    private val wake = Channel<Unit>(Channel.CONFLATED)
    private var wakeLock: PowerManager.WakeLock? = null

    private var pollIntervalMs = 5_000L
    private var maxBatch = 5
    private var lastHeartbeat = 0L
    private var consecutiveErrors = 0

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            Prefs(this).serviceEnabled = false
            stopLoop()
            stopSelf()
            return START_NOT_STICKY
        }
        startInForeground()
        if (loop?.isActive != true) startLoop()
        return START_STICKY
    }

    private fun startInForeground() {
        val n = buildNotification("Starting…")
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIFICATION_ID, n)
        }
    }

    private fun startLoop() {
        val prefs = Prefs(this)
        if (!prefs.isConfigured) {
            LogBus.e("Server URL or device key missing")
            LogBus.status("Not configured")
            stopSelf()
            return
        }
        isRunning = true
        prefs.serviceEnabled = true
        acquireWakeLock()
        ReportQueue.setListener { wake.trySend(Unit) }
        LogBus.i("Gateway started → ${prefs.serverUrl}")
        LogBus.status("Connecting…")

        loop = scope.launch {
            val api = ApiClient(prefs.serverUrl, prefs.deviceKey)
            while (isActive) {
                var hadWork = false
                try {
                    if (System.currentTimeMillis() - lastHeartbeat > HEARTBEAT_MS) heartbeat(api, prefs)
                    flushReports(api)

                    val jobs = api.claim(maxBatch)
                    if (jobs.isNotEmpty()) {
                        hadWork = true
                        LogBus.i("Claimed ${jobs.size} message(s)")
                        for (job in jobs) {
                            if (!isActive) break
                            sendOne(job, prefs)
                            delay(prefs.sendGapMs)
                        }
                        flushReports(api)
                    }
                    if (consecutiveErrors > 0) LogBus.i("Connection restored")
                    consecutiveErrors = 0
                    LogBus.status("Online · sent total ${prefs.sentTotal} · queue ${ReportQueue.size(this@SmsGatewayService)}")
                    updateNotification("Online · sent ${prefs.sentTotal}")
                } catch (e: ApiException) {
                    consecutiveErrors++
                    LogBus.e("Server error ${e.code}: ${e.message}")
                    LogBus.status("Server error ${e.code}")
                    updateNotification("Server error ${e.code}")
                    if (e.code == 401) delay(30_000) // bad key: do not hammer
                } catch (e: IOException) {
                    consecutiveErrors++
                    if (consecutiveErrors == 1 || consecutiveErrors % 12 == 0) LogBus.w("Network error: ${e.message}")
                    LogBus.status("Offline (retrying)")
                    updateNotification("Offline, retrying…")
                } catch (e: Exception) {
                    if (!isActive) break
                    LogBus.e("Unexpected error", e)
                }

                if (!hadWork) {
                    // Sleep until the next poll, or earlier if a status report arrives.
                    val backoff = if (consecutiveErrors > 0) minOf(pollIntervalMs * (1 shl minOf(consecutiveErrors, 4)), 60_000L) else pollIntervalMs
                    withTimeoutOrNull(backoff) { wake.receive() }
                }
            }
        }
    }

    private fun heartbeat(api: ApiClient, prefs: Prefs) {
        val info = api.heartbeat(batteryLevel(), BuildConfig.VERSION_NAME, SmsSender.describeSims(this), null)
        pollIntervalMs = (info.pollIntervalSeconds.coerceAtLeast(2) * 1000L)
        maxBatch = info.maxBatch.coerceIn(1, 20)
        lastHeartbeat = System.currentTimeMillis()
        if (info.pendingInQueue > 0) LogBus.i("Heartbeat ok · ${info.pendingInQueue} pending on server · today ${info.sentToday}/${info.dailyLimit}")
    }

    private fun sendOne(job: SmsJob, prefs: Prefs) {
        try {
            LogBus.i("Sending → ${job.to} (attempt ${job.attempt}, ${job.message.length} chars)")
            SmsSender.send(this, job, prefs.subscriptionId)
            prefs.sentTotal = prefs.sentTotal + 1
        } catch (e: Exception) {
            LogBus.e("SmsManager error for ${job.id}", e)
            SmsSender.pendingParts.remove(job.id)
            ReportQueue.add(this, job.id, "Failed", "SmsManager: ${e.javaClass.simpleName} ${e.message}")
        }
    }

    private fun flushReports(api: ApiClient) {
        val batch = ReportQueue.peek(this)
        if (batch.isEmpty()) return
        val accepted = api.reportBulk(batch)
        val done = batch.filter { it.id in accepted }
        ReportQueue.remove(this, done)
        if (done.size < batch.size) LogBus.w("${batch.size - done.size} status report(s) not accepted, will retry")
    }

    private fun stopLoop() {
        isRunning = false
        ReportQueue.setListener(null)
        loop?.cancel()
        loop = null
        releaseWakeLock()
        LogBus.i("Gateway stopped")
        LogBus.status("Stopped")
    }

    override fun onDestroy() {
        stopLoop()
        scope.cancel()
        super.onDestroy()
    }

    // ---------------------------------------------------------------- helpers

    private fun batteryLevel(): Int? = runCatching {
        getSystemService(BATTERY_SERVICE).let { (it as BatteryManager).getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY) }
    }.getOrNull()

    private fun acquireWakeLock() {
        if (wakeLock?.isHeld == true) return
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SmsGateway:loop").apply { acquire() }
    }

    private fun releaseWakeLock() {
        runCatching { if (wakeLock?.isHeld == true) wakeLock?.release() }
        wakeLock = null
    }

    private fun buildNotification(text: String): Notification {
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0)
        )
        val stop = PendingIntent.getService(
            this, 1, Intent(this, SmsGatewayService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0)
        )
        return NotificationCompat.Builder(this, App.CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("SMS Gateway running")
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(open)
            .addAction(0, "Stop", stop)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    private fun updateNotification(text: String) {
        runCatching {
            val nm = getSystemService(NOTIFICATION_SERVICE) as android.app.NotificationManager
            nm.notify(NOTIFICATION_ID, buildNotification(text))
        }
    }

    companion object {
        const val ACTION_START = "com.smsgateway.app.START"
        const val ACTION_STOP = "com.smsgateway.app.STOP"
        private const val NOTIFICATION_ID = 1001
        private const val HEARTBEAT_MS = 30_000L

        @Volatile var isRunning = false
            private set

        fun start(context: Context) {
            val i = Intent(context, SmsGatewayService::class.java).setAction(ACTION_START)
            ContextCompat.startForegroundService(context, i)
        }

        fun stop(context: Context) {
            context.startService(Intent(context, SmsGatewayService::class.java).setAction(ACTION_STOP))
        }
    }
}
