using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Api.Data;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Auth;

/// <summary>
/// Register + login → access + refresh tokens (ADR-030/048); refresh/logout/rotate-signing-key round out
/// the token lifecycle (ADR-048/049). Register/login/refresh are anonymous (you can't have a token before
/// you log in) but rate-limited (ADR-047); logout and key rotation require an authenticated caller.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public class AuthController(
    TadkaDbContext db,
    TokenService tokens,
    RefreshTokenService refreshTokens,
    SigningKeyStore signingKeys,
    IPasswordHasher<User> hasher,
    IOptions<AccountLockoutOptions> lockoutOptions) : ControllerBase
{
    private readonly AccountLockoutOptions _lockout = lockoutOptions.Value;

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthWrite)]
    public async Task<ActionResult<TokenResponse>> Register([FromBody] RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Set<User>().AnyAsync(u => u.Email == email))
            return Conflict(new { error = "Email already registered." });

        var user = new User { Name = request.Name, Email = email, Phone = request.Phone, Role = UserRole.Customer };
        user.PasswordHash = hasher.HashPassword(user, request.Password); // never store plaintext (ADR-030/032)
        db.Add(user);
        await db.SaveChangesAsync();

        return Ok(await IssueTokenPairAsync(user));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthWrite)]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Email == email);

        // Same generic response whether the user is missing, the password is wrong, or the account is
        // locked — a distinct "locked" response would tell an attacker their guess-flood is working and
        // confirm the email exists (ADR-047's honesty-about-the-trade-off call).
        if (user is null)
            return Unauthorized(new { error = "Invalid credentials." });

        if (user.LockedUntil is { } until && until > DateTime.UtcNow)
            return Unauthorized(new { error = "Invalid credentials." });

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= _lockout.MaxFailedAttempts)
            {
                user.LockedUntil = DateTime.UtcNow.AddSeconds(_lockout.LockoutSeconds);
                user.FailedLoginAttempts = 0; // fresh count for the next window after the lock clears
            }
            await db.SaveChangesAsync();
            return Unauthorized(new { error = "Invalid credentials." });
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync();

        return Ok(await IssueTokenPairAsync(user));
    }

    /// <summary>Single-use rotation (ADR-048): the presented refresh token is consumed; a new pair comes back.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthWrite)]
    public async Task<ActionResult<TokenResponse>> Refresh([FromBody] RefreshRequest request)
    {
        var result = await refreshTokens.RotateAsync(request.RefreshToken);
        // 401 either way — ReuseDetected isn't revealed to the caller (ADR-048); it's still logged
        // server-side by SaveChanges/EF's own logging, which is enough for an on-call investigation.
        if (result.Outcome != RefreshOutcome.Ok)
            return Unauthorized(new { error = "Invalid refresh token." });

        return Ok(new TokenResponse(result.AccessToken!, tokens.AccessTokenSeconds, result.User!.Role.ToString(), result.RawRefreshToken!));
    }

    /// <summary>
    /// Revokes the caller's current refresh-token family. Known limitation (ADR-048): the ACCESS token
    /// already issued stays valid until its own short expiry — a stateless JWT can't be revoked without
    /// extra infra (a denylist), so this is "no more silent refreshes," not "instantly logged out."
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request)
    {
        var userId = User.UserId();
        var stored = await refreshTokens.FindAsync(request.RefreshToken);
        // Only ever revoke a family that belongs to the caller — otherwise 204 no-ops silently (don't
        // leak whether the token was someone else's valid token vs. garbage).
        if (stored is not null && stored.UserId == userId)
            await refreshTokens.RevokeFamilyAsync(stored.FamilyId);

        return NoContent();
    }

    /// <summary>Admin-only key rotation (ADR-049): a fresh RSA keypair becomes the signer; the oldest drops once over the cap.</summary>
    [HttpPost("rotate-signing-key")]
    [Authorize(Roles = nameof(UserRole.Admin))]
    public IActionResult RotateSigningKey()
    {
        var key = signingKeys.Rotate();
        return Ok(new { kid = key.Kid, createdAt = key.CreatedAt });
    }

    private async Task<TokenResponse> IssueTokenPairAsync(User user)
    {
        var access = tokens.CreateAccessToken(user);
        var refresh = await refreshTokens.IssueNewFamilyAsync(user.Id);
        return new TokenResponse(access, tokens.AccessTokenSeconds, user.Role.ToString(), refresh);
    }
}

/// <summary>Named rate-limiting policies (ADR-047).</summary>
public static class RateLimitPolicies
{
    public const string AuthWrite = "auth-write";
}
