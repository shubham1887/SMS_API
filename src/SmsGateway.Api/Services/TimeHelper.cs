using Microsoft.Extensions.Options;

namespace SmsGateway.Api.Services;

public class TimeHelper
{
    private readonly TimeZoneInfo _tz;

    public TimeHelper(IOptions<GatewayOptions> options)
    {
        try { _tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone); }
        catch { _tz = TimeZoneInfo.Utc; }
    }

    public DateTime UtcNow => DateTime.UtcNow;

    /// <summary>Today's date in the configured (India) time zone, used for daily limits.</summary>
    public DateTime LocalToday => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz).Date;

    public DateTime LocalDayStartUtc(DateTime localDate)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified), _tz);
}
