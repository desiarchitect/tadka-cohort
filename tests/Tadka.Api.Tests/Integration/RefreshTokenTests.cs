using System.Net;
using System.Net.Http.Json;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Refresh-token rotation + reuse detection (ADR-048): a refresh token is single-use — presenting it
/// rotates in a new pair; presenting an already-rotated (revoked) token is treated as theft and kills the
/// WHOLE rotation chain, not just the replayed token. Logout revokes the caller's own chain.
/// </summary>
public class RefreshTokenTests(TadkaApiFactory factory) : IClassFixture<TadkaApiFactory>
{
    private readonly TadkaApiFactory _factory = factory;

    private static readonly Guid Priya = new("c1b2c3d4-0001-4000-8000-000000000001");

    private sealed record TokenBody(string AccessToken, int ExpiresInSeconds, string Role, string RefreshToken);

    // This suite drives login/refresh several times per test across several tests — more auth-write calls
    // than the default 5-per-10s rate-limit policy allows (that policy is RateLimitingTests' concern, not
    // this one's). Every client here comes from a loosened-limit host so a 429 never masks what we're
    // actually asserting on.
    private HttpClient CreateClient() => _factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimit:PermitLimit", "1000")).CreateClient();

    [Fact]
    public async Task Login_returns_both_tokens_and_refresh_rotates_them()
    {
        var client = CreateClient();

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "priya@tadka.test", password = "Password123!" });
        loginResp.EnsureSuccessStatusCode();
        var original = await loginResp.Content.ReadFromJsonAsync<TokenBody>();
        Assert.False(string.IsNullOrWhiteSpace(original!.RefreshToken));

        var refreshResp = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = original.RefreshToken });
        refreshResp.EnsureSuccessStatusCode();
        var rotated = await refreshResp.Content.ReadFromJsonAsync<TokenBody>();

        Assert.NotEqual(original.AccessToken, rotated!.AccessToken);
        Assert.NotEqual(original.RefreshToken, rotated.RefreshToken);
    }

    [Fact]
    public async Task Reusing_an_already_rotated_refresh_token_is_rejected_and_kills_the_whole_chain()
    {
        var client = CreateClient();

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "priya@tadka.test", password = "Password123!" });
        var t0 = await loginResp.Content.ReadFromJsonAsync<TokenBody>();

        // Rotate once: t0's refresh token is now revoked, t1 is the newest valid one in the chain.
        var r1 = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = t0!.RefreshToken });
        var t1 = await r1.Content.ReadFromJsonAsync<TokenBody>();

        // REPLAY t0 (already revoked) — reuse detected → 401, and the ENTIRE family (including t1) dies.
        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = t0.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // t1 was the newest, legitimately-rotated-to token in the SAME chain — reuse detection must have
        // revoked it too, so even the "innocent" holder is forced to fully re-login.
        var afterReuse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = t1!.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_rejected()
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "not-a-real-token" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_familys_refresh_tokens_but_the_still_live_access_token_is_unaffected()
    {
        var client = CreateClient();

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "priya@tadka.test", password = "Password123!" });
        var tokens = await loginResp.Content.ReadFromJsonAsync<TokenBody>();

        // Logout is [Authorize] — authenticate as Priya herself via the test scheme (TestAuthHandler).
        var logoutReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout")
        {
            Content = JsonContent.Create(new { refreshToken = tokens!.RefreshToken })
        };
        logoutReq.Headers.Add("X-Test-Auth", $"Customer:{Priya}");
        var logoutResp = await client.SendAsync(logoutReq);
        Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode);

        // The refresh token is dead post-logout (ADR-048).
        var refreshAfterLogout = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterLogout.StatusCode);

        // Known limitation named in ADR-048: the ACCESS token itself is a stateless JWT with no denylist,
        // so it is NOT revoked by logout — it simply expires on its own short (15 min) TTL. We don't assert
        // that here (this factory swaps in TestAuthHandler, so it never checks a real bearer token's
        // validity), but the ADR is explicit that "logout" only ever means "no more silent refreshes."
    }

    [Fact]
    public async Task Logout_is_a_noop_for_a_token_that_does_not_belong_to_the_caller()
    {
        var client = CreateClient();
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "priya@tadka.test", password = "Password123!" });
        var tokens = await loginResp.Content.ReadFromJsonAsync<TokenBody>();

        // A DIFFERENT authenticated user tries to log Priya's session out — must not revoke her chain.
        var logoutReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout")
        {
            Content = JsonContent.Create(new { refreshToken = tokens!.RefreshToken })
        };
        logoutReq.Headers.Add("X-Test-Auth", $"Customer:{Guid.NewGuid()}");
        var logoutResp = await client.SendAsync(logoutReq);
        Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode); // still 204 — doesn't leak whose token it was

        // Priya's refresh token is still perfectly usable — the mismatched-owner logout was a no-op.
        var refreshResp = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);
    }
}
