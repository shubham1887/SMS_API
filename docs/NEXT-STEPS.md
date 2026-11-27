# Next steps after deployment

API is live at `https://webwhatsapp.finocore.net` (IIS, folder `C:\inetpub\wwwroot\webwhatsapp`).
Production keys are in `C:\inetpub\wwwroot\webwhatsapp\appsettings.Production.json`.

## 1. Install the Android app

Ready-made signed APK (built from this repo) is hosted by the API itself:

```
https://webwhatsapp.finocore.net/app/SmsGateway-v1.0.1.apk
```

Open that link on the phone, install (allow "unknown sources"), then continue with section 2.
Any file placed in `C:\inetpub\wwwroot\webwhatsapp\app\` is served under `/app/`.

To rebuild the APK yourself:

1. Install Android Studio (Hedgehog 2023.1 or newer) on any PC. Open the folder `android/SmsGatewayApp`.
2. Let Gradle sync finish (first time downloads ~1 GB). If asked, accept the Gradle wrapper setup.
3. **Build > Build Bundle(s) / APK(s) > Build APK(s)**. The APK is at `app/build/outputs/apk/debug/app-debug.apk`.
   For a release APK: **Build > Generate Signed Bundle / APK**, choose APK, create a keystore once, keep it safe.
   Command line (server build): keystore at `C:\Android\keystore\smsgateway.jks`, settings in `C:\Android\keystore\keystore.properties`,
   then `set KEYSTORE_PROPERTIES=C:\Android\keystore\keystore.properties` and `gradlew assembleRelease` in `android/SmsGatewayApp`.
   Updates must be signed with the same keystore, otherwise the phone refuses to install over the old version.
4. Copy the APK to the phone (USB, WhatsApp, Drive) and install it. Allow "install from unknown sources" when asked.

## 2. Configure the phone

1. Open **SMS Gateway** app.
2. Tap **Permissions** and allow SMS, Phone and Notifications.
3. Tap **Battery** and choose "Allow" / "Don't optimise" so Android does not kill the service.
4. Server URL: `https://webwhatsapp.finocore.net`
5. Device key: the `Seed:DeviceKey` value from `appsettings.Production.json` (device "Phone-1").
   For a second phone create a new device first: `POST /api/admin/devices` with `X-Admin-Key`, and use the returned `deviceKey`.
6. Choose the SIM, tap **Test connection** (log must show `Connected as 'Phone-1'`), then **START GATEWAY**.
7. Keep the phone on charger and Wi-Fi/4G. The notification "SMS Gateway running" must stay visible.

Phone-specific settings that kill background apps (do this once):
- Xiaomi/Redmi: Settings > Apps > SMS Gateway > Autostart ON, Battery saver "No restrictions".
- Vivo/Oppo/Realme: Settings > Battery > Background power consumption > allow; also "Auto start" in app settings.
- Samsung: Settings > Battery > Background usage limits > remove the app from "Sleeping apps", add to "Never sleeping apps".

## 3. Test end to end

1. Send one SMS to your own number from Swagger (`https://webwhatsapp.finocore.net/swagger`, Authorize with the client key) or just open this in a browser:
   ```
   https://webwhatsapp.finocore.net/api/sms/send?apiKey=<client key>&to=<your number>&message=Test+from+gateway&reference=test-1
   ```
2. Within ~5 s the phone log shows `Claimed 1 message(s)` → `Sending → +91…` → `Sent ✓` → `Delivered ✓`.
3. Check status: `https://webwhatsapp.finocore.net/api/sms/status?apiKey=<client key>&reference=test-1` → `"status":"Delivered"`.
4. Admin view: `GET /api/admin/dashboard` with `X-Admin-Key` shows the phone online and today's counts.
5. Failure test: switch the phone to airplane mode, send another SMS. The phone reports `Failed`, the server schedules a retry (30 s, 60 s, 120 s), and once the network is back it goes out. After 3 failed attempts the message is `Failed` permanently.

If something does not work:
- Phone log "Server error 401": wrong device key.
- Phone log "Network error": phone cannot reach the URL, check internet / URL spelling (https, no trailing slash).
- Message stays `Pending`: no phone online, or the phone hit `MaxPerMinute` / `DailyLimit` (see dashboard).
- Message `Assigned` for long: phone stopped mid-send; after 3 min it is re-queued automatically.
- Android shows "App is sending a large number of SMS": tap Allow (after ~30 SMS/30 min).
- Server logs: `C:\inetpub\wwwroot\webwhatsapp\logs\`.

## 4. Finocore integration

Simplest: one HTTP GET per SMS.

```
https://webwhatsapp.finocore.net/api/sms/send?apiKey=<client key>&to=9876543210&message=<url-encoded text>&priority=10&reference=<your unique id>
```

- `priority=10` for OTP, `0` for normal messages.
- `reference` = your own unique id (e.g. `otp-<userId>-<ts>`). Sending the same reference twice never sends twice.
- Response `202` with `id`; status later via `.../api/sms/status?apiKey=...&reference=<your id>`.
- Add `&callbackUrl=https://api.finocore.net/sms/callback` to receive Sent/Delivered/Failed pushes instead of polling.

C# example (works with `HttpClient`):

```csharp
var url = $"https://webwhatsapp.finocore.net/api/sms/send?apiKey={key}&to={Uri.EscapeDataString(mobile)}" +
          $"&message={Uri.EscapeDataString(text)}&priority=10&reference={Uri.EscapeDataString(reference)}";
var res = await http.GetAsync(url);
var json = await res.Content.ReadAsStringAsync();   // { "id": "...", "status": "Pending", ... }
```

Full typed client: `samples/SmsGatewayClient.cs` (POST + webhook model).

## 5. Operations

- Add phone: `POST /api/admin/devices` → key shown once.
- Disable phone: `PUT /api/admin/devices/{id}` with `{ "isActive": false }`.
- Rotate a leaked key: `POST /api/admin/clients/{id}/rotate-key` or `/devices/{id}/rotate-key`.
- Retry failed: `POST /api/admin/messages/{id}/retry`.
- Redeploy after code change:
  ```
  dotnet publish src/SmsGateway.Api -c Release -o publish
  %windir%\system32\inetsrv\appcmd stop apppool "webwhatsapp.ripay.co.in"
  xcopy publish C:\inetpub\wwwroot\webwhatsapp /E /Y
  %windir%\system32\inetsrv\appcmd start apppool "webwhatsapp.ripay.co.in"
  ```
  Never overwrite `appsettings.Production.json`.
