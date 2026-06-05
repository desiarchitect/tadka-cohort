using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Auth;

/// <summary>Register + login → a JWT (ADR-030). Anonymous (you can't have a token before you log in).</summary>
[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
public class AuthController(TadkaDbContext db, TokenService tokens, IPasswordHasher<User> hasher) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<TokenResponse>> Register([FromBody] RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Set<User>().AnyAsync(u => u.Email == email))
            return Conflict(new { error = "Email already registered." });

        var user = new User { Name = request.Name, Email = email, Phone = request.Phone, Role = UserRole.Customer };
        user.PasswordHash = hasher.HashPassword(user, request.Password); // never store plaintext (ADR-030/032)
        db.Add(user);
        await db.SaveChangesAsync();

        return Ok(tokens.CreateAccessToken(user));
    }

    [HttpPost("login")]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Email == email);
        // Same response whether the user is missing or the password is wrong (don't leak which).
        if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return Unauthorized(new { error = "Invalid credentials." });

        return Ok(tokens.CreateAccessToken(user));
    }
}
