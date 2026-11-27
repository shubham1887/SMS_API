namespace SmsGateway.Api.Domain;

/// <summary>Lifecycle of an SMS inside the gateway.</summary>
public enum SmsStatus
{
    Pending = 0,    // waiting in queue, no device has picked it yet
    Assigned = 1,   // claimed by a device, send in progress
    Sent = 2,       // device reported the SMS left the SIM
    Delivered = 3,  // operator delivery report received
    Failed = 4,     // permanently failed (max attempts / expired)
    Cancelled = 5   // cancelled by client before sending
}

/// <summary>A consumer application (e.g. Finocore) that calls the API with X-Api-Key.</summary>
public class ApiClient
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>An Android phone running the gateway app, identified by X-Device-Key.</summary>
public class Device
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string KeyHash { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    /// <summary>Max messages this device may claim per minute.</summary>
    public int MaxPerMinute { get; set; } = 10;
    /// <summary>Max messages this device may send per day (0 = unlimited).</summary>
    public int DailyLimit { get; set; } = 300;
    public int SentToday { get; set; }
    public DateTime SentTodayDate { get; set; }

    public DateTime? LastSeenAt { get; set; }
    public string? AppVersion { get; set; }
    public int? BatteryLevel { get; set; }
    public string? SimInfo { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SmsMessage
{
    public Guid Id { get; set; }
    public int ApiClientId { get; set; }
    public ApiClient? ApiClient { get; set; }

    public string ToNumber { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public SmsStatus Status { get; set; }
    /// <summary>Higher value is sent first (e.g. OTP = 10, marketing = 0).</summary>
    public int Priority { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;

    public int? DeviceId { get; set; }
    public Device? Device { get; set; }
    /// <summary>Force a specific device/SIM. Null = any available device (load balanced).</summary>
    public int? PreferredDeviceId { get; set; }

    /// <summary>Client side id for idempotency and reconciliation.</summary>
    public string? ClientReference { get; set; }
    public string? CallbackUrl { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? AssignedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Audit trail for every state change of a message.</summary>
public class SmsEvent
{
    public long Id { get; set; }
    public Guid SmsMessageId { get; set; }
    public int? DeviceId { get; set; }
    public string Event { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; }
}
