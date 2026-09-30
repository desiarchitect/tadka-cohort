using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Tadka.Restaurant.Api.Tests;

/// <summary>
/// Restaurant.Api's side of RS256 + JWKS (ADR-067): this service holds NO signing secret and only fetches PUBLIC
/// keys, yet must accept a real token signed by the monolith and keep honouring its <c>role</c> and
/// <c>restaurantId</c> claims. The synthetic TestAuthHandler never goes through the JWT handler's inbound claim
/// renaming, so only these real-token tests would catch a missing <c>MapInboundClaims = false</c>.
/// </summary>
public class JwksValidationTests(RealAuthRestaurantApiFactory factory) : IClassFixture<RealAuthRestaurantApiFactory>
{
    private readonly RealAuthRestaurantApiFactory _factory = factory;
    private static readonly JsonWebTokenHandler Handler = new();

    private static readonly Guid Meghana = new("a1b2c3d4-0001-4000-8000-000000000001");
    private static readonly Guid OtherRestaurant = new("a1b2c3d4-0002-4000-8000-000000000002");

    [Fact]
    public async Task No_token_is_rejected_with_401()
    {
        var resp = await _factory.CreateClient().SendAsync(SetAvailability(Meghana, token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task A_real_owner_token_passes_role_and_ownership_checks_for_its_own_restaurant()
    {
        var (kid, rsa) = _factory.Jwks.AddKey();
        var token = MakeToken(kid, rsa, "RestaurantOwner", restaurantId: Meghana);

        var resp = await _factory.CreateClient().SendAsync(SetAvailability(Meghana, token));

        // 404 (no such menu item), NOT 401/403: the token verified, the `role` claim satisfied [Authorize(Roles)],
        // and the `restaurantId` claim matched. A renamed claim would surface here as a 403.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task A_real_owner_token_is_forbidden_from_another_restaurant()
    {
        var (kid, rsa) = _factory.Jwks.AddKey();
        var token = MakeToken(kid, rsa, "RestaurantOwner", restaurantId: Meghana);

        var resp = await _factory.CreateClient().SendAsync(SetAvailability(OtherRestaurant, token));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task After_rotation_a_token_from_the_NEW_key_is_accepted_via_a_fresh_fetch()
    {
        var (kid1, rsa1) = _factory.Jwks.AddKey();
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(SetAvailability(Meghana, MakeToken(kid1, rsa1, "Admin")))).StatusCode);

        var (kid2, rsa2) = _factory.Jwks.AddKey(); // the monolith rotated: a new current key, the old one still published
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(SetAvailability(Meghana, MakeToken(kid2, rsa2, "Admin")))).StatusCode);
    }

    [Fact]
    public async Task A_token_from_a_key_that_is_no_longer_published_is_rejected()
    {
        var (kid1, rsa1) = _factory.Jwks.AddKey();
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(SetAvailability(Meghana, MakeToken(kid1, rsa1, "Admin")))).StatusCode);

        _factory.Jwks.AddKey();
        _factory.Jwks.Drop(kid1); // rotated past the retention cap

        var resp = await client.SendAsync(SetAvailability(Meghana, MakeToken(kid1, rsa1, "Admin")));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_a_never_published_key_is_rejected()
    {
        using var rogue = RSA.Create(2048);
        var resp = await _factory.CreateClient().SendAsync(SetAvailability(Meghana, MakeToken("never-published", rogue, "Admin")));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private static HttpRequestMessage SetAvailability(Guid restaurantId, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/restaurants/{restaurantId}/menu/{Guid.NewGuid()}/availability")
        { Content = JsonContent.Create(new { isAvailable = false }) };
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static string MakeToken(string kid, RSA rsa, string role, Guid? restaurantId = null)
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()), new("role", role) };
        if (restaurantId is { } r) claims.Add(new Claim("restaurantId", r.ToString()));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "tadka",
            Audience = "tadka",
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = kid }, SecurityAlgorithms.RsaSha256)
        };
        return Handler.CreateToken(descriptor);
    }
}
