using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmsGateway.Api.Contracts;
using SmsGateway.Api.Data;
using SmsGateway.Api.Domain;
using SmsGateway.Api.Security;
using SmsGateway.Api.Services;

namespace SmsGateway.Api.Controllers;

/// <summary>Endpoints used by the Android gateway app. Auth: X-Device-Key.</summary>
[ApiController]
[Route("api/device")]
[Authorize(Policy = "Device")]
[Produces("application/json")]
public class DeviceController(
    AppDbContext db,
    SmsQueueService queue,
    TimeHelper time,
    IOptions<GatewayOptions> options) : ControllerBase
{
    /// <summary>Phone reports it is alive; server returns runtime settings.</summary>
    [HttpPost("heartbeat")]
    public async Task<ActionResult<HeartbeatResponse>> Heartbeat([FromBody] HeartbeatRequest? req, CancellationToken ct)
    {
        var deviceId = User.DeviceId();
        var device = await db.Devices.FirstAsync(d => d.Id == deviceId, ct);
        var now = time.UtcNow;

        device.LastSeenAt = now;
        if (req is not null)
        {
            if (req.BatteryLevel is not null) device.BatteryLevel = req.BatteryLevel;
            if (!string.IsNullOrWhiteSpace(req.AppVersion)) device.AppVersion = req.AppVersion.Trim();
            if (!string.IsNullOrWhiteSpace(req.SimInfo)) device.SimInfo = req.SimInfo.Trim();
            if (!string.IsNullOrWhiteSpace(req.PhoneNumber) && string.IsNullOrWhiteSpace(device.PhoneNumber))
                device.PhoneNumber = req.PhoneNumber.Trim();
        }
        var today = time.LocalToday;
        if (device.SentTodayDate != today) { device.SentToday = 0; device.SentTodayDate = today; }
        await db.SaveChangesAsync(ct);

        var pending = await db.SmsMessages.CountAsync(m =>
            m.Status == SmsStatus.Pending && (m.NextRetryAt == null || m.NextRetryAt <= now) &&
            (m.PreferredDeviceId == null || m.PreferredDeviceId == deviceId), ct);

        var o = options.Value;
        return new HeartbeatResponse(device.Id, device.Name, now, o.DevicePollIntervalSeconds, o.DeviceMaxBatch,
            device.MaxPerMinute, device.DailyLimit, device.SentToday, pending);
    }

    /// <summary>Claim the next batch of messages to send. Each message is handed to exactly one device.</summary>
    [HttpGet("messages")]
    public async Task<ActionResult<List<DeviceSmsItem>>> Claim([FromQuery] int max = 5, CancellationToken ct = default)
    {
        var messages = await queue.ClaimAsync(User.DeviceId(), max, ct);
        return messages.Select(m => new DeviceSmsItem(m.Id, m.ToNumber, m.Body, m.Priority, m.Attempts)).ToList();
    }

    /// <summary>Report Sent / Delivered / Failed for one message.</summary>
    [HttpPost("messages/{id:guid}/status")]
    public async Task<IActionResult> Report(Guid id, [FromBody] DeviceStatusReport req, CancellationToken ct)
    {
        var (msg, error) = await queue.ReportStatusAsync(User.DeviceId(), id, req.Status, req.Error, req.Timestamp, ct);
        if (msg is null) return BadRequest(new { error });
        return Ok(new { msg.Id, status = msg.Status.ToString(), msg.Attempts, msg.NextRetryAt });
    }

    /// <summary>Report several statuses at once (used when the phone was offline).</summary>
    [HttpPost("messages/status")]
    public async Task<IActionResult> ReportBulk([FromBody] List<DeviceStatusBulkItem> items, CancellationToken ct)
    {
        var deviceId = User.DeviceId();
        var results = new List<object>(items.Count);
        foreach (var item in items)
        {
            var (msg, error) = await queue.ReportStatusAsync(deviceId, item.Id, item.Status, item.Error, item.Timestamp, ct);
            results.Add(new { item.Id, ok = msg is not null, status = msg?.Status.ToString(), error });
        }
        return Ok(results);
    }
}
