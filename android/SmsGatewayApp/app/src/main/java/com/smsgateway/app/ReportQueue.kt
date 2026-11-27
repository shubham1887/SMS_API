package com.smsgateway.app

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

data class PendingReport(val id: String, val status: String, val error: String?, val timestampIso: String)

/**
 * Status reports (Sent / Delivered / Failed) are written here first and pushed to the
 * server by the service. Persisted in SharedPreferences so nothing is lost if the
 * network is down or the process restarts.
 */
object ReportQueue {
    private const val PREFS = "report_queue"
    private const val KEY = "items"
    private val iso = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC") }

    @Volatile private var listener: (() -> Unit)? = null

    fun setListener(l: (() -> Unit)?) { listener = l }

    @Synchronized
    fun add(context: Context, id: String, status: String, error: String?) {
        val arr = load(context)
        arr.put(JSONObject().apply {
            put("id", id); put("status", status); put("error", error ?: JSONObject.NULL); put("ts", iso.format(Date()))
        })
        save(context, arr)
        listener?.invoke()
    }

    @Synchronized
    fun peek(context: Context, max: Int = 50): List<PendingReport> {
        val arr = load(context)
        return (0 until minOf(max, arr.length())).map { i ->
            val o = arr.getJSONObject(i)
            PendingReport(o.getString("id"), o.getString("status"), if (o.isNull("error")) null else o.getString("error"), o.getString("ts"))
        }
    }

    @Synchronized
    fun remove(context: Context, done: Collection<PendingReport>) {
        if (done.isEmpty()) return
        val keys = done.map { it.id + "|" + it.status }.toHashSet()
        val arr = load(context)
        val out = JSONArray()
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            if ((o.getString("id") + "|" + o.getString("status")) !in keys) out.put(o)
        }
        save(context, out)
    }

    @Synchronized
    fun size(context: Context): Int = load(context).length()

    private fun load(context: Context): JSONArray =
        runCatching { JSONArray(context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY, "[]")) }
            .getOrDefault(JSONArray())

    private fun save(context: Context, arr: JSONArray) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(KEY, arr.toString()).apply()
}
