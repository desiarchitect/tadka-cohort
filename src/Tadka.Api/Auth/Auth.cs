using System.Security.Claims;

namespace Tadka.Api.Auth;

/// <summary>
/// JWT config (ADR-030, signing switched to RS256 in ADR-049). No shared secret any more — the monolith
/// signs with its own <see cref="SigningKeyStore"/> RSA key; every verifier (including Payment.Api) fetches
/// the PUBLIC key from <c>/.well-known/jwks.json</c> instead of holding a copy of a signing secret.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "tadka";
    public string Audience { get; set; } = "tadka";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}

/// <summary>Fixed-window rate limiting on the auth write endpoints (ADR-047).</summary>
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "Auth:RateLimit";
    public int PermitLimit { get; set; } = 5;
    public int WindowSeconds { get; set; } = 10;
}

/// <summary>Per-account brute-force lockout thresholds (ADR-047).</summary>
public sealed class AccountLockoutOptions
{
    public const string SectionName = "Auth:Lockout";
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutSeconds { get; set; } = 60;
}

// Auth HTTP contracts (ADR-030/048).
public sealed record RegisterRequest(string Name, string Email, string Phone, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string RefreshToken);
public sealed record TokenResponse(string AccessToken, int ExpiresInSeconds, string Role, string RefreshToken);

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
