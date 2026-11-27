using System.ComponentModel.DataAnnotations;
using SmsGateway.Api.Domain;

namespace SmsGateway.Api.Contracts;

// ---------- Client (consumer app) ----------

/// <summary>Validation of To/Message is done in SmsQueueService so bulk items fail individually.</summary>
public record SendSmsRequest(
    string? To,
    string? Message,
    int Priority = 0,
    string? Reference = null,
    string? CallbackUrl = null,
    int? DeviceId = null,
    int? MaxAttempts = null);

public record SendBulkSmsRequest([Required, MinLength(1), MaxLength(500)] List<SendSmsRequest> Messages);

public record SmsResponse(
    Guid Id,
    string To,
    string Status,
    int Priority,
    int Attempts,
    int MaxAttempts,
    int? DeviceId,
    string? DeviceName,
    string? Reference,
    string? Error,
    DateTime CreatedAt,
    DateTime? AssignedAt,
    DateTime? SentAt,
    DateTime? DeliveredAt,
    DateTime? FailedAt,
    DateTime? NextRetryAt,
    string? Message = null)
{
    public static SmsResponse From(SmsMessage m, bool includeBody = false) => new(
        m.Id, m.ToNumber, m.Status.ToString(), m.Priority, m.Attempts, m.MaxAttempts,
        m.DeviceId, m.Device?.Name, m.ClientReference, m.ErrorMessage,
        m.CreatedAt, m.AssignedAt, m.SentAt, m.DeliveredAt, m.FailedAt, m.NextRetryAt,
        includeBody ? m.Body : null);
}

public record BulkItemResult(int Index, bool Accepted, SmsResponse? Sms, string? Error);

public record PagedResponse<T>(int Page, int PageSize, int Total, IReadOnlyList<T> Items);

public record SmsStatsResponse(
    DateTime From, DateTime To, int Total,
    int Pending, int Assigned, int Sent, int Delivered, int Failed, int Cancelled);

// ---------- Device (Android gateway) ----------

public record HeartbeatRequest(int? BatteryLevel, string? AppVersion, string? SimInfo, string? PhoneNumber);

public record HeartbeatResponse(
    int DeviceId, string Name, DateTime ServerTimeUtc,
    int PollIntervalSeconds, int MaxBatch, int MaxPerMinute, int DailyLimit, int SentToday, int PendingInQueue);

public record DeviceSmsItem(Guid Id, string To, string Message, int Priority, int Attempt);

public record DeviceStatusReport(
    [Required] string Status,          // Sent | Delivered | Failed
    string? Error = null,
    DateTime? Timestamp = null);

public record DeviceStatusBulkItem(Guid Id, [Required] string Status, string? Error = null, DateTime? Timestamp = null);

// ---------- Admin ----------

public record CreateClientRequest([Required, StringLength(100)] string Name);
public record UpdateClientRequest(string? Name, bool? IsActive);
public record ClientResponse(int Id, string Name, string KeyPrefix, bool IsActive, DateTime CreatedAt, DateTime? LastUsedAt)
{
    public static ClientResponse From(ApiClient c) => new(c.Id, c.Name, c.KeyPrefix, c.IsActive, c.CreatedAt, c.LastUsedAt);
}
public record ClientCreatedResponse(ClientResponse Client, string ApiKey);

public record CreateDeviceRequest(
    [Required, StringLength(100)] string Name,
    [StringLength(20)] string? PhoneNumber = null,
    [Range(1, 1000)] int MaxPerMinute = 10,
    [Range(0, 100000)] int DailyLimit = 300);
public record UpdateDeviceRequest(string? Name, string? PhoneNumber, bool? IsActive, int? MaxPerMinute, int? DailyLimit);
public record DeviceResponse(
    int Id, string Name, string? PhoneNumber, string KeyPrefix, bool IsActive, bool IsOnline,
    int MaxPerMinute, int DailyLimit, int SentToday, DateTime? LastSeenAt,
    string? AppVersion, int? BatteryLevel, string? SimInfo, DateTime CreatedAt)
{
    public static DeviceResponse From(Device d, DateTime now, int offlineAfterSeconds) => new(
        d.Id, d.Name, d.PhoneNumber, d.KeyPrefix, d.IsActive,
        d.IsActive && d.LastSeenAt is not null && (now - d.LastSeenAt.Value).TotalSeconds <= offlineAfterSeconds,
        d.MaxPerMinute, d.DailyLimit, d.SentToday, d.LastSeenAt, d.AppVersion, d.BatteryLevel, d.SimInfo, d.CreatedAt);
}
public record DeviceCreatedResponse(DeviceResponse Device, string DeviceKey);

public record EventResponse(long Id, string Event, string? Details, int? DeviceId, DateTime CreatedAt);

public record DashboardResponse(
    DateTime ServerTimeUtc,
    int QueueDepth,
    int InFlight,
    int DevicesTotal,
    int DevicesOnline,
    SmsStatsResponse Today,
    SmsStatsResponse Last7Days,
    IReadOnlyList<DeviceResponse> Devices);
