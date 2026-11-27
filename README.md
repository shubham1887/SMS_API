# SMS Gateway (ASP.NET Core 9 + Android)

Send SMS from your own application through your own Android phones and SIM cards.
No DLT / third-party SMS provider needed for low volume (OTP, alerts, reminders).

```
Finocore / any app  --X-Api-Key-->  SmsGateway.Api (SQL Server queue)  <--X-Device-Key--  Android Gateway App  -->  SIM  -->  Customer
```

| Part | Path | Status |
|------|------|--------|
| ASP.NET Core 9 Web API | `src/SmsGateway.Api` | built and tested end-to-end locally |
| SQL Server schema | `database/SmsGateway_Schema.sql` (also auto-migrated on startup) | tested |
| Android gateway app (Kotlin) | `android/SmsGatewayApp` | source complete, build it in Android Studio |
| Finocore integration sample | `samples/SmsGatewayClient.cs` | drop-in C# client |
| API reference | `docs/API.md` | |

## Features

- **API key security**: three separate keys. Clients use `X-Api-Key`, phones use `X-Device-Key`, management uses `X-Admin-Key`. Only SHA-256 hashes are stored.
- **Queue with priority**: OTPs (priority 10) go before marketing (priority 0).
- **Retry with backoff**: failed sends retry 30s, 60s, 120s… up to `MaxAttempts` (default 3). Messages a phone claimed but never reported are re-queued automatically.
- **Multiple phones / load balancing**: every online phone pulls from the same queue. A message is handed to exactly one phone (SQL `UPDLOCK, READPAST`). Per-phone limits: `MaxPerMinute` and `DailyLimit`. Optional `deviceId` on a message forces a specific SIM.
- **Delivery status**: `Pending → Assigned → Sent → Delivered` (or `Failed` / `Cancelled`), with timestamps and a full event log per message.
- **Webhooks**: pass `callbackUrl` and the gateway POSTs every status change to your app.
- **Idempotency**: pass `reference` (your own id). Re-sending the same reference returns the existing message instead of sending twice.
- **Logging**: Serilog to console + rolling file `logs/`, every state change saved in `SmsEvents` table.
- **Swagger UI** at `/swagger`, health check at `/health`.

## 1. Run the API

Requirements: .NET 9 SDK, SQL Server (local SQLEXPRESS works).

1. Edit `src/SmsGateway.Api/appsettings.json`:
   - `ConnectionStrings:Default` – your SQL Server.
   - `Admin:Key` – change it.
   - `Seed:ClientApiKey` / `Seed:DeviceKey` – change them. They are inserted only when the tables are empty (first run).
2. Start:

```bash
cd src/SmsGateway.Api
dotnet run
```

The database `SmsGateway` is created automatically (EF migrations). API listens on `http://0.0.0.0:5080` so phones on the same network can reach it. Open `http://localhost:5080/swagger`.

Windows firewall: allow inbound TCP 5080 (`netsh advfirewall firewall add rule name="SMS Gateway" dir=in action=allow protocol=TCP localport=5080`).

For production, publish and host behind IIS or as a Windows service:

```bash
dotnet publish src/SmsGateway.Api -c Release -o publish
```

## 2. Register a phone

```bash
curl -X POST http://localhost:5080/api/admin/devices -H "X-Admin-Key: <admin key>" -H "Content-Type: application/json" -d "{\"name\":\"Phone-2\",\"maxPerMinute\":10,\"dailyLimit\":300}"
```

The response contains `deviceKey` **once**. Copy it into the Android app. (A first device `Phone-1` is already seeded with `Seed:DeviceKey`.)

## 3. Install the Android app

