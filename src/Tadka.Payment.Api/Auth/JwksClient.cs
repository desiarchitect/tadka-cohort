using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Tadka.Payment.Api.Auth;

file sealed record JwkDto(
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("n")] string N,
    [property: JsonPropertyName("e")] string E);

file sealed record JwksDocumentDto([property: JsonPropertyName("keys")] List<JwkDto>? Keys);

/// <summary>
/// Fetches Tadka.Api's <c>/.well-known/jwks.json</c> over HTTP and caches the public keys in memory for a
/// short TTL (ADR-049) — this service never holds a signing secret, only public keys, and only refetches
/// them a handful of times an hour even under real traffic. A <c>kid</c> that isn't in the cache triggers
/// ONE forced refetch (it may just have been rotated in) before giving up.
/// </summary>
public sealed class JwksClient(IHttpClientFactory httpClientFactory, IOptions<JwksOptions> options)
{
    public const string HttpClientName = "jwks";

    private readonly JwksOptions _o = options.Value;
    private readonly Lock _lock = new();
    private Dictionary<string, RSA>? _cache;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public async Task<SecurityKey?> ResolveAsync(string kid, CancellationToken ct)
    {
        await EnsureFreshAsync(force: false, ct);
        if (TryGet(kid, out var key)) return key;

        await EnsureFreshAsync(force: true, ct);
        return TryGet(kid, out key) ? key : null;
    }

    private bool TryGet(string kid, out SecurityKey? key)
    {
        lock (_lock)
        {
            if (_cache is not null && _cache.TryGetValue(kid, out var rsa))
            {
                key = new RsaSecurityKey(rsa) { KeyId = kid };
                return true;
            }
        }
        key = null;
        return false;
    }

    private async Task EnsureFreshAsync(bool force, CancellationToken ct)
    {
        bool stale;
        lock (_lock)
            stale = _cache is null || DateTime.UtcNow - _cachedAtUtc > TimeSpan.FromMinutes(_o.JwksCacheMinutes);
        if (!force && !stale) return;

        JwksDocumentDto? doc;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            doc = await client.GetFromJsonAsync<JwksDocumentDto>("/.well-known/jwks.json", ct);
        }
        catch (HttpRequestException)
        {
            // Tadka.Api unreachable — keep serving the last good cache rather than fail every request
            // (ADR-049's honest failure mode: only bites when BOTH the cache is stale AND the endpoint is down).
            return;
        }
        if (doc?.Keys is null) return;

        var fresh = new Dictionary<string, RSA>();
        foreach (var jwk in doc.Keys)
        {
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = Base64UrlDecode(jwk.N), Exponent = Base64UrlDecode(jwk.E) });
            fresh[jwk.Kid] = rsa;
        }

        lock (_lock) { _cache = fresh; _cachedAtUtc = DateTime.UtcNow; }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
