package com.smsgateway.app

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import android.telephony.SmsManager
import android.telephony.SubscriptionManager
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/** Sends one SMS (single or multipart) through SmsManager and wires SENT / DELIVERED intents. */
object SmsSender {
    const val ACTION_SENT = "com.smsgateway.app.SMS_SENT"
    const val ACTION_DELIVERED = "com.smsgateway.app.SMS_DELIVERED"
    const val EXTRA_ID = "messageId"
    const val EXTRA_PART = "part"
    const val EXTRA_PARTS = "parts"

    private val requestCode = AtomicInteger((System.currentTimeMillis() % 100000).toInt())

    /** messageId -> parts still waiting for a SENT result. */
    internal val pendingParts = ConcurrentHashMap<String, AtomicInteger>()
    /** messageIds for which a Delivered report has already been queued. */
    internal val deliveredOnce = ConcurrentHashMap.newKeySet<String>()

    fun smsManager(context: Context, subscriptionId: Int): SmsManager {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val base = context.getSystemService(SmsManager::class.java)
            if (subscriptionId >= 0) base.createForSubscriptionId(subscriptionId) else base
        } else {
            @Suppress("DEPRECATION")
            if (subscriptionId >= 0) SmsManager.getSmsManagerForSubscriptionId(subscriptionId) else SmsManager.getDefault()
        }
    }

    /** Throws on immediate failure (invalid arguments, missing permission). Async result comes via SmsStatusReceiver. */
    fun send(context: Context, job: SmsJob, subscriptionId: Int) {
        val sm = smsManager(context, subscriptionId)
        val parts = sm.divideMessage(job.message)
        val n = parts.size
        pendingParts[job.id] = AtomicInteger(n)
        deliveredOnce.remove(job.id)

        val sentIntents = ArrayList<PendingIntent>(n)
        val deliveredIntents = ArrayList<PendingIntent>(n)
        for (i in 0 until n) {
            sentIntents.add(pending(context, ACTION_SENT, job.id, i, n))
            deliveredIntents.add(pending(context, ACTION_DELIVERED, job.id, i, n))
        }

        if (n == 1) {
            sm.sendTextMessage(job.to, null, parts[0], sentIntents[0], deliveredIntents[0])
        } else {
            sm.sendMultipartTextMessage(job.to, null, parts, sentIntents, deliveredIntents)
        }
    }

    private fun pending(context: Context, action: String, id: String, part: Int, parts: Int): PendingIntent {
        val intent = Intent(context, SmsStatusReceiver::class.java)
            .setAction(action)
            .putExtra(EXTRA_ID, id)
            .putExtra(EXTRA_PART, part)
            .putExtra(EXTRA_PARTS, parts)
        val flags = PendingIntent.FLAG_UPDATE_CURRENT or
            (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) PendingIntent.FLAG_IMMUTABLE else 0)
        return PendingIntent.getBroadcast(context, requestCode.incrementAndGet(), intent, flags)
    }

    /** Human readable list of active SIMs, e.g. "1: Jio (+91…), 2: Airtel". */
    fun describeSims(context: Context): String? = runCatching {
        val sm = context.getSystemService(Context.TELEPHONY_SUBSCRIPTION_SERVICE) as SubscriptionManager
        sm.activeSubscriptionInfoList?.joinToString(", ") { "${it.simSlotIndex + 1}:${it.carrierName}(sub ${it.subscriptionId})" }
    }.getOrNull()

    fun errorName(resultCode: Int): String = when (resultCode) {
        SmsManager.RESULT_ERROR_GENERIC_FAILURE -> "RESULT_ERROR_GENERIC_FAILURE"
        SmsManager.RESULT_ERROR_RADIO_OFF -> "RESULT_ERROR_RADIO_OFF (airplane mode / no radio)"
        SmsManager.RESULT_ERROR_NULL_PDU -> "RESULT_ERROR_NULL_PDU"
        SmsManager.RESULT_ERROR_NO_SERVICE -> "RESULT_ERROR_NO_SERVICE (no network)"
        SmsManager.RESULT_ERROR_LIMIT_EXCEEDED -> "RESULT_ERROR_LIMIT_EXCEEDED (Android SMS limit, allow in dialog)"
        SmsManager.RESULT_ERROR_SHORT_CODE_NOT_ALLOWED -> "RESULT_ERROR_SHORT_CODE_NOT_ALLOWED"
        SmsManager.RESULT_ERROR_SHORT_CODE_NEVER_ALLOWED -> "RESULT_ERROR_SHORT_CODE_NEVER_ALLOWED"
        else -> "SMS_ERROR_$resultCode"
    }
}
