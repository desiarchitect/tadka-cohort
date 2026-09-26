using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tadka.Payment.Api.Tests;

/// <summary>
/// Test auth scheme: authenticates as Admin (random sub) by default, so the existing charge/query tests
/// don't need to know about auth at all. Override per request with headers, mirroring Tadka.Api's own
/// TestAuthHandler:
///   <c>X-Test-NoAuth: true</c>       → not authenticated (→ 401)
///   <c>X-Test-Auth: Role:sub</c>     → authenticate as that role/user (→ ownership 403 tests, ADR-031)
/// </summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-Test-NoAuth"] == "true")
            return Task.FromResult(AuthenticateResult.NoResult());

        var spec = Request.Headers["X-Test-Auth"].FirstOrDefault();
        var role = "Admin";
        var sub = Guid.NewGuid().ToString();
        if (!string.IsNullOrWhiteSpace(spec))
        {
            var parts = spec.Split(':');
            role = parts[0];
            if (parts.Length > 1) sub = parts[1];
        }

        var identity = new ClaimsIdentity(
            [new Claim("sub", sub), new Claim("role", role)], Scheme, "sub", "role");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}
