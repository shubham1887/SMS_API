// Drop-in client for Finocore (or any .NET app).
//
// Program.cs / DI registration:
//   builder.Services.AddHttpClient<SmsGatewayClient>(c =>
//   {
//       c.BaseAddress = new Uri(builder.Configuration["SmsGateway:BaseUrl"]!);   // e.g. http://192.168.1.50:5080
//       c.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["SmsGateway:ApiKey"]!);
//   });
//
// Usage:
//   var sms = await gateway.SendAsync("9876543210", $"Your Finocore OTP is {otp}", priority: 10, reference: $"otp-{userId}-{otp}");
//   var status = await gateway.GetAsync(sms.Id);

using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Finocore.Integrations.Sms;

public sealed class SmsGatewayClient(HttpClient http)
{
    public async Task<SmsResult> SendAsync(
        string to, string message, int priority = 0, string? reference = null,
        string? callbackUrl = null, int? deviceId = null, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("/api/sms/send",
            new { to, message, priority, reference, callbackUrl, deviceId }, ct);
        await EnsureOk(res, ct);
        return (await res.Content.ReadFromJsonAsync<SmsResult>(cancellationToken: ct))!;
    }

    public async Task<BulkResult> SendBulkAsync(IEnumerable<(string To, string Message, string? Reference)> items, int priority = 0, CancellationToken ct = default)
    {
        var body = new { messages = items.Select(i => new { to = i.To, message = i.Message, reference = i.Reference, priority }) };
        var res = await http.PostAsJsonAsync("/api/sms/send-bulk", body, ct);
        await EnsureOk(res, ct);
        return (await res.Content.ReadFromJsonAsync<BulkResult>(cancellationToken: ct))!;
    }

    public async Task<SmsResult?> GetAsync(Guid id, CancellationToken ct = default)
    {
        using var res = await http.GetAsync($"/api/sms/{id}", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureOk(res, ct);
        return await res.Content.ReadFromJsonAsync<SmsResult>(cancellationToken: ct);
    }

    public async Task<SmsResult?> GetByReferenceAsync(string reference, CancellationToken ct = default)
    {
        using var res = await http.GetAsync($"/api/sms/by-reference/{Uri.EscapeDataString(reference)}", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureOk(res, ct);
        return await res.Content.ReadFromJsonAsync<SmsResult>(cancellationToken: ct);
    }

    private static async Task EnsureOk(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync(ct);
        throw new SmsGatewayException((int)res.StatusCode, text);
    }
}

public sealed class SmsGatewayException(int statusCode, string body) : Exception($"SMS gateway returned {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}

public sealed record SmsResult(
    Guid Id, string To, string Status, int Priority, int Attempts, int MaxAttempts,
    int? DeviceId, string? DeviceName, string? Reference, string? Error,
    DateTime CreatedAt, DateTime? AssignedAt, DateTime? SentAt, DateTime? DeliveredAt, DateTime? FailedAt, DateTime? NextRetryAt,
    string? Message)
{
    [JsonIgnore] public bool IsFinal => Status is "Delivered" or "Failed" or "Cancelled";
}

public sealed record BulkResult(int Accepted, int Rejected, List<BulkItem> Items);
public sealed record BulkItem(int Index, bool Accepted, SmsResult? Sms, string? Error);

/// <summary>Payload the gateway POSTs to your callbackUrl. Example minimal API endpoint:</summary>
/// <example>
/// app.MapPost("/sms/callback", (SmsWebhook w) => { /* update your table by w.Reference or w.Id */ return Results.Ok(); });
/// </example>
public sealed record SmsWebhook(
    Guid Id, string? Reference, string To, string Status, string? Error, int Attempts,
    int? DeviceId, string? DeviceName, DateTime? SentAt, DateTime? DeliveredAt, DateTime? FailedAt, DateTime Timestamp);
