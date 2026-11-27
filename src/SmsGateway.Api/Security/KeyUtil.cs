using System.Security.Cryptography;
using System.Text;

namespace SmsGateway.Api.Security;

public static class KeyUtil
{
    /// <summary>Creates a random key like "sk_3f9a...". Only the SHA-256 hash is stored.</summary>
    public static string Generate(string prefix)
        => $"{prefix}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";

    public static string Hash(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string Prefix(string key) => key.Length <= 12 ? key : key[..12];

    public static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
