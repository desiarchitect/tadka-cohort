using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Tadka.Api.Auth;

/// <summary>One public key in JWK format (RFC 7517) — what <c>/.well-known/jwks.json</c> publishes (ADR-049).</summary>
public sealed record Jwk(
    [property: JsonPropertyName("kty")] string Kty,
    [property: JsonPropertyName("use")] string Use,
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("n")] string N,
    [property: JsonPropertyName("e")] string E);

public sealed record JwksDocument([property: JsonPropertyName("keys")] IReadOnlyList<Jwk> Keys);

/// <summary>Converts this service's RSA public keys to/from standard JWK Set format.</summary>
public static class JwkConverter
{
    public static Jwk ToJwk(SigningKey key)
    {
        var p = key.Rsa.ExportParameters(includePrivateParameters: false);
        return new Jwk("RSA", "sig", key.Kid, "RS256", Base64UrlEncode(p.Modulus!), Base64UrlEncode(p.Exponent!));
    }

    public static JwksDocument ToDocument(IEnumerable<SigningKey> keys) => new([.. keys.Select(ToJwk)]);

    /// <summary>Rebuilds an RSA public key from a JWK's modulus/exponent (used by a JWKS *consumer*, e.g. Payment.Api).</summary>
    public static RSA ToRsaPublicKey(Jwk jwk)
    {
        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Base64UrlDecode(jwk.N), Exponent = Base64UrlDecode(jwk.E) });
        return rsa;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
