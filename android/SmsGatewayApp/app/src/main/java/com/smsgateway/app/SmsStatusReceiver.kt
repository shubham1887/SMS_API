package com.smsgateway.app

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Receives the SENT and DELIVERED results from SmsManager and queues a status report. */
class SmsStatusReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val id = intent.getStringExtra(SmsSender.EXTRA_ID) ?: return
        val part = intent.getIntExtra(SmsSender.EXTRA_PART, 0)
        val parts = intent.getIntExtra(SmsSender.EXTRA_PARTS, 1)
        val code = resultCode

        when (intent.action) {
            SmsSender.ACTION_SENT -> {
                if (code == Activity.RESULT_OK) {
                    val remaining = SmsSender.pendingParts[id]?.decrementAndGet() ?: 0
                    if (remaining <= 0) {
                        SmsSender.pendingParts.remove(id)
                        LogBus.i("Sent ✓ $id" + if (parts > 1) " ($parts parts)" else "")
                        ReportQueue.add(context, id, "Sent", null)
                    }
                } else {
                    // One failed part fails the whole message; the server decides about retry.
                    if (SmsSender.pendingParts.remove(id) != null) {
                        val err = SmsSender.errorName(code)
                        LogBus.w("Send failed ✗ $id part ${part + 1}/$parts: $err")
                        ReportQueue.add(context, id, "Failed", err)
                    }
                }
            }

            SmsSender.ACTION_DELIVERED -> {
                // Report once per message (first delivery report that comes back).
                if (!SmsSender.deliveredOnce.add(id)) return
                if (code == Activity.RESULT_OK) {
                    LogBus.i("Delivered ✓ $id")
                    ReportQueue.add(context, id, "Delivered", null)
                } else {
                    LogBus.w("Delivery failed ✗ $id (code $code)")
                    ReportQueue.add(context, id, "Failed", "Delivery report: failure (code $code)")
                }
            }
        }
    }
}
