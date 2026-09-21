using System.Security.Cryptography;

namespace Tadka.Api.Auth;

/// <summary>One RSA keypair the monolith can sign with (or has recently signed with) — ADR-049.</summary>
public sealed class SigningKey
{
    public required string Kid { get; init; }
    public required RSA Rsa { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// In-memory RSA keypair store for RS256 signing + JWKS publication (ADR-049). Holds the CURRENT
/// signing key plus a small number of retired keys kept around for VERIFICATION only, so a rotation
/// doesn't instantly 401 every token issued moments earlier. Keys live only in process memory — see
/// ADR-049's Trade-off for why, and what that costs on a restart.
/// </summary>
public sealed class SigningKeyStore
{
    // Current + 1 previous (ADR-049's retention choice). Index 0 is always the current signing key.
    private const int MaxKeys = 2;

    private readonly Lock _lock = new();
    private readonly List<SigningKey> _keys = [];

    public SigningKeyStore() => Rotate(); // always start holding at least one usable key

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

    /// <summary>Generates a fresh keypair, makes it current, and retires the oldest key once over the cap.</summary>
    public SigningKey Rotate()
    {
        var next = new SigningKey { Kid = Guid.NewGuid().ToString("N"), Rsa = RSA.Create(2048), CreatedAt = DateTime.UtcNow };
        lock (_lock)
        {
            _keys.Insert(0, next);
            while (_keys.Count > MaxKeys)
            {
                var retired = _keys[^1];
                _keys.RemoveAt(_keys.Count - 1);
                retired.Rsa.Dispose(); // beyond the grace window — a token signed with this kid now fails to verify
            }
        }
        return next;
    }
}
