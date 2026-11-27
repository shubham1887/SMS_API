using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmsGateway.Api.Data;

namespace SmsGateway.Api.Services;

/// <summary>In-memory queue of message ids whose status should be pushed to the client's CallbackUrl.</summary>
public class WebhookDispatcher
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid messageId) => _channel.Writer.TryWrite(messageId);
    public ChannelReader<Guid> Reader => _channel.Reader;
}

public class WebhookWorker(
    WebhookDispatcher dispatcher,
    IServiceScopeFactory scopes,
    IHttpClientFactory httpFactory,
    IOptions<GatewayOptions> options,
    ILogger<WebhookWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var id in dispatcher.Reader.ReadAllAsync(ct))
        {
            try { await DeliverAsync(id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "Webhook delivery crashed for {Id}", id); }
        }
    }

    private async Task DeliverAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var msg = await db.SmsMessages.Include(m => m.Device).AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg?.CallbackUrl is null) return;

        var payload = new
        {
            id = msg.Id,
            reference = msg.ClientReference,
            to = msg.ToNumber,
            status = msg.Status.ToString(),
            error = msg.ErrorMessage,
            attempts = msg.Attempts,
            deviceId = msg.DeviceId,
            deviceName = msg.Device?.Name,
            sentAt = msg.SentAt,
            deliveredAt = msg.DeliveredAt,
            failedAt = msg.FailedAt,
            timestamp = DateTime.UtcNow
        };

        var client = httpFactory.CreateClient("webhook");
        client.Timeout = TimeSpan.FromSeconds(options.Value.WebhookTimeoutSeconds);
        var retries = Math.Max(1, options.Value.WebhookRetries);
        string? lastError = null;

        for (var attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, msg.CallbackUrl) { Content = JsonContent.Create(payload) };
                req.Headers.Add("X-Gateway-Event", "sms.status");
                req.Headers.Add("X-Gateway-Message-Id", msg.Id.ToString());
                using var res = await client.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    db.SmsEvents.Add(SmsQueueService.NewEvent(msg.Id, null, "WebhookSent", $"{msg.Status} -> HTTP {(int)res.StatusCode}", DateTime.UtcNow));
                    await db.SaveChangesAsync(ct);
                    return;
                }
                lastError = $"HTTP {(int)res.StatusCode}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lastError = ex.GetType().Name + ": " + ex.Message;
            }

            if (attempt < retries)
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
        }

        log.LogWarning("Webhook failed for {Id} -> {Url}: {Error}", msg.Id, msg.CallbackUrl, lastError);
        db.SmsEvents.Add(SmsQueueService.NewEvent(msg.Id, null, "WebhookFailed", lastError, DateTime.UtcNow));
        await db.SaveChangesAsync(ct);
    }
}
