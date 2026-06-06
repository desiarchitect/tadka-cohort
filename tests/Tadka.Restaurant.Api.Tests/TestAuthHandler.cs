using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tadka.Restaurant.Api.Tests;

/// <summary>
/// Test auth scheme: Admin by default; <c>X-Test-NoAuth: true</c> → anonymous (401);
/// <c>X-Test-Auth: Role:sub:restaurantId</c> → that role + restaurantId claim (drives ownership 403).
/// </summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-Test-NoAuth"] == "true")
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>();
        var custom = Request.Headers["X-Test-Auth"].ToString();
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var parts = custom.Split(':');
            claims.Add(new Claim("role", parts[0]));
            claims.Add(new Claim("sub", parts.Length > 1 ? parts[1] : Guid.NewGuid().ToString()));
            if (parts.Length > 2) claims.Add(new Claim("restaurantId", parts[2]));
        }
        else
        {
            claims.Add(new Claim("sub", Guid.NewGuid().ToString()));
            claims.Add(new Claim("role", "Admin"));
        }

        var identity = new ClaimsIdentity(claims, Scheme, "sub", "role");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}
