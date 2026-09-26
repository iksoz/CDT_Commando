using System.Security.Cryptography;
using System.Text;

namespace Commando.Server;

public static class Security
{
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string DeriveHostEnrollmentKey(string eventEnrollmentKey, string hostName) =>
        Convert.ToBase64String(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(eventEnrollmentKey),
            Encoding.UTF8.GetBytes($"Commando:host:{hostName.ToLowerInvariant()}")));

    public static string HashToken(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool TokenMatches(string supplied, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return false;
        }

        var actual = Convert.FromHexString(HashToken(supplied));
        var expected = Convert.FromHexString(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
