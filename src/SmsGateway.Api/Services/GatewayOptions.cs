namespace SmsGateway.Api.Services;

public class GatewayOptions
{
    public const string Section = "Gateway";

    /// <summary>Prefixed to 10-digit numbers, e.g. "+91".</summary>
    public string DefaultCountryCode { get; set; } = "+91";
    public int DefaultMaxAttempts { get; set; } = 3;
    /// <summary>Base delay for retry backoff: base * 2^(attempt-1).</summary>
    public int RetryBaseSeconds { get; set; } = 30;
    /// <summary>If a device claims a message and does not report within this time it is re-queued.</summary>
    public int StaleAssignmentMinutes { get; set; } = 3;
    /// <summary>Pending messages older than this are failed as expired.</summary>
    public int MessageExpiryHours { get; set; } = 24;
    public int DevicePollIntervalSeconds { get; set; } = 5;
    public int DeviceMaxBatch { get; set; } = 5;
    public int DeviceOfflineAfterSeconds { get; set; } = 120;
    public int MaintenanceIntervalSeconds { get; set; } = 30;
    /// <summary>Windows time zone id used for the per-device daily counter reset.</summary>
    public string TimeZone { get; set; } = "India Standard Time";
    public int WebhookRetries { get; set; } = 3;
    public int WebhookTimeoutSeconds { get; set; } = 10;
}
