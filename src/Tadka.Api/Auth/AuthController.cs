using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Api.Data;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Auth;

/// <summary>
/// Register + login → access + refresh tokens (ADR-030/048); refresh/logout/rotate-signing-key round out
/// the token lifecycle (ADR-066/049). Register/login/refresh are anonymous (you can't have a token before
/// you log in) but rate-limited (ADR-065); logout and key rotation require an authenticated caller.
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
    [ServiceFilter(typeof(AuthThrottleFilter))]
    public async Task<ActionResult<TokenResponse>> Register([FromBody] RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Set<User>().AnyAsync(u => u.Email == email))
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = "Email already registered.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.10"
            });

        var user = new User { Name = request.Name, Email = email, Phone = request.Phone, Role = UserRole.Customer };
        user.PasswordHash = hasher.HashPassword(user, request.Password); // never store plaintext (ADR-030/032)
        db.Add(user);
        await db.SaveChangesAsync();

        return Ok(await IssueTokenPairAsync(user));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ServiceFilter(typeof(AuthThrottleFilter))]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Email == email);

        // Same generic response whether the user is missing, the password is wrong, or the account is
        // locked — a distinct "locked" response would tell an attacker their guess-flood is working and
        // confirm the email exists (ADR-065's honesty-about-the-trade-off call).
        if (user is null)
            return Unauthorized(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = "Invalid credentials.",
                Type = "https://tools.ietf.org/html/rfc7235#section-3.1"
            });

        if (user.LockedUntil is { } until && until > DateTime.UtcNow)
            return Unauthorized(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = "Invalid credentials.",
                Type = "https://tools.ietf.org/html/rfc7235#section-3.1"
            });

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= _lockout.MaxFailedAttempts)
            {
                user.LockedUntil = DateTime.UtcNow.AddSeconds(_lockout.LockoutSeconds);
                user.FailedLoginAttempts = 0; // fresh count for the next window after the lock clears
            }
            await db.SaveChangesAsync();
            return Unauthorized(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = "Invalid credentials.",
                Type = "https://tools.ietf.org/html/rfc7235#section-3.1"
            });
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync();

        return Ok(await IssueTokenPairAsync(user));
    }

    /// <summary>Single-use rotation (ADR-066): the presented refresh token is consumed; a new pair comes back.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ServiceFilter(typeof(AuthThrottleFilter))]
    public async Task<ActionResult<TokenResponse>> Refresh([FromBody] RefreshRequest request)
    {
        var result = await refreshTokens.RotateAsync(request.RefreshToken);
        // 401 either way — ReuseDetected isn't revealed to the caller (ADR-066); it's still logged
        // server-side by SaveChanges/EF's own logging, which is enough for an on-call investigation.
        if (result.Outcome != RefreshOutcome.Ok)
            return Unauthorized(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = "Invalid refresh token.",
                Type = "https://tools.ietf.org/html/rfc7235#section-3.1"
            });

        return Ok(new TokenResponse(result.AccessToken!, tokens.AccessTokenSeconds, result.User!.Role.ToString(), result.RawRefreshToken!));
    }

    /// <summary>
    /// Revokes the caller's current refresh-token family. Known limitation (ADR-066): the ACCESS token
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

    /// <summary>Admin-only key rotation (ADR-067): a fresh RSA keypair becomes the signer; the oldest drops once over the cap.</summary>
    [HttpPost("rotate-signing-key")]
    [Authorize(Roles = nameof(UserRole.Admin))]
    public IActionResult RotateSigningKey()
    {
        // A key shared by every replica (Jwt:SigningKeyPem) is rotated by deploying a new one, not by calling
        // one replica: a key generated here would be unknown to all the others.
        if (signingKeys.IsShared)
            return Problem(detail: "The signing key is shared by every replica; rotate it by deploying a new key.",
                statusCode: StatusCodes.Status409Conflict, title: "Signing Key Is Managed Outside The App");

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

