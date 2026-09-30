using System.Security.Cryptography;

namespace Tadka.Api.Auth;

/// <summary>One RSA keypair the monolith can sign with (or has recently signed with) — ADR-067.</summary>
public sealed class SigningKey
{
    public required string Kid { get; init; }
    public required RSA Rsa { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// RSA keypair store for RS256 signing + JWKS publication (ADR-067). Holds the CURRENT signing key plus a
/// small number of retired keys kept for VERIFICATION only, so a rotation doesn't instantly 401 every token
/// issued moments earlier.
///
/// Two modes:
/// <list type="bullet">
/// <item><b>Generated (default).</b> Keys are created in this process and live only in its memory. Fine for one
/// instance; with several replicas each would hold its OWN key, so a token signed by replica A fails on B.</item>
/// <item><b>Shared.</b> The current (and optionally the previous) private key is supplied from configuration /
/// a secret store, so every replica signs with, and publishes, the SAME key. The <c>kid</c> is derived from the
/// public key, so it is identical everywhere. Rotation is then a deployment act (put the new key in the secret,
/// move the old one to "previous"), not a call on one replica: <see cref="Rotate"/> is refused.</item>
/// </list>
/// </summary>
public sealed class SigningKeyStore
{
    // Current + 1 previous (ADR-067's retention choice). Index 0 is always the current signing key.
    private const int MaxKeys = 2;

    private readonly Lock _lock = new();
    private readonly List<SigningKey> _keys = [];

    /// <summary>Generated mode: start holding one freshly generated key.</summary>
    public SigningKeyStore() => Rotate();

    /// <summary>Shared mode: load the current key (and, optionally, the previous one) from PEM text or a bare
    /// base64 PKCS#8 blob, the two shapes a secret store usually hands back.</summary>
    public SigningKeyStore(string currentPrivateKey, string? previousPrivateKey = null)
    {
        IsShared = true;
        _keys.Add(Load(currentPrivateKey));
        if (!string.IsNullOrWhiteSpace(previousPrivateKey))
            _keys.Add(Load(previousPrivateKey));
    }

    /// <summary>True when the keys come from configuration (identical on every replica), false when generated here.</summary>
    public bool IsShared { get; }

    public SigningKey Current
    {
        get { lock (_lock) return _keys[0]; }
    }

    /// <summary>Every key still valid for verifying a previously-issued token (current + grace-window old ones).</summary>
    public IReadOnlyList<SigningKey> AllForVerification
    {
        get { lock (_lock) return [.. _keys]; }
    }

    public SigningKey? Find(string kid)
    {
        lock (_lock) return _keys.FirstOrDefault(k => k.Kid == kid);
    }

    /// <summary>Generates a fresh keypair, makes it current, and retires the oldest key once over the cap.
    /// Refused in shared mode: a key generated on one replica would be unknown to all the others.</summary>
    public SigningKey Rotate()
    {
        if (IsShared)
            throw new InvalidOperationException(
                "The signing key is shared by every replica, so it cannot be rotated on one of them. " +
                "Rotate by deploying the new key (and the old one as the previous key).");

        var next = new SigningKey { Kid = Guid.NewGuid().ToString("N"), Rsa = RSA.Create(2048), CreatedAt = DateTime.UtcNow };
        lock (_lock)
        {
            _keys.Insert(0, next);
            while (_keys.Count > MaxKeys)
            {
                var retired = _keys[^1];
                _keys.RemoveAt(_keys.Count - 1);
                retired.Rsa.Dispose(); // beyond the grace window: a token signed with this kid now fails to verify
            }
        }
        return next;
    }

    private static SigningKey Load(string privateKey)
    {
        var rsa = RSA.Create();
        var text = privateKey.Trim();
        if (text.Contains("-----BEGIN", StringComparison.Ordinal))
            rsa.ImportFromPem(text);
        else
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(text), out _);

        // kid = first 16 hex chars of SHA-256(public key). Same key in, same kid out, on every replica.
        var kid = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
        return new SigningKey { Kid = kid, Rsa = rsa, CreatedAt = DateTime.UtcNow };
    }
}
