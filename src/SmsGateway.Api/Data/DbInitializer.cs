using Microsoft.EntityFrameworkCore;
using SmsGateway.Api.Domain;
using SmsGateway.Api.Security;

namespace SmsGateway.Api.Data;

/// <summary>Applies migrations and seeds a first client / device from configuration (section "Seed").</summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        await db.Database.MigrateAsync();

        var now = DateTime.UtcNow;

        var clientKey = config["Seed:ClientApiKey"];
        if (!string.IsNullOrWhiteSpace(clientKey) && !await db.ApiClients.AnyAsync())
        {
            db.ApiClients.Add(new ApiClient
            {
                Name = config["Seed:ClientName"] ?? "Default Client",
                KeyHash = KeyUtil.Hash(clientKey),
                KeyPrefix = KeyUtil.Prefix(clientKey),
                IsActive = true,
                CreatedAt = now
            });
            log.LogInformation("Seeded API client '{Name}' from configuration", config["Seed:ClientName"]);
        }

        var deviceKey = config["Seed:DeviceKey"];
        if (!string.IsNullOrWhiteSpace(deviceKey) && !await db.Devices.AnyAsync())
        {
            db.Devices.Add(new Device
            {
                Name = config["Seed:DeviceName"] ?? "Phone-1",
                KeyHash = KeyUtil.Hash(deviceKey),
                KeyPrefix = KeyUtil.Prefix(deviceKey),
                IsActive = true,
                MaxPerMinute = 10,
                DailyLimit = 300,
                SentTodayDate = now.Date,
                CreatedAt = now
            });
            log.LogInformation("Seeded device '{Name}' from configuration", config["Seed:DeviceName"]);
        }

        await db.SaveChangesAsync();
    }
}
