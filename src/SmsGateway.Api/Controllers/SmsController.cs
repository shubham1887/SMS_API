using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmsGateway.Api.Contracts;
using SmsGateway.Api.Data;
using SmsGateway.Api.Domain;
using SmsGateway.Api.Security;
using SmsGateway.Api.Services;

namespace SmsGateway.Api.Controllers;

/// <summary>Endpoints used by consumer applications (Finocore etc.). Auth: X-Api-Key.</summary>
[ApiController]
[Route("api/sms")]
[Authorize(Policy = "Client")]
[Produces("application/json")]
public class SmsController(AppDbContext db, SmsQueueService queue, TimeHelper time) : ControllerBase
{
    /// <summary>Queue a single SMS. Returns 202 with the message id to track.</summary>
    [HttpPost("send")]
    [ProducesResponseType<SmsResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Send([FromBody] SendSmsRequest req, CancellationToken ct)
    {
        var (msg, error) = await queue.EnqueueAsync(User.ClientId(), req, ct);
        if (msg is null) return BadRequest(new { error });
        return AcceptedAtAction(nameof(Get), new { id = msg.Id }, SmsResponse.From(msg));
    }

    /// <summary>
    /// Simple GET version for quick integrations:
    /// /api/sms/send?apiKey=KEY&amp;to=9876543210&amp;message=Hello&amp;priority=10&amp;reference=otp-1
    /// </summary>
    [HttpGet("send")]
    [ProducesResponseType<SmsResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendViaGet(
        [FromQuery] string? to, [FromQuery] string? message, [FromQuery] int priority = 0,
        [FromQuery] string? reference = null, [FromQuery] string? callbackUrl = null,
        [FromQuery] int? deviceId = null, CancellationToken ct = default)
    {
        var req = new SendSmsRequest(to, message, priority, reference, callbackUrl, deviceId);
        var (msg, error) = await queue.EnqueueAsync(User.ClientId(), req, ct);
        if (msg is null) return BadRequest(new { error });
        return AcceptedAtAction(nameof(Get), new { id = msg.Id }, SmsResponse.From(msg));
    }

    /// <summary>Status by id or reference via GET: /api/sms/status?apiKey=KEY&amp;id=... or &amp;reference=...</summary>
    [HttpGet("status")]
    public async Task<IActionResult> StatusViaGet([FromQuery] Guid? id, [FromQuery] string? reference, CancellationToken ct)
    {
        if (id is null && string.IsNullOrWhiteSpace(reference)) return BadRequest(new { error = "Pass id or reference." });
        var clientId = User.ClientId();
        var msg = await db.SmsMessages.Include(m => m.Device).AsNoTracking()
            .FirstOrDefaultAsync(m => m.ApiClientId == clientId && (id != null ? m.Id == id : m.ClientReference == reference), ct);
        return msg is null ? NotFound(new { error = "Message not found." }) : Ok(SmsResponse.From(msg, includeBody: true));
    }

    /// <summary>Queue up to 500 SMS in one call. Each item is accepted or rejected independently.</summary>
    [HttpPost("send-bulk")]
    public async Task<IActionResult> SendBulk([FromBody] SendBulkSmsRequest req, CancellationToken ct)
    {
        var clientId = User.ClientId();
        var results = new List<BulkItemResult>(req.Messages.Count);
        for (var i = 0; i < req.Messages.Count; i++)
        {
            var (msg, error) = await queue.EnqueueAsync(clientId, req.Messages[i], ct);
            results.Add(new BulkItemResult(i, msg is not null, msg is null ? null : SmsResponse.From(msg), error));
        }
        return Ok(new
        {
            accepted = results.Count(r => r.Accepted),
            rejected = results.Count(r => !r.Accepted),
            items = results
        });
    }

    /// <summary>Get current status of a message.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<SmsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var clientId = User.ClientId();
        var msg = await db.SmsMessages.Include(m => m.Device).AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id && m.ApiClientId == clientId, ct);
        return msg is null ? NotFound(new { error = "Message not found." }) : Ok(SmsResponse.From(msg, includeBody: true));
    }

    /// <summary>Look up a message by your own reference.</summary>
    [HttpGet("by-reference/{reference}")]
    public async Task<IActionResult> GetByReference(string reference, CancellationToken ct)
    {
        var clientId = User.ClientId();
        var msg = await db.SmsMessages.Include(m => m.Device).AsNoTracking()
            .FirstOrDefaultAsync(m => m.ClientReference == reference && m.ApiClientId == clientId, ct);
        return msg is null ? NotFound(new { error = "Message not found." }) : Ok(SmsResponse.From(msg, includeBody: true));
    }

    /// <summary>List your messages with optional filters.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResponse<SmsResponse>>> List(
        [FromQuery] SmsStatus? status, [FromQuery] string? to, [FromQuery] DateTime? from, [FromQuery] DateTime? until,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var clientId = User.ClientId();

        var q = db.SmsMessages.Include(m => m.Device).AsNoTracking().Where(m => m.ApiClientId == clientId);
        if (status is not null) q = q.Where(m => m.Status == status);
        if (!string.IsNullOrWhiteSpace(to)) q = q.Where(m => m.ToNumber.Contains(to));
        if (from is not null) q = q.Where(m => m.CreatedAt >= from);
        if (until is not null) q = q.Where(m => m.CreatedAt < until);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(m => m.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResponse<SmsResponse>(page, pageSize, total, items.Select(m => SmsResponse.From(m)).ToList());
    }

    /// <summary>Counts by status for a date range (defaults to today, IST).</summary>
    [HttpGet("stats")]
    public async Task<ActionResult<SmsStatsResponse>> Stats([FromQuery] DateTime? from, [FromQuery] DateTime? until, CancellationToken ct)
    {
        var clientId = User.ClientId();
        var f = from ?? time.LocalDayStartUtc(time.LocalToday);
        var u = until ?? f.AddDays(1);
        return await StatsQuery.ComputeAsync(db.SmsMessages.Where(m => m.ApiClientId == clientId), f, u, ct);
    }

    /// <summary>Cancel a message that is still Pending in the queue.</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var (msg, error) = await queue.CancelAsync(id, User.ClientId(), ct);
        return msg is null ? BadRequest(new { error }) : Ok(SmsResponse.From(msg));
    }
}
