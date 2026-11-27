# SMS Gateway API reference

Base URL: `http://<host>:5080`. All bodies are JSON. Swagger UI: `/swagger`.

Errors: `400 { "error": "..." }`, `401 { "error": "Unauthorized. Send a valid X-Api-Key header." }`, `404 { "error": "..." }`.

## Message status

| Status | Meaning |
|--------|---------|
| `Pending` | In queue, waiting for a phone (or waiting for `nextRetryAt`) |
| `Assigned` | A phone has picked it up and is sending |
| `Sent` | Left the SIM (SmsManager reported success) |
| `Delivered` | Operator delivery report received |
| `Failed` | All attempts failed, expired, or delivery failed |
| `Cancelled` | Cancelled by client/admin while Pending |

## Client endpoints — header `X-Api-Key` (or `Authorization: Bearer <key>`)

### POST `/api/sms/send`
```json
{
  "to": "9876543210",            // 10 digits, 0xxxxxxxxxx, 91xxxxxxxxxx or +91xxxxxxxxxx
  "message": "Your OTP is 4821", // max 1000 chars, long texts are sent multipart
  "priority": 10,                // 0..100, higher first (optional, default 0)
  "reference": "otp-4821",       // optional, unique per client: idempotency key
  "callbackUrl": "https://...",  // optional webhook
  "deviceId": 2,                 // optional: force a specific phone/SIM
  "maxAttempts": 3               // optional 1..10
}
```
`202 Accepted` → message object. Re-sending the same `reference` returns the existing message (`202`, same id).

### GET `/api/sms/send` (simple URL integration)
```
GET /api/sms/send?apiKey=KEY&to=9876543210&message=Your+OTP+is+4821&priority=10&reference=otp-4821
```
Same parameters and response as the POST version. `message` must be URL-encoded. The key may also be sent as a header. The API key is accepted as `apiKey`, `api_key` or `key` query parameter on every client endpoint.

### GET `/api/sms/status?apiKey=KEY&id=...` or `&reference=...`
Returns the message object (same as `GET /api/sms/{id}`).

### POST `/api/sms/send-bulk`
`{ "messages": [ {…}, {…} ] }` (max 500). `200` → `{ "accepted": n, "rejected": n, "items": [ { "index", "accepted", "sms", "error" } ] }`. Each item is validated separately.

### GET `/api/sms/{id}` · GET `/api/sms/by-reference/{reference}`
Message object including `message` text, `deviceName`, timestamps, `error`, `nextRetryAt`.

### GET `/api/sms?status=&to=&from=&until=&page=1&pageSize=50`
Paged list of your messages (`status` = Pending|Assigned|Sent|Delivered|Failed|Cancelled).

### GET `/api/sms/stats?from=&until=`
Counts by status; defaults to today (IST).

### POST `/api/sms/{id}/cancel`
Only while `Pending`.

### Webhook (when `callbackUrl` given)
POST on every change to Sent / Delivered / Failed, headers `X-Gateway-Event: sms.status`, `X-Gateway-Message-Id`:
```json
{ "id": "…", "reference": "otp-4821", "to": "+919876543210", "status": "Delivered", "error": null,
  "attempts": 1, "deviceId": 1, "deviceName": "Phone-1", "sentAt": "…", "deliveredAt": "…", "failedAt": null, "timestamp": "…" }
```
Return any 2xx. Retried 3 times (5s, 10s) otherwise; result stored as `WebhookSent` / `WebhookFailed` event.

## Device endpoints — header `X-Device-Key` (used by the Android app)

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/api/device/heartbeat` | `{ batteryLevel, appVersion, simInfo, phoneNumber }` → poll interval, batch size, limits, pending count |
| GET | `/api/device/messages?max=5` | Atomically claim next messages: `[ { id, to, message, priority, attempt } ]` |
| POST | `/api/device/messages/{id}/status` | `{ "status": "Sent|Delivered|Failed", "error": "...", "timestamp": "..." }` |
| POST | `/api/device/messages/status` | Bulk: `[ { id, status, error, timestamp } ]` → `[ { id, ok, status, error } ]` |

Claim rules: only `Pending` messages whose `nextRetryAt` has passed and whose `preferredDeviceId` is null or this device; ordered by priority desc, created asc; capped by `DeviceMaxBatch`, the device's `MaxPerMinute` and `DailyLimit`. A claim increments `attempts`.

## Admin endpoints — header `X-Admin-Key`

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/admin/dashboard` | Queue depth, in-flight, device online status, today / 7-day stats |
| GET/POST | `/api/admin/clients` | List / create API client (key returned once) |
| PUT | `/api/admin/clients/{id}` | `{ name, isActive }` |
| POST | `/api/admin/clients/{id}/rotate-key` | New key |
| DELETE | `/api/admin/clients/{id}` | Only when it has no messages |
| GET/POST | `/api/admin/devices` | List / create phone `{ name, phoneNumber, maxPerMinute, dailyLimit }` (key returned once) |
| PUT | `/api/admin/devices/{id}` | `{ name, phoneNumber, isActive, maxPerMinute, dailyLimit }` |
| POST | `/api/admin/devices/{id}/rotate-key` | New key |
| DELETE | `/api/admin/devices/{id}` | History kept, `deviceId` on messages becomes null |
| GET | `/api/admin/messages?status=&clientId=&deviceId=&to=&reference=&from=&until=&page=&pageSize=` | All messages |
| GET | `/api/admin/messages/{id}` | Message + client + event log |
| GET | `/api/admin/messages/{id}/events` | Event log |
| POST | `/api/admin/messages/{id}/retry` | Failed/Cancelled → Pending, attempts reset |
| POST | `/api/admin/messages/{id}/cancel` | Pending → Cancelled |

## Database

Tables: `ApiClients`, `Devices`, `SmsMessages`, `SmsEvents`, `__EFMigrationsHistory`. All timestamps are UTC (`datetime2`).
Script: `database/SmsGateway_Schema.sql` (idempotent, same as the EF migration applied on startup).

Useful queries:
```sql
-- queue right now
SELECT Status, COUNT(*) FROM SmsMessages GROUP BY Status;
-- what each phone did today (IST)
SELECT d.Name, m.Status, COUNT(*) FROM SmsMessages m JOIN Devices d ON d.Id = m.DeviceId
WHERE m.CreatedAt >= DATEADD(hour,-5.5,CAST(CAST(SYSUTCDATETIME() AS date) AS datetime2)) GROUP BY d.Name, m.Status;
-- history of one message
SELECT * FROM SmsEvents WHERE SmsMessageId = '...' ORDER BY Id;
```
