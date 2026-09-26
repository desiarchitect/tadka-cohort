using System.Security.Claims;

namespace Tadka.Payment.Api.Auth;

/// <summary>
/// Read identity off the validated JWT (claim types: <c>sub</c>, <c>role</c>) — this service's own copy of
/// the same helper Tadka.Api has (ADR-024/026: no shared code across services). Used to enforce resource
/// ownership on the payment-read endpoint (ADR-031), not just authentication.
/// </summary>
public static class ClaimsExtensions
{
    public static Guid? UserId(this ClaimsPrincipal u)
    {
        var s = u.FindFirst("sub")?.Value ?? u.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(s, out var g) ? g : null;
    }

    public static bool IsAdmin(this ClaimsPrincipal u) => u.IsInRole("Admin");
}
