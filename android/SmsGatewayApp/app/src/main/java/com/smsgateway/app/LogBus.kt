package com.smsgateway.app

import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** In-memory log shown on the main screen plus logcat. */
object LogBus {
    private const val TAG = "SmsGateway"
    private const val MAX = 300
    private val fmt = SimpleDateFormat("HH:mm:ss", Locale.US)

    private val _lines = MutableStateFlow<List<String>>(emptyList())
    val lines: StateFlow<List<String>> = _lines

    private val _status = MutableStateFlow("Stopped")
    val status: StateFlow<String> = _status

    fun status(s: String) { _status.value = s }

    fun i(msg: String) = add("I", msg).also { Log.i(TAG, msg) }
    fun w(msg: String) = add("W", msg).also { Log.w(TAG, msg) }
    fun e(msg: String, t: Throwable? = null) = add("E", msg + (t?.let { ": ${it.message}" } ?: "")).also { Log.e(TAG, msg, t) }

    @Synchronized
    private fun add(level: String, msg: String) {
        val line = "${fmt.format(Date())} $level  $msg"
        _lines.value = (_lines.value + line).takeLast(MAX)
    }

    fun clear() { _lines.value = emptyList() }
}
