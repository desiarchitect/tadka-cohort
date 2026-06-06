using System.Security.Claims;

namespace Tadka.Restaurant.Api;

/// <summary>Read identity off the validated JWT (claim types: <c>sub</c>, <c>role</c>, <c>restaurantId</c>) —
/// the Restaurant service validates the SAME token as the monolith (per-service JWT, ADR-031).</summary>
public static class ClaimsExtensions
{
    public static Guid? OwnedRestaurantId(this ClaimsPrincipal u)
        => Guid.TryParse(u.FindFirst("restaurantId")?.Value, out var g) ? g : null;

    public static bool IsAdmin(this ClaimsPrincipal u) => u.IsInRole("Admin");
}
