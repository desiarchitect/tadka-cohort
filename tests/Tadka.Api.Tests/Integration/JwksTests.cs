using System.Net;
using System.Net.Http.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tadka.Api.Auth;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// RS256 signing + JWKS publication + admin-only rotation (ADR-049). These prove the actual cryptographic
/// round trip — a token this service issues really does verify against the public key IT publishes — and
/// the retention/grace-window behaviour: an old key stays verifiable for one rotation, then is dropped.
/// </summary>
public class JwksTests(TadkaApiFactory factory) : IClassFixture<TadkaApiFactory>
{
    private readonly TadkaApiFactory _factory = factory;
    private static readonly JsonWebTokenHandler Handler = new();

    private sealed record TokenBody(string AccessToken, int ExpiresInSeconds, string Role, string RefreshToken);
    private sealed record RotateResponse(string Kid, DateTime CreatedAt);

    [Fact]
    public async Task Jwks_endpoint_is_anonymous_and_publishes_at_least_one_RSA_key()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/.well-known/jwks.json"); // no auth header at all
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<JwksDocument>();
        Assert.NotEmpty(doc!.Keys);
        var key = doc.Keys[0];
        Assert.Equal("RSA", key.Kty);
        Assert.Equal("RS256", key.Alg);
        Assert.False(string.IsNullOrWhiteSpace(key.Kid));
        Assert.False(string.IsNullOrWhiteSpace(key.N));
        Assert.False(string.IsNullOrWhiteSpace(key.E));
    }

    [Fact]
    public async Task A_real_issued_token_verifies_against_its_own_published_JWKS_key()
    {
        var client = _factory.CreateClient();

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "priya@tadka.test", password = "Password123!" });
        loginResp.EnsureSuccessStatusCode();
        var token = (await loginResp.Content.ReadFromJsonAsync<TokenBody>())!.AccessToken;

        var kid = Handler.ReadJsonWebToken(token).Kid;
        Assert.False(string.IsNullOrWhiteSpace(kid));

        var jwks = await (await client.GetAsync("/.well-known/jwks.json")).Content.ReadFromJsonAsync<JwksDocument>();
        var jwk = Assert.Single(jwks!.Keys, k => k.Kid == kid);

        var result = await Handler.ValidateTokenAsync(token, ValidationParamsFor(jwk));
        Assert.True(result.IsValid, result.Exception?.ToString());
    }

    [Fact]
    public async Task Rotation_keeps_the_old_key_verifiable_for_one_grace_window_then_drops_it()
    {
        var client = _factory.CreateClient();

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "priya@tadka.test", password = "Password123!" });
        var originalToken = (await loginResp.Content.ReadFromJsonAsync<TokenBody>())!.AccessToken;
        var originalKid = Handler.ReadJsonWebToken(originalToken).Kid;

        // Rotate once: current + previous (originalKid) both still published — the grace window.
        var rotate1 = await client.PostAsync("/api/v1/auth/rotate-signing-key", null); // Admin by default (TestAuthHandler)
        rotate1.EnsureSuccessStatusCode();
        var afterFirstRotation = await (await client.GetAsync("/.well-known/jwks.json")).Content.ReadFromJsonAsync<JwksDocument>();
        Assert.Equal(2, afterFirstRotation!.Keys.Count);
        var stillThere = Assert.Single(afterFirstRotation.Keys, k => k.Kid == originalKid);

        var stillValid = await Handler.ValidateTokenAsync(originalToken, ValidationParamsFor(stillThere));
        Assert.True(stillValid.IsValid, stillValid.Exception?.ToString());

        // Rotate a second time: cap is current + 1 previous (ADR-049) — the ORIGINAL key is now dropped.
        var rotate2 = await client.PostAsync("/api/v1/auth/rotate-signing-key", null);
        rotate2.EnsureSuccessStatusCode();
        var afterSecondRotation = await (await client.GetAsync("/.well-known/jwks.json")).Content.ReadFromJsonAsync<JwksDocument>();
        Assert.Equal(2, afterSecondRotation!.Keys.Count);
        Assert.DoesNotContain(afterSecondRotation.Keys, k => k.Kid == originalKid);

        // The token signed with the now-retired-and-dropped key no longer resolves against ANY currently
        // published key — a verifier building its trust set from the live JWKS document rejects it.
        var trustedKeys = afterSecondRotation.Keys.Select(JwkConverter.ToRsaPublicKey).Select(rsa => (SecurityKey)new RsaSecurityKey(rsa)).ToList();
        var rejected = await Handler.ValidateTokenAsync(originalToken, ValidationParamsFor(trustedKeys));
        Assert.False(rejected.IsValid);
    }

    [Fact]
    public async Task Rotate_signing_key_is_admin_only()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/rotate-signing-key");
        req.Headers.Add("X-Test-Auth", $"Customer:{Guid.NewGuid()}");
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private static TokenValidationParameters ValidationParamsFor(Jwk jwk) =>
        ValidationParamsFor(new SecurityKey[] { new RsaSecurityKey(JwkConverter.ToRsaPublicKey(jwk)) });

    private static TokenValidationParameters ValidationParamsFor(IEnumerable<SecurityKey> keys) => new()
    {
        ValidateIssuer = true, ValidIssuer = "tadka",
        ValidateAudience = true, ValidAudience = "tadka",
        ValidateIssuerSigningKey = true,
        IssuerSigningKeys = keys,
        ValidateLifetime = true
    };
}
