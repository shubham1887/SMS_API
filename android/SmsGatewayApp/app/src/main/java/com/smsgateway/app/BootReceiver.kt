package com.smsgateway.app

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Restarts the gateway after reboot / app update when the user had it running. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val prefs = Prefs(context)
        if (prefs.autoStart && prefs.serviceEnabled && prefs.isConfigured) {
            LogBus.i("Boot: restarting gateway service")
            SmsGatewayService.start(context)
        }
    }
}
