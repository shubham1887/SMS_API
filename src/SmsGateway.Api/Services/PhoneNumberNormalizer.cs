using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SmsGateway.Api.Services;

public partial class PhoneNumberNormalizer(IOptions<GatewayOptions> options)
{
    [GeneratedRegex(@"[\s\-\(\)\.]")] private static partial Regex Noise();
    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")] private static partial Regex E164();

    /// <summary>Returns an E.164 number or null when invalid. "98765 43210" becomes "+919876543210".</summary>
    public string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var n = Noise().Replace(raw.Trim(), "");
        if (n.StartsWith("00")) n = "+" + n[2..];

        var cc = options.Value.DefaultCountryCode.TrimStart('+');
        if (!n.StartsWith('+'))
        {
            if (n.Length == 10) n = "+" + cc + n;
            else if (n.StartsWith('0') && n.Length == 11) n = "+" + cc + n[1..];
            else n = "+" + n;
        }
        return E164().IsMatch(n) ? n : null;
    }
}
