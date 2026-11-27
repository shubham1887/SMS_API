using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmsGateway.Api.Data;
using SmsGateway.Api.Domain;

namespace SmsGateway.Api.Services;

/// <summary>
/// Periodic housekeeping:
///  1. Messages a device claimed but never reported on are re-queued (or failed).
///  2. Messages stuck in the queue longer than MessageExpiryHours are failed.
/// </summary>
public class MaintenanceWorker(
    IServiceScopeFactory scopes,
    IOptions<GatewayOptions> options,
    ILogger<MaintenanceWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct); // let the app finish starting
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.MaintenanceIntervalSeconds)));
        do
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "Maintenance run failed"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<SmsQueueService>();
        var webhooks = scope.ServiceProvider.GetRequiredService<WebhookDispatcher>();
        var now = DateTime.UtcNow;
        var o = options.Value;

        // 1. stale assignments
        var staleCutoff = now.AddMinutes(-o.StaleAssignmentMinutes);
        var stale = await db.SmsMessages
            .Where(m => m.Status == SmsStatus.Assigned && m.AssignedAt < staleCutoff)
            .OrderBy(m => m.AssignedAt)
            .Take(500)
            .ToListAsync(ct);

        foreach (var m in stale)
            queue.ApplyFailure(m, $"Device did not report status within {o.StaleAssignmentMinutes} min", now, m.DeviceId);

        // 2. expired pending
        var expiryCutoff = now.AddHours(-o.MessageExpiryHours);
        var expired = await db.SmsMessages
            .Where(m => m.Status == SmsStatus.Pending && m.CreatedAt < expiryCutoff)
            .OrderBy(m => m.CreatedAt)
            .Take(500)
            .ToListAsync(ct);

        foreach (var m in expired)
        {
            m.Status = SmsStatus.Failed;
            m.FailedAt = now;
            m.UpdatedAt = now;
            m.ErrorMessage = $"Expired: not sent within {o.MessageExpiryHours}h";
            db.SmsEvents.Add(SmsQueueService.NewEvent(m.Id, null, "Expired", m.ErrorMessage, now));
        }

        if (stale.Count + expired.Count == 0) return;

        await db.SaveChangesAsync(ct);
        foreach (var m in stale.Concat(expired).Where(m => m.Status == SmsStatus.Failed && m.CallbackUrl is not null))
            webhooks.Enqueue(m.Id);

        log.LogInformation("Maintenance: re-queued/failed {Stale} stale, expired {Expired}", stale.Count, expired.Count);
    }
}
