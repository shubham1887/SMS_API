package com.smsgateway.app

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.TimeUnit

data class SmsJob(val id: String, val to: String, val message: String, val priority: Int, val attempt: Int)

data class HeartbeatInfo(
    val deviceId: Int,
    val name: String,
    val pollIntervalSeconds: Int,
    val maxBatch: Int,
    val maxPerMinute: Int,
    val dailyLimit: Int,
    val sentToday: Int,
    val pendingInQueue: Int
)

class ApiException(val code: Int, message: String) : IOException("HTTP $code: $message")

/** Thin HTTP client for the /api/device endpoints. All calls are blocking; run them on Dispatchers.IO. */
class ApiClient(private val baseUrl: String, private val deviceKey: String) {

    private val http = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .writeTimeout(30, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    private val json = "application/json; charset=utf-8".toMediaType()

    fun heartbeat(battery: Int?, appVersion: String, simInfo: String?, phoneNumber: String?): HeartbeatInfo {
        val body = JSONObject().apply {
            put("batteryLevel", battery ?: JSONObject.NULL)
            put("appVersion", appVersion)
            put("simInfo", simInfo ?: JSONObject.NULL)
            put("phoneNumber", phoneNumber ?: JSONObject.NULL)
        }
        val o = JSONObject(post("/api/device/heartbeat", body.toString()))
        return HeartbeatInfo(
            o.getInt("deviceId"), o.getString("name"), o.getInt("pollIntervalSeconds"), o.getInt("maxBatch"),
            o.getInt("maxPerMinute"), o.getInt("dailyLimit"), o.getInt("sentToday"), o.getInt("pendingInQueue")
        )
    }

    fun claim(max: Int): List<SmsJob> {
        val arr = JSONArray(get("/api/device/messages?max=$max"))
        return (0 until arr.length()).map { i ->
            val o = arr.getJSONObject(i)
            SmsJob(o.getString("id"), o.getString("to"), o.getString("message"), o.optInt("priority"), o.optInt("attempt"))
        }
    }

    fun report(id: String, status: String, error: String?) {
        val body = JSONObject().apply {
            put("status", status)
            put("error", error ?: JSONObject.NULL)
        }
        post("/api/device/messages/$id/status", body.toString())
    }

    /** Returns the ids the server accepted (ok=true). */
    fun reportBulk(items: List<PendingReport>): Set<String> {
        val arr = JSONArray()
        items.forEach {
            arr.put(JSONObject().apply {
                put("id", it.id)
                put("status", it.status)
                put("error", it.error ?: JSONObject.NULL)
                put("timestamp", it.timestampIso)
            })
        }
        val res = JSONArray(post("/api/device/messages/status", arr.toString()))
        val accepted = HashSet<String>()
        for (i in 0 until res.length()) {
            val o = res.getJSONObject(i)
            // "Message not found" / "not assigned" are permanent: drop them too so the queue never jams.
            if (o.optBoolean("ok") || !o.isNull("error")) accepted.add(o.getString("id"))
        }
        return accepted
    }

    private fun get(path: String): String = execute(Request.Builder().url(baseUrl + path).get())

    private fun post(path: String, body: String): String =
        execute(Request.Builder().url(baseUrl + path).post(body.toRequestBody(json)))

    private fun execute(b: Request.Builder): String {
        val req = b.header("X-Device-Key", deviceKey).header("Accept", "application/json").build()
        http.newCall(req).execute().use { res ->
            val text = res.body?.string() ?: ""
            if (!res.isSuccessful) {
                val msg = runCatching { JSONObject(text).optString("error", text) }.getOrDefault(text)
                throw ApiException(res.code, msg.ifBlank { res.message })
            }
            return text
        }
    }
}
