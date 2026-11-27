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

/// <summary>Management endpoints. Auth: X-Admin-Key (configuration Admin:Key).</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = "Admin")]
[Produces("application/json")]
public class AdminController(
    AppDbContext db,
    SmsQueueService queue,
    TimeHelper time,
    IOptions<GatewayOptions> options) : ControllerBase
{
    private int OfflineAfter => options.Value.DeviceOfflineAfterSeconds;

    // ------------------------------------------------------------ dashboard

    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardResponse>> Dashboard(CancellationToken ct)
    {
        var now = time.UtcNow;
        var todayStart = time.LocalDayStartUtc(time.LocalToday);

        var devices = await db.Devices.AsNoTracking().OrderBy(d => d.Id).ToListAsync(ct);
        var deviceDtos = devices.Select(d => DeviceResponse.From(d, now, OfflineAfter)).ToList();

        return new DashboardResponse(
            now,
            await db.SmsMessages.CountAsync(m => m.Status == SmsStatus.Pending, ct),
            await db.SmsMessages.CountAsync(m => m.Status == SmsStatus.Assigned, ct),
            devices.Count,
            deviceDtos.Count(d => d.IsOnline),
            await StatsQuery.ComputeAsync(db.SmsMessages, todayStart, todayStart.AddDays(1), ct),
            await StatsQuery.ComputeAsync(db.SmsMessages, todayStart.AddDays(-6), todayStart.AddDays(1), ct),
            deviceDtos);
    }

    // ------------------------------------------------------------ API clients

    [HttpGet("clients")]
    public async Task<ActionResult<List<ClientResponse>>> Clients(CancellationToken ct)
        => (await db.ApiClients.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct)).Select(ClientResponse.From).ToList();

    /// <summary>Create an API client. The key is returned ONCE; store it safely.</summary>
    [HttpPost("clients")]
    public async Task<ActionResult<ClientCreatedResponse>> CreateClient([FromBody] CreateClientRequest req, CancellationToken ct)
    {
        var key = KeyUtil.Generate("sk");
        var client = new ApiClient
        {
            Name = req.Name.Trim(),
            KeyHash = KeyUtil.Hash(key),
            KeyPrefix = KeyUtil.Prefix(key),
            IsActive = true,
            CreatedAt = time.UtcNow
        };
        db.ApiClients.Add(client);
        await db.SaveChangesAsync(ct);
        return new ClientCreatedResponse(ClientResponse.From(client), key);
    }

    [HttpPut("clients/{id:int}")]
    public async Task<ActionResult<ClientResponse>> UpdateClient(int id, [FromBody] UpdateClientRequest req, CancellationToken ct)
    {
        var client = await db.ApiClients.FindAsync([id], ct);
        if (client is null) return NotFound(new { error = "Client not found." });
        if (!string.IsNullOrWhiteSpace(req.Name)) client.Name = req.Name.Trim();
        if (req.IsActive is not null) client.IsActive = req.IsActive.Value;
        await db.SaveChangesAsync(ct);
        return ClientResponse.From(client);
    }

    [HttpPost("clients/{id:int}/rotate-key")]
    public async Task<ActionResult<ClientCreatedResponse>> RotateClientKey(int id, CancellationToken ct)
    {
        var client = await db.ApiClients.FindAsync([id], ct);
        if (client is null) return NotFound(new { error = "Client not found." });
        var key = KeyUtil.Generate("sk");
        client.KeyHash = KeyUtil.Hash(key);
        client.KeyPrefix = KeyUtil.Prefix(key);
        await db.SaveChangesAsync(ct);
        return new ClientCreatedResponse(ClientResponse.From(client), key);
    }

    [HttpDelete("clients/{id:int}")]
    public async Task<IActionResult> DeleteClient(int id, CancellationToken ct)
    {
        var client = await db.ApiClients.FindAsync([id], ct);
        if (client is null) return NotFound(new { error = "Client not found." });
        if (await db.SmsMessages.AnyAsync(m => m.ApiClientId == id, ct))
            return BadRequest(new { error = "Client has messages; disable it instead (PUT IsActive=false)." });
        db.ApiClients.Remove(client);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ------------------------------------------------------------ devices

    [HttpGet("devices")]
    public async Task<ActionResult<List<DeviceResponse>>> Devices(CancellationToken ct)
    {
        var now = time.UtcNow;
        return (await db.Devices.AsNoTracking().OrderBy(d => d.Id).ToListAsync(ct))
            .Select(d => DeviceResponse.From(d, now, OfflineAfter)).ToList();
    }

    /// <summary>Register an Android phone. The device key is returned ONCE; enter it in the app.</summary>
    [HttpPost("devices")]
    public async Task<ActionResult<DeviceCreatedResponse>> CreateDevice([FromBody] CreateDeviceRequest req, CancellationToken ct)
    {
        var key = KeyUtil.Generate("dk");
        var now = time.UtcNow;
        var device = new Device
        {
            Name = req.Name.Trim(),
            PhoneNumber = req.PhoneNumber?.Trim(),
            KeyHash = KeyUtil.Hash(key),
            KeyPrefix = KeyUtil.Prefix(key),
            IsActive = true,
            MaxPerMinute = req.MaxPerMinute,
            DailyLimit = req.DailyLimit,
            SentTodayDate = time.LocalToday,
            CreatedAt = now
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);
        return new DeviceCreatedResponse(DeviceResponse.From(device, now, OfflineAfter), key);
    }

    [HttpPut("devices/{id:int}")]
    public async Task<ActionResult<DeviceResponse>> UpdateDevice(int id, [FromBody] UpdateDeviceRequest req, CancellationToken ct)
    {
        var device = await db.Devices.FindAsync([id], ct);
        if (device is null) return NotFound(new { error = "Device not found." });
        if (!string.IsNullOrWhiteSpace(req.Name)) device.Name = req.Name.Trim();
        if (req.PhoneNumber is not null) device.PhoneNumber = req.PhoneNumber.Trim();
        if (req.IsActive is not null) device.IsActive = req.IsActive.Value;
        if (req.MaxPerMinute is > 0) device.MaxPerMinute = req.MaxPerMinute.Value;
        if (req.DailyLimit is >= 0) device.DailyLimit = req.DailyLimit.Value;
        await db.SaveChangesAsync(ct);
        return DeviceResponse.From(device, time.UtcNow, OfflineAfter);
    }

    [HttpPost("devices/{id:int}/rotate-key")]
    public async Task<ActionResult<DeviceCreatedResponse>> RotateDeviceKey(int id, CancellationToken ct)
    {
        var device = await db.Devices.FindAsync([id], ct);
        if (device is null) return NotFound(new { error = "Device not found." });
        var key = KeyUtil.Generate("dk");
        device.KeyHash = KeyUtil.Hash(key);
        device.KeyPrefix = KeyUtil.Prefix(key);
        await db.SaveChangesAsync(ct);
        return new DeviceCreatedResponse(DeviceResponse.From(device, time.UtcNow, OfflineAfter), key);
    }

    [HttpDelete("devices/{id:int}")]
    public async Task<IActionResult> DeleteDevice(int id, CancellationToken ct)
    {
        var device = await db.Devices.FindAsync([id], ct);
        if (device is null) return NotFound(new { error = "Device not found." });
        db.Devices.Remove(device); // messages keep history, DeviceId becomes NULL
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ------------------------------------------------------------ messages

    [HttpGet("messages")]
    public async Task<ActionResult<PagedResponse<SmsResponse>>> Messages(
        [FromQuery] SmsStatus? status, [FromQuery] int? clientId, [FromQuery] int? deviceId,
        [FromQuery] string? to, [FromQuery] string? reference, [FromQuery] DateTime? from, [FromQuery] DateTime? until,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var q = db.SmsMessages.Include(m => m.Device).AsNoTracking().AsQueryable();
        if (status is not null) q = q.Where(m => m.Status == status);
        if (clientId is not null) q = q.Where(m => m.ApiClientId == clientId);
        if (deviceId is not null) q = q.Where(m => m.DeviceId == deviceId);
        if (!string.IsNullOrWhiteSpace(to)) q = q.Where(m => m.ToNumber.Contains(to));
        if (!string.IsNullOrWhiteSpace(reference)) q = q.Where(m => m.ClientReference == reference);
        if (from is not null) q = q.Where(m => m.CreatedAt >= from);
        if (until is not null) q = q.Where(m => m.CreatedAt < until);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(m => m.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResponse<SmsResponse>(page, pageSize, total, items.Select(m => SmsResponse.From(m)).ToList());
    }

    [HttpGet("messages/{id:guid}")]
    public async Task<IActionResult> Message(Guid id, CancellationToken ct)
    {
        var msg = await db.SmsMessages.Include(m => m.Device).Include(m => m.ApiClient).AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg is null) return NotFound(new { error = "Message not found." });
        var events = await db.SmsEvents.AsNoTracking().Where(e => e.SmsMessageId == id).OrderBy(e => e.Id)
            .Select(e => new EventResponse(e.Id, e.Event, e.Details, e.DeviceId, e.CreatedAt)).ToListAsync(ct);
        return Ok(new { message = SmsResponse.From(msg, includeBody: true), client = msg.ApiClient?.Name, events });
    }

    [HttpGet("messages/{id:guid}/events")]
    public async Task<ActionResult<List<EventResponse>>> Events(Guid id, CancellationToken ct)
        => await db.SmsEvents.AsNoTracking().Where(e => e.SmsMessageId == id).OrderBy(e => e.Id)
            .Select(e => new EventResponse(e.Id, e.Event, e.Details, e.DeviceId, e.CreatedAt)).ToListAsync(ct);

    /// <summary>Put a Failed/Cancelled message back in the queue with a fresh attempt counter.</summary>
    [HttpPost("messages/{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        var (msg, error) = await queue.RetryAsync(id, ct);
        return msg is null ? BadRequest(new { error }) : Ok(SmsResponse.From(msg));
    }

    [HttpPost("messages/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var (msg, error) = await queue.CancelAsync(id, null, ct);
        return msg is null ? BadRequest(new { error }) : Ok(SmsResponse.From(msg));
    }
}
