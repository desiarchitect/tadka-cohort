using System.Security.Claims;

namespace Tadka.Api.Auth;

/// <summary>JWT config (ADR-030). Shared signing key — the Payment service verifies with the same key (defense in depth, ADR-031).</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "tadka";
    public string Audience { get; set; } = "tadka";
    public string SigningKey { get; set; } = "";   // HS256 needs ≥ 32 bytes
    public int AccessTokenMinutes { get; set; } = 15;
}

// Auth HTTP contracts (ADR-030).
public sealed record RegisterRequest(string Name, string Email, string Phone, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record TokenResponse(string AccessToken, int ExpiresInSeconds, string Role);

/// <summary>Read identity off the validated JWT (claim types: <c>sub</c>, <c>role</c>, <c>restaurantId</c>).</summary>
public static class ClaimsExtensions
{
    public static Guid? UserId(this ClaimsPrincipal u)
    {
        var s = u.FindFirst("sub")?.Value ?? u.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(s, out var g) ? g : null;
    }

    public static Guid? OwnedRestaurantId(this ClaimsPrincipal u)
        => Guid.TryParse(u.FindFirst("restaurantId")?.Value, out var g) ? g : null;

    public static bool IsAdmin(this ClaimsPrincipal u) => u.IsInRole("Admin");
}
