using System.Security.Claims;

namespace Tadka.Delivery.Api.Auth;

/// <summary>Read identity off the validated JWT (claim types <c>sub</c>, <c>role</c>) — this service's own copy
/// of the helper the monolith has (ADR-033: no shared code across services).</summary>
public static class ClaimsExtensions
{
    public static Guid? UserId(this ClaimsPrincipal u)
    {
        var s = u.FindFirst("sub")?.Value ?? u.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(s, out var g) ? g : null;
    }

    public static bool IsAdmin(this ClaimsPrincipal u) => u.IsInRole("Admin");

    public static bool IsRider(this ClaimsPrincipal u) => u.IsInRole("DeliveryAgent");
}
