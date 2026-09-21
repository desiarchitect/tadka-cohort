using System.Net;
using System.Net.Http.Json;
using Tadka.Api.Auth;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Brute-force protection on the login endpoint (ADR-047): a fixed-window rate limiter keyed by IP
/// (429 once tripped) plus an independent per-account lockout counter (401, even for the RIGHT password,
/// once the account is locked). The two are tested with DIFFERENT clients — the lockout tests bump the
/// rate-limit's PermitLimit way up via <c>WithWebHostBuilder</c> so a 429 from the OTHER control never
/// masks the 401 this suite is actually asserting on.
/// </summary>
public class RateLimitingTests(TadkaApiFactory factory) : IClassFixture<TadkaApiFactory>
{
    private readonly TadkaApiFactory _factory = factory;

    [Fact]
    public async Task Login_beyond_the_window_limit_is_rejected_with_429()
    {
        // Default policy (appsettings.json): 5 requests / 10s window, keyed by IP. TestServer requests
        // all share one "unknown" IP partition, so they all land in the SAME window deterministically.
        var client = _factory.CreateClient();
        var body = new { email = "nobody-ratelimit@tadka.test", password = "wrong" };

        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
            last = await client.PostAsJsonAsync("/api/v1/auth/login", body);

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }

    [Fact]
    public async Task Account_locks_after_too_many_failed_attempts_and_then_rejects_even_the_correct_password()
    {
        // A dedicated client whose rate-limit policy is loosened way up (this test drives 6 login calls
        // against ONE account, which would otherwise trip the 429 control above before lockout logic —
        // a distinct process, tested distinctly — ever gets to run).
        var client = _factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimit:PermitLimit", "1000")).CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "rahul@tadka.test", password = "wrong-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // 6th attempt uses the CORRECT password — still 401, because the account is now locked (ADR-047:
        // a generic 401 either way, so a caller can't distinguish "wrong password" from "locked out").
        var lockedAttempt = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "rahul@tadka.test", password = AuthSeeder.DefaultPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, lockedAttempt.StatusCode);
    }

    [Fact]
    public async Task Lockout_clears_itself_once_the_cooldown_passes()
    {
        // Short lockout window (1s) + loosened rate limit, isolated to THIS test's own host.
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:RateLimit:PermitLimit", "1000");
            b.UseSetting("Auth:Lockout:LockoutSeconds", "1");
        }).CreateClient();
        const string email = "owner1@tadka.test"; // a DIFFERENT seeded account from the other tests in this class

        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong-password" });

        var whileLocked = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = AuthSeeder.DefaultPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, whileLocked.StatusCode);

        await Task.Delay(1200);

        var afterCooldown = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = AuthSeeder.DefaultPassword });
        Assert.Equal(HttpStatusCode.OK, afterCooldown.StatusCode);
    }
}
