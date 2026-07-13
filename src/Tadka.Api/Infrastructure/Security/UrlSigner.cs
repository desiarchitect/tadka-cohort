using System.Security.Cryptography;
using System.Text;

namespace Tadka.Api.Infrastructure.Security;

/// <summary>
/// Day 6, Beat (CDN emulation - signed URLs): HMAC-SHA256 over "{resourceId}|{expiryUnixSeconds}",
/// hex-encoded. Time-limited, tamper-evident, no server-side state (no token to store or revoke -
/// the trade-off: a signed URL that leaks is valid until it expires, full stop). This is the same
/// mechanic real CDNs / S3 presigned URLs use, scaled down to one HMAC key instead of a KMS.
/// </summary>
public sealed class UrlSigner(IConfiguration configuration)
{
    // Demo-only key with a safe fallback so the feature works out of the box; a real deployment
    // would require Demo:InvoiceSigningKey (or equivalent) from a secret store, not a literal default.
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        configuration["Demo:InvoiceSigningKey"] ?? "tadka-demo-signing-key-do-not-use-in-prod");

    public (string Signature, long ExpiresAtUnixSeconds) Sign(string resourceId, TimeSpan validFor)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
        return (Compute(resourceId, expiresAt), expiresAt);
    }

    public bool Verify(string resourceId, long expiresAtUnixSeconds, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnixSeconds) return false;

        var expected = Compute(resourceId, expiresAtUnixSeconds);
        // Fixed-time comparison — a signature check is exactly the kind of comparison a naive
        // `==` turns into a timing side channel.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
    }

    private string Compute(string resourceId, long expiresAtUnixSeconds)
    {
        var payload = Encoding.UTF8.GetBytes($"{resourceId}|{expiresAtUnixSeconds}");
        return Convert.ToHexString(HMACSHA256.HashData(_key, payload)).ToLowerInvariant();
    }
}
