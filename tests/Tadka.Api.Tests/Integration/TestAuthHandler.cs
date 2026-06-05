using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Test authentication scheme so the existing suites don't each need a real JWT. By default it
/// authenticates the request as an **Admin** (so every pre-Day-10 test passes unchanged). Override per
/// request with headers:
///   <c>X-Test-NoAuth: true</c>            → not authenticated (→ 401, the auth-bypass demo)
///   <c>X-Test-Auth: Role[:sub[:restId]]</c> → authenticate as that role/user/owned-restaurant (→ 403 demos)
/// </summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-Test-NoAuth"] == "true")
            return Task.FromResult(AuthenticateResult.NoResult()); // simulate "no token"

        var spec = Request.Headers["X-Test-Auth"].FirstOrDefault();
        string role = "Admin";
        string? sub = null, restaurantId = null;
        if (!string.IsNullOrWhiteSpace(spec))
        {
            var parts = spec.Split(':');
            role = parts[0];
            if (parts.Length > 1) sub = parts[1];
            if (parts.Length > 2) restaurantId = parts[2];
        }

        var claims = new List<Claim> { new("role", role) };
        if (sub is not null) claims.Add(new Claim("sub", sub));
        if (restaurantId is not null) claims.Add(new Claim("restaurantId", restaurantId));

        var identity = new ClaimsIdentity(claims, Scheme, nameType: "sub", roleType: "role");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
