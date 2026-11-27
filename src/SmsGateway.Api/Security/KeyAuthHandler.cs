using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmsGateway.Api.Data;

namespace SmsGateway.Api.Security;

public class KeyAuthOptions : AuthenticationSchemeOptions { }

/// <summary>
/// One handler, three schemes:
///  ClientKey -> header X-Api-Key    (consumer apps such as Finocore)
///  DeviceKey -> header X-Device-Key (Android gateway phones)
///  AdminKey  -> header X-Admin-Key  (management, value from configuration Admin:Key)
/// </summary>
public class KeyAuthHandler(
    IOptionsMonitor<KeyAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db,
    IConfiguration config) : AuthenticationHandler<KeyAuthOptions>(options, logger, encoder)
{
    public const string ClientScheme = "ClientKey";
    public const string DeviceScheme = "DeviceKey";
    public const string AdminScheme = "AdminKey";

    public const string ClientHeader = "X-Api-Key";
    public const string DeviceHeader = "X-Device-Key";
    public const string AdminHeader = "X-Admin-Key";

    public const string ClaimClientId = "client_id";
    public const string ClaimDeviceId = "device_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Scheme.Name switch
        {
            ClientScheme => ClientHeader,
            DeviceScheme => DeviceHeader,
            AdminScheme => AdminHeader,
            _ => throw new InvalidOperationException("Unknown scheme " + Scheme.Name)
        };

        var key = Request.Headers[header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key) && Scheme.Name == ClientScheme)
        {
            // Allow "Authorization: Bearer <api key>" for clients as a convenience.
            var auth = Request.Headers.Authorization.FirstOrDefault();
            if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                key = auth[7..].Trim();

            // Simple GET integrations: ?apiKey=... (also api_key / key)
            if (string.IsNullOrWhiteSpace(key))
                key = Request.Query["apiKey"].FirstOrDefault()
                      ?? Request.Query["api_key"].FirstOrDefault()
                      ?? Request.Query["key"].FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(key))
            return AuthenticateResult.NoResult();

        key = key.Trim();

        if (Scheme.Name == AdminScheme)
        {
            var adminKey = config["Admin:Key"];
            if (string.IsNullOrWhiteSpace(adminKey) || !KeyUtil.FixedTimeEquals(key, adminKey))
                return AuthenticateResult.Fail("Invalid admin key");

            return Success([new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Admin")]);
        }

        var hash = KeyUtil.Hash(key);
        var now = DateTime.UtcNow;

        if (Scheme.Name == ClientScheme)
        {
            var client = await db.ApiClients.AsNoTracking().FirstOrDefaultAsync(c => c.KeyHash == hash);
            if (client is null) return AuthenticateResult.Fail("Invalid API key");
            if (!client.IsActive) return AuthenticateResult.Fail("API key is disabled");

            if (client.LastUsedAt is null || now - client.LastUsedAt > TimeSpan.FromMinutes(1))
                await db.ApiClients.Where(c => c.Id == client.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, now));

            return Success([
                new Claim(ClaimTypes.Name, client.Name),
                new Claim(ClaimTypes.Role, "Client"),
                new Claim(ClaimClientId, client.Id.ToString())]);
        }

        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.KeyHash == hash);
        if (device is null) return AuthenticateResult.Fail("Invalid device key");
        if (!device.IsActive) return AuthenticateResult.Fail("Device is disabled");

        return Success([
            new Claim(ClaimTypes.Name, device.Name),
            new Claim(ClaimTypes.Role, "Device"),
            new Claim(ClaimDeviceId, device.Id.ToString())]);
    }

    private AuthenticateResult Success(IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json";
        var header = Scheme.Name switch { ClientScheme => ClientHeader, DeviceScheme => DeviceHeader, _ => AdminHeader };
        return Response.WriteAsync(JsonSerializer.Serialize(new { error = $"Unauthorized. Send a valid {header} header." }));
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.ContentType = "application/json";
        return Response.WriteAsync(JsonSerializer.Serialize(new { error = "Forbidden." }));
    }
}

public static class PrincipalExtensions
{
    public static int ClientId(this ClaimsPrincipal p) => int.Parse(p.FindFirstValue(KeyAuthHandler.ClaimClientId)!);
    public static int DeviceId(this ClaimsPrincipal p) => int.Parse(p.FindFirstValue(KeyAuthHandler.ClaimDeviceId)!);
}