Signed APK download (served by the API from `C:\inetpub\wwwroot\webwhatsapp\app\`): `https://webwhatsapp.finocore.net/app/SmsGateway-v1.0.1.apk`

To build it yourself:

1. Open `android/SmsGatewayApp` in Android Studio (Hedgehog or newer). Let it sync Gradle, then **Build > Build APK** and install on the phone.
2. In the app: tap **Permissions** (SMS, phone state, notifications), tap **Battery** and allow "not optimised" so the service is not killed.
3. Enter **Server URL** (`http://<PC-IP>:5080`), **Device key**, pick the **SIM**, tap **Test connection**, then **START GATEWAY**.
4. A persistent notification shows the service is running. It restarts after reboot when "Auto start" is ticked.

Android notes:
- Android asks "App is sending a large number of SMS" after ~30 SMS in 30 minutes for apps that are not the default SMS app. Tap **Allow** (you can tick "always"). Keep `MaxPerMinute` modest (5–10) to stay under operator spam filters.
- Keep the phone on charger and Wi-Fi. The app holds a partial wake lock while running.
- Dual-SIM: choose the SIM in the app; each phone/SIM should be its own device in the API.

## 4. Send from your application

```bash
curl -X POST http://localhost:5080/api/sms/send \
  -H "X-Api-Key: <client key>" -H "Content-Type: application/json" \
  -d "{\"to\":\"9876543210\",\"message\":\"Your OTP is 4821\",\"priority\":10,\"reference\":\"otp-4821\",\"callbackUrl\":\"https://finocore.example.com/sms/callback\"}"
```

Response `202 Accepted`:

```json
{ "id": "01a1c9f3-cb28-71e2-9669-afc40fb9cd40", "to": "+919876543210", "status": "Pending", "priority": 10, "attempts": 0, "maxAttempts": 3, "reference": "otp-4821", "createdAt": "2026-11-11T09:32:40Z" }
```

Simple GET version (no JSON body, key in the URL):

```
GET https://<host>/api/sms/send?apiKey=<client key>&to=9876543210&message=Your+OTP+is+4821&priority=10&reference=otp-4821
GET https://<host>/api/sms/status?apiKey=<client key>&reference=otp-4821
```

Track it: `GET /api/sms/{id}` or `GET /api/sms/by-reference/otp-4821`. Full endpoint list in `docs/API.md`. C# client for Finocore in `samples/SmsGatewayClient.cs`.

## Configuration (`Gateway` section)

| Key | Default | Meaning |
|-----|---------|---------|
| `DefaultCountryCode` | `+91` | Added to 10-digit numbers |
| `DefaultMaxAttempts` | 3 | Send attempts before `Failed` |
| `RetryBaseSeconds` | 30 | Backoff: 30s, 60s, 120s… |
| `StaleAssignmentMinutes` | 3 | Re-queue if phone does not report |
| `MessageExpiryHours` | 24 | Pending older than this is failed |
| `DevicePollIntervalSeconds` | 5 | How often phones poll |
| `DeviceMaxBatch` | 5 | Messages per poll |
| `DeviceOfflineAfterSeconds` | 120 | Online/offline threshold on dashboard |
| `TimeZone` | India Standard Time | Daily limit reset |

## Project layout

```
src/SmsGateway.Api
  Controllers/   SmsController (clients), DeviceController (phones), AdminController
  Services/      SmsQueueService (enqueue / claim / status / retry), MaintenanceWorker, WebhookWorker
  Security/      KeyAuthHandler (3 API-key schemes), KeyUtil
  Data/          AppDbContext, Migrations, DbInitializer (migrate + seed)
  Domain/        ApiClient, Device, SmsMessage, SmsEvent
android/SmsGatewayApp/app/src/main/java/com/smsgateway/app
  SmsGatewayService   foreground service: heartbeat, claim, send, report
  SmsSender           SmsManager + SENT/DELIVERED PendingIntents, dual SIM
  SmsStatusReceiver   receives results, queues reports
  ReportQueue         persisted offline queue of status reports
  ApiClient           OkHttp client for /api/device
  MainActivity        settings, permissions, log
```
