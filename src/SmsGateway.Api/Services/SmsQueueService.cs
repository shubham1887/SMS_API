using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SmsGateway.Api.Contracts;
using SmsGateway.Api.Data;
using SmsGateway.Api.Domain;

namespace SmsGateway.Api.Services;

/// <summary>
/// Core queue logic: enqueue from clients, atomic claim by devices (load balancing),
/// status reporting with retry / backoff.
/// </summary>
public class SmsQueueService(
    AppDbContext db,
    IOptions<GatewayOptions> options,
    PhoneNumberNormalizer phones,
    TimeHelper time,
    WebhookDispatcher webhooks,
    ILogger<SmsQueueService> log)
{
    private GatewayOptions O => options.Value;
    public const int MaxBodyLength = 1000;

    // ------------------------------------------------------------------ enqueue

    public async Task<(SmsMessage? Message, string? Error)> EnqueueAsync(int clientId, SendSmsRequest req, CancellationToken ct)
    {
        var to = phones.Normalize(req.To);
        if (to is null) return (null, $"Invalid phone number '{req.To}'.");

        var body = (req.Message ?? string.Empty).Trim();
        if (body.Length == 0) return (null, "Message is empty.");
        if (body.Length > MaxBodyLength) return (null, $"Message exceeds {MaxBodyLength} characters.");
        if (req.Priority is < 0 or > 100) return (null, "Priority must be between 0 and 100.");
        if (req.MaxAttempts is < 1 or > 10) return (null, "MaxAttempts must be between 1 and 10.");
        if (req.Reference?.Length > 100) return (null, "Reference max length is 100.");
        if (req.CallbackUrl?.Length > 500) return (null, "CallbackUrl max length is 500.");

        if (req.DeviceId is not null)
        {
            var ok = await db.Devices.AnyAsync(d => d.Id == req.DeviceId && d.IsActive, ct);
            if (!ok) return (null, $"Device {req.DeviceId} not found or inactive.");
        }

        if (!string.IsNullOrWhiteSpace(req.CallbackUrl))
        {
            if (!Uri.TryCreate(req.CallbackUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return (null, "CallbackUrl must be an absolute http(s) URL.");
        }

        var reference = string.IsNullOrWhiteSpace(req.Reference) ? null : req.Reference.Trim();
        if (reference is not null)
        {
            var existing = await db.SmsMessages.Include(m => m.Device)
                .FirstOrDefaultAsync(m => m.ApiClientId == clientId && m.ClientReference == reference, ct);
            if (existing is not null) return (existing, null); // idempotent re-submit
        }

        var now = time.UtcNow;
        var msg = new SmsMessage
        {
            Id = Guid.CreateVersion7(),
            ApiClientId = clientId,
            ToNumber = to,
            Body = body,
            Status = SmsStatus.Pending,
            Priority = req.Priority,
            MaxAttempts = req.MaxAttempts ?? O.DefaultMaxAttempts,
            PreferredDeviceId = req.DeviceId,
            ClientReference = reference,
            CallbackUrl = string.IsNullOrWhiteSpace(req.CallbackUrl) ? null : req.CallbackUrl.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SmsMessages.Add(msg);
        db.SmsEvents.Add(NewEvent(msg.Id, null, "Queued", $"priority={msg.Priority}; preferredDevice={msg.PreferredDeviceId?.ToString() ?? "any"}", now));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (reference is not null)
        {
            // Lost a race on the unique (client, reference) index: return the winner.
            db.ChangeTracker.Clear();
            var existing = await db.SmsMessages.Include(m => m.Device)
                .FirstAsync(m => m.ApiClientId == clientId && m.ClientReference == reference, ct);
            return (existing, null);
        }

        return (msg, null);
    }

    // ------------------------------------------------------------------ claim (device side)

    /// <summary>
    /// Atomically hands the next pending messages to the calling device.
    /// Load balancing is implicit: every online phone keeps pulling, and each
    /// message can only be claimed once thanks to UPDLOCK/READPAST.
    /// </summary>
    public async Task<List<SmsMessage>> ClaimAsync(int deviceId, int requested, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null || !device.IsActive) return [];

        var now = time.UtcNow;
        var today = time.LocalToday;
        if (device.SentTodayDate != today)
        {
            device.SentToday = 0;
            device.SentTodayDate = today;
        }
        device.LastSeenAt = now;

        var max = Math.Clamp(requested, 1, O.DeviceMaxBatch);
        if (device.DailyLimit > 0) max = Math.Min(max, device.DailyLimit - device.SentToday);

        if (max > 0)
        {
            var minuteAgo = now.AddMinutes(-1);
            var lastMinute = await db.SmsMessages.CountAsync(m => m.DeviceId == deviceId && m.AssignedAt >= minuteAgo, ct);
            max = Math.Min(max, device.MaxPerMinute - lastMinute);
        }

        if (max <= 0)
        {
            await db.SaveChangesAsync(ct);
            return [];
        }

        var ids = await ClaimIdsAsync(deviceId, max, now, ct);
        if (ids.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return [];
        }

        var messages = await db.SmsMessages
            .Where(m => ids.Contains(m.Id))
            .OrderByDescending(m => m.Priority).ThenBy(m => m.CreatedAt)
            .ToListAsync(ct);

        device.SentToday += messages.Count;
        foreach (var m in messages)
            db.SmsEvents.Add(NewEvent(m.Id, deviceId, "Assigned", $"attempt {m.Attempts}/{m.MaxAttempts} -> {device.Name}", now));

        await db.SaveChangesAsync(ct);
        log.LogInformation("Device {Device} claimed {Count} message(s)", device.Name, messages.Count);
        return messages;
    }

    private async Task<List<Guid>> ClaimIdsAsync(int deviceId, int max, DateTime now, CancellationToken ct)
    {
        const string sql = """
            UPDATE m
               SET Status = @assigned, DeviceId = @deviceId, AssignedAt = @now, UpdatedAt = @now,
                   Attempts = Attempts + 1, NextRetryAt = NULL
            OUTPUT inserted.Id
              FROM SmsMessages m
             WHERE m.Id IN (
                   SELECT TOP (@max) Id
                     FROM SmsMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE Status = @pending
                      AND (NextRetryAt IS NULL OR NextRetryAt <= @now)
                      AND (PreferredDeviceId IS NULL OR PreferredDeviceId = @deviceId)
                    ORDER BY Priority DESC, CreatedAt ASC);
            """;

        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            AddParam(cmd, "@assigned", (int)SmsStatus.Assigned);
            AddParam(cmd, "@pending", (int)SmsStatus.Pending);
            AddParam(cmd, "@deviceId", deviceId);
            AddParam(cmd, "@now", now);
            AddParam(cmd, "@max", max);

            var ids = new List<Guid>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
            return ids;
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    // ------------------------------------------------------------------ status reports

    public async Task<(SmsMessage? Message, string? Error)> ReportStatusAsync(
        int deviceId, Guid id, string status, string? error, DateTime? timestamp, CancellationToken ct)
    {
        var msg = await db.SmsMessages.Include(m => m.Device).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg is null) return (null, "Message not found.");
        if (msg.DeviceId != deviceId) return (null, "Message is not assigned to this device.");

        var now = time.UtcNow;
        var at = timestamp?.ToUniversalTime() ?? now;
        var changed = false;

        switch (status.Trim().ToLowerInvariant())
        {
            case "sent":
                if (msg.Status is SmsStatus.Assigned or SmsStatus.Pending)
                {
                    msg.Status = SmsStatus.Sent;
                    msg.SentAt = at;
                    msg.ErrorMessage = null;
                    db.SmsEvents.Add(NewEvent(msg.Id, deviceId, "Sent", null, now));
                    changed = true;
                }
                break;

            case "delivered":
                if (msg.Status is SmsStatus.Assigned or SmsStatus.Pending or SmsStatus.Sent)
                {
                    msg.Status = SmsStatus.Delivered;
                    msg.SentAt ??= at;
                    msg.DeliveredAt = at;
                    msg.ErrorMessage = null;
                    db.SmsEvents.Add(NewEvent(msg.Id, deviceId, "Delivered", null, now));
                    changed = true;
                }
                break;

            case "failed":
                if (msg.Status is SmsStatus.Assigned or SmsStatus.Pending)
                {
                    ApplyFailure(msg, error ?? "Send failed on device", now, deviceId);
                    changed = true;
                }
                else if (msg.Status is SmsStatus.Sent)
                {
                    // Operator reported delivery failure after the SMS left the SIM: do not resend.
                    msg.Status = SmsStatus.Failed;
                    msg.FailedAt = now;
                    msg.ErrorMessage = Truncate(error ?? "Delivery failed", 500);
                    db.SmsEvents.Add(NewEvent(msg.Id, deviceId, "DeliveryFailed", msg.ErrorMessage, now));
                    changed = true;
                }
                break;

            default:
                return (null, "Status must be Sent, Delivered or Failed.");
        }

        if (changed)
        {
            msg.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            if ((msg.Status is SmsStatus.Sent or SmsStatus.Delivered or SmsStatus.Failed) && msg.CallbackUrl is not null)
                webhooks.Enqueue(msg.Id);
        }
        return (msg, null);
    }

    /// <summary>Retry with exponential backoff, or fail permanently when attempts are exhausted.</summary>
    public void ApplyFailure(SmsMessage msg, string error, DateTime now, int? deviceId)
    {
        error = Truncate(error, 500);
        if (msg.Attempts >= msg.MaxAttempts)
        {
            msg.Status = SmsStatus.Failed;
            msg.FailedAt = now;
            msg.ErrorMessage = error;
            msg.NextRetryAt = null;
            db.SmsEvents.Add(NewEvent(msg.Id, deviceId, "Failed", $"{error} (attempt {msg.Attempts}/{msg.MaxAttempts})", now));
        }
        else
        {
            var delay = TimeSpan.FromSeconds(O.RetryBaseSeconds * Math.Pow(2, Math.Max(0, msg.Attempts - 1)));
            msg.Status = SmsStatus.Pending;
            msg.DeviceId = null;           // let any device pick up the retry
            msg.AssignedAt = null;
            msg.NextRetryAt = now + delay;
            msg.ErrorMessage = error;
            db.SmsEvents.Add(NewEvent(msg.Id, deviceId, "RetryScheduled", $"{error}; retry in {delay.TotalSeconds:0}s (attempt {msg.Attempts}/{msg.MaxAttempts})", now));
        }
        msg.UpdatedAt = now;
    }

    public async Task<(SmsMessage? Message, string? Error)> CancelAsync(Guid id, int? clientId, CancellationToken ct)
    {
        var msg = await db.SmsMessages.Include(m => m.Device).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg is null || (clientId is not null && msg.ApiClientId != clientId)) return (null, "Message not found.");
        if (msg.Status != SmsStatus.Pending) return (null, $"Only Pending messages can be cancelled (current: {msg.Status}).");

        var now = time.UtcNow;
        msg.Status = SmsStatus.Cancelled;
        msg.UpdatedAt = now;
        db.SmsEvents.Add(NewEvent(msg.Id, null, "Cancelled", clientId is null ? "by admin" : "by client", now));
        await db.SaveChangesAsync(ct);
        return (msg, null);
    }

    public async Task<(SmsMessage? Message, string? Error)> RetryAsync(Guid id, CancellationToken ct)
    {
        var msg = await db.SmsMessages.Include(m => m.Device).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg is null) return (null, "Message not found.");
        if (msg.Status is not (SmsStatus.Failed or SmsStatus.Cancelled)) return (null, $"Only Failed/Cancelled messages can be retried (current: {msg.Status}).");

        var now = time.UtcNow;
        msg.Status = SmsStatus.Pending;
        msg.Attempts = 0;
        msg.DeviceId = null;
        msg.AssignedAt = null;
        msg.NextRetryAt = null;
        msg.FailedAt = null;
        msg.ErrorMessage = null;
        msg.UpdatedAt = now;
        db.SmsEvents.Add(NewEvent(msg.Id, null, "ManualRetry", "re-queued by admin", now));
        await db.SaveChangesAsync(ct);
        return (msg, null);
    }

    public static SmsEvent NewEvent(Guid messageId, int? deviceId, string name, string? details, DateTime at) => new()
    {
        SmsMessageId = messageId,
        DeviceId = deviceId,
        Event = name,
        Details = details is null ? null : Truncate(details, 1000),
        CreatedAt = at
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
