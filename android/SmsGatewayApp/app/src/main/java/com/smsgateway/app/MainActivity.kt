package com.smsgateway.app

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.telephony.SubscriptionManager
import android.widget.ArrayAdapter
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.smsgateway.app.databinding.ActivityMainBinding
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : AppCompatActivity() {

    private lateinit var b: ActivityMainBinding
    private lateinit var prefs: Prefs
    private var simIds: List<Int> = listOf(-1)

    private val permissionLauncher = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
        val denied = result.filterValues { !it }.keys
        if (denied.isEmpty()) {
            toast("All permissions granted")
        } else {
            // Android stops showing the dialog after the user denied once/twice: send them to Settings.
            val permanentlyDenied = denied.any { !shouldShowRequestPermissionRationale(it) }
            if (permanentlyDenied) showPermissionSettingsDialog(denied)
            else toast("Denied: ${denied.joinToString { it.substringAfterLast('.') }}")
        }
        loadSims()
        refreshPermissionState()
    }

    private fun showPermissionSettingsDialog(denied: Collection<String>) {
        val names = denied.joinToString(", ") {
            when (it) {
                Manifest.permission.SEND_SMS -> "SMS"
                Manifest.permission.READ_PHONE_STATE -> "Phone"
                else -> "Notifications"
            }
        }
        androidx.appcompat.app.AlertDialog.Builder(this)
            .setTitle("Permission required")
            .setMessage("Android has blocked the $names permission for this app.\n\n" +
                "Open Settings > Permissions and set SMS and Phone to \"Allow\".\n" +
                "If a permission shows \"Restricted setting\", tap the ⋮ menu on the app info page and choose \"Allow restricted settings\" first.")
            .setPositiveButton("Open Settings") { _, _ -> openAppSettings() }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun openAppSettings() {
        runCatching {
            startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).setData(Uri.parse("package:$packageName")))
        }.onFailure { toast("Open Settings > Apps > SMS Gateway > Permissions") }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityMainBinding.inflate(layoutInflater)
        setContentView(b.root)
        prefs = Prefs(this)

        b.serverUrl.setText(prefs.serverUrl)
        b.deviceKey.setText(prefs.deviceKey)
        b.sendGap.setText((prefs.sendGapMs / 1000).toString())
        b.autoStart.isChecked = prefs.autoStart

        b.btnPermissions.setOnClickListener { requestPermissions() }
        b.btnPermissions.setOnLongClickListener { openAppSettings(); true }
        b.btnBattery.setOnClickListener { requestIgnoreBatteryOptimisation() }
        b.btnSave.setOnClickListener { save(); toast("Saved") }
        b.btnTest.setOnClickListener { save(); testConnection() }
        b.btnStart.setOnClickListener {
            save()
            if (!prefs.isConfigured) { toast("Enter server URL and device key"); return@setOnClickListener }
            if (!hasPermission(Manifest.permission.SEND_SMS)) {
                if (shouldShowRequestPermissionRationale(Manifest.permission.SEND_SMS)) requestPermissions()
                else showPermissionSettingsDialog(listOf(Manifest.permission.SEND_SMS))
                return@setOnClickListener
            }
            SmsGatewayService.start(this)
        }
        b.btnStop.setOnClickListener { SmsGatewayService.stop(this) }
        b.btnClearLog.setOnClickListener { LogBus.clear() }

        lifecycleScope.launch {
            LogBus.lines.collect { lines ->
                b.log.text = lines.joinToString("\n")
                b.logScroll.post { b.logScroll.fullScroll(android.view.View.FOCUS_DOWN) }
            }
        }
        lifecycleScope.launch {
            LogBus.status.collect { s ->
                b.status.text = s
                b.btnStart.isEnabled = !SmsGatewayService.isRunning
                b.btnStop.isEnabled = SmsGatewayService.isRunning
            }
        }

        loadSims()
        refreshPermissionState()
    }

    override fun onResume() {
        super.onResume()
        refreshPermissionState()
        b.btnStart.isEnabled = !SmsGatewayService.isRunning
        b.btnStop.isEnabled = SmsGatewayService.isRunning
    }

    private fun save() {
        prefs.serverUrl = b.serverUrl.text.toString()
        prefs.deviceKey = b.deviceKey.text.toString()
        prefs.sendGapMs = (b.sendGap.text.toString().toLongOrNull() ?: 2L).coerceIn(0, 60) * 1000
        prefs.autoStart = b.autoStart.isChecked
        val idx = b.simSpinner.selectedItemPosition
        prefs.subscriptionId = simIds.getOrElse(idx) { -1 }
    }

    private fun testConnection() {
        b.status.text = "Testing…"
        lifecycleScope.launch {
            val result = withContext(Dispatchers.IO) {
                runCatching {
                    ApiClient(prefs.serverUrl, prefs.deviceKey)
                        .heartbeat(null, BuildConfig.VERSION_NAME, SmsSender.describeSims(this@MainActivity), null)
                }
            }
            result.onSuccess {
                LogBus.i("Connected as '${it.name}' (device #${it.deviceId}), ${it.pendingInQueue} pending, limit ${it.maxPerMinute}/min ${it.dailyLimit}/day")
                b.status.text = "Connected: ${it.name}"
            }.onFailure {
                LogBus.e("Connection failed", it)
                b.status.text = "Connection failed"
            }
        }
    }

    // ------------------------------------------------------------ permissions

    private fun requiredPermissions(): Array<String> {
        val list = mutableListOf(Manifest.permission.SEND_SMS, Manifest.permission.READ_PHONE_STATE)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) list += Manifest.permission.POST_NOTIFICATIONS
        return list.toTypedArray()
    }

    private fun hasPermission(p: String) = ContextCompat.checkSelfPermission(this, p) == PackageManager.PERMISSION_GRANTED

    private fun requestPermissions() = permissionLauncher.launch(requiredPermissions())

    private fun refreshPermissionState() {
        val missing = requiredPermissions().filterNot { hasPermission(it) }
        val pm = getSystemService(POWER_SERVICE) as PowerManager
        val battery = if (Build.VERSION.SDK_INT >= 23) pm.isIgnoringBatteryOptimizations(packageName) else true
        b.permState.text = buildString {
            append(if (missing.isEmpty()) "Permissions: OK" else "Permissions missing: " + missing.joinToString { it.substringAfterLast('.') } + " (tap Permissions, or long-press to open Settings)")
            append("\n")
            append(if (battery) "Battery optimisation: disabled (good)" else "Battery optimisation: ON – tap the button to disable")
        }
    }

    private fun requestIgnoreBatteryOptimisation() {
        if (Build.VERSION.SDK_INT < 23) return
        runCatching {
            startActivity(
                Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).setData(Uri.parse("package:$packageName"))
            )
        }.onFailure {
            startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
        }
    }

    // ------------------------------------------------------------ SIM list

    private fun loadSims() {
        val labels = mutableListOf("Default SIM")
        val ids = mutableListOf(-1)
        if (hasPermission(Manifest.permission.READ_PHONE_STATE)) {
            runCatching {
                val sm = getSystemService(TELEPHONY_SUBSCRIPTION_SERVICE) as SubscriptionManager
                sm.activeSubscriptionInfoList?.forEach {
                    labels += "SIM ${it.simSlotIndex + 1}: ${it.carrierName} ${it.number ?: ""}".trim()
                    ids += it.subscriptionId
                }
            }
        }
        simIds = ids
        b.simSpinner.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, labels)
        val sel = ids.indexOf(prefs.subscriptionId)
        if (sel >= 0) b.simSpinner.setSelection(sel)
    }

    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()
}
