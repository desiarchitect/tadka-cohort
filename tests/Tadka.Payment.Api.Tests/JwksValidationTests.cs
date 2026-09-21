using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Tadka.Payment.Api.Tests;

/// <summary>
/// Payment.Api's side of RS256 + JWKS (ADR-049) — the "hardest task": this service holds NO signing
/// secret, only ever fetches PUBLIC keys via <c>JwksClient</c> + <c>IssuerSigningKeyResolver</c>, and must
/// keep validating tokens correctly as Tadka.Api rotates its signing key underneath it. A
/// <see cref="FakeJwksServer"/> stands in for Tadka.Api's real endpoint (see <see cref="RealAuthPaymentApiFactory"/>
/// for why a full second service + its own Postgres wasn't worth the extra couple of minutes per test run —
/// the real cross-service round trip, RS256-signed by Tadka.Api's actual TokenService and verified against
/// its actual JWKS endpoint, is covered on the Tadka.Api side by JwksTests, and re-confirmed live via curl
/// in the delivery report).
/// </summary>
public class JwksValidationTests(RealAuthPaymentApiFactory factory) : IClassFixture<RealAuthPaymentApiFactory>
{
    private readonly RealAuthPaymentApiFactory _factory = factory;
    private static readonly JsonWebTokenHandler Handler = new();

    [Fact]
    public async Task No_token_is_rejected_with_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/payments/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_the_currently_published_key_is_accepted()
    {
        var (kid, rsa) = _factory.Jwks.AddKey();
        var client = _factory.CreateClient();

        var resp = await Call(client, MakeToken(kid, rsa));

        // 404 (no such payment) — NOT 401 — proves the token was authenticated successfully.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task After_rotation_a_token_from_the_NEW_key_is_accepted_via_a_fresh_fetch()
    {
        var (kid1, rsa1) = _factory.Jwks.AddKey();
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await Call(client, MakeToken(kid1, rsa1))).StatusCode);

        // Simulate Tadka.Api's rotate-signing-key: a NEW current key, old one still published (grace window).
        var (kid2, rsa2) = _factory.Jwks.AddKey();
        var resp = await Call(client, MakeToken(kid2, rsa2));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode); // the unknown kid triggered a refetch that picked it up
    }

    [Fact]
    public async Task A_token_from_a_key_thats_been_dropped_beyond_the_cap_is_rejected()
    {
        var (kid1, rsa1) = _factory.Jwks.AddKey();
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await Call(client, MakeToken(kid1, rsa1))).StatusCode);

        _factory.Jwks.AddKey(); // rotate past it
        _factory.Jwks.Drop(kid1); // ...and simulate the retention cap dropping the old one

        var rejected = await Call(client, MakeToken(kid1, rsa1)); // still cryptographically valid, just not published any more
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task An_unknown_kid_that_was_never_published_is_rejected()
    {
        using var rogueRsa = RSA.Create(2048);
        var client = _factory.CreateClient();
        var resp = await Call(client, MakeToken("never-published-kid", rogueRsa));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private static Task<HttpResponseMessage> Call(HttpClient client, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/payments/{Guid.NewGuid()}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(req);
    }

    private static string MakeToken(string kid, RSA rsa)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = kid };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "tadka",
            Audience = "tadka",
            Subject = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim("role", "Admin")]),
            Expires = DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        };
        return Handler.CreateToken(descriptor);
    }
}
