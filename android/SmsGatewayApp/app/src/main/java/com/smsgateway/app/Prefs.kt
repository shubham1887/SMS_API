package com.smsgateway.app

import android.content.Context
import android.content.SharedPreferences

/** Persistent settings entered on the main screen. */
class Prefs(context: Context) {
    private val sp: SharedPreferences = context.getSharedPreferences("gateway", Context.MODE_PRIVATE)

    var serverUrl: String
        get() = sp.getString("serverUrl", "http://192.168.1.100:5080") ?: ""
        set(v) = sp.edit().putString("serverUrl", v.trim().trimEnd('/')).apply()

    var deviceKey: String
        get() = sp.getString("deviceKey", "") ?: ""
        set(v) = sp.edit().putString("deviceKey", v.trim()).apply()

    /** -1 = system default SIM. */
    var subscriptionId: Int
        get() = sp.getInt("subscriptionId", -1)
        set(v) = sp.edit().putInt("subscriptionId", v).apply()

    /** Delay between two consecutive sends, protects against carrier spam filters. */
    var sendGapMs: Long
        get() = sp.getLong("sendGapMs", 2000L)
        set(v) = sp.edit().putLong("sendGapMs", v).apply()

    var autoStart: Boolean
        get() = sp.getBoolean("autoStart", true)
        set(v) = sp.edit().putBoolean("autoStart", v).apply()

    /** Set while the user has pressed Start; used by BootReceiver. */
    var serviceEnabled: Boolean
        get() = sp.getBoolean("serviceEnabled", false)
        set(v) = sp.edit().putBoolean("serviceEnabled", v).apply()

    var sentTotal: Int
        get() = sp.getInt("sentTotal", 0)
        set(v) = sp.edit().putInt("sentTotal", v).apply()

    val isConfigured: Boolean get() = serverUrl.startsWith("http") && deviceKey.isNotBlank()
}
