using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tadka.Api.Data;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Auth;

public enum RefreshOutcome
{
    Ok,
    Invalid,       // not found / expired / malformed — generic 401, don't say which (ADR-048)
    ReuseDetected  // presented token was already revoked → its whole family is now dead too
}

public readonly record struct RefreshResult(RefreshOutcome Outcome, string? AccessToken, string? RawRefreshToken, User? User);

/// <summary>
/// Refresh-token issuance + single-use rotation + reuse detection (ADR-048). We hash the raw token with a
/// fast, deterministic hash (SHA-256) rather than <c>IPasswordHasher</c> — a refresh token is a 256-bit
/// random secret (not a low-entropy user password), so it needs no per-value salt/slow-hash to resist
/// guessing, and a deterministic hash is what lets us look a presented token up by an indexed column
/// instead of iterating every stored hash (see ADR-048's Trade-off).
/// </summary>
public sealed class RefreshTokenService(TadkaDbContext db, TokenService tokens, IOptions<JwtOptions> options)
{
    private readonly JwtOptions _o = options.Value;

    /// <summary>Starts a brand-new rotation chain (login/register) and returns the raw token — persisted ONLY as a hash.</summary>
    public async Task<string> IssueNewFamilyAsync(Guid userId, CancellationToken ct = default)
        => await IssueAsync(userId, Guid.NewGuid(), ct);

    private async Task<string> IssueAsync(Guid userId, Guid familyId, CancellationToken ct)
    {
        var raw = GenerateRawToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(raw),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(_o.RefreshTokenDays)
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>
    /// Validates + rotates a presented refresh token. A valid token is revoked and replaced by a new one in
    /// the SAME family (rotation = single-use). A token that is already revoked is reuse of a stolen/replayed
    /// token — the entire family is revoked immediately (ADR-048), forcing a full re-login even for the
    /// legitimate holder of the newest token in that chain.
    /// </summary>
    public async Task<RefreshResult> RotateAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var now = DateTime.UtcNow;

        // Atomic claim (fix 4): the previous version was check-then-act - SELECT the row, check
        // RevokedAt in C#, and only write RevokedAt back on SaveChangesAsync much later. Two
        // concurrent requests presenting the SAME token both read RevokedAt == null, both passed
        // the check, and both rotated - reuse detection was silently skipped, and the family ended
        // up with two "current" tokens instead of one. This single UPDATE ... WHERE RevokedAt IS
        // NULL is atomic at the database: only one concurrent caller can ever claim the row.
        var claimed = await db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        if (claimed == 1)
        {
            // We won the claim. ExecuteUpdateAsync doesn't return the entity, so re-read it (now
            // revoked) for the FamilyId/UserId needed to issue the rotated token in the same family.
            var stored = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (stored is null)
                return new RefreshResult(RefreshOutcome.Invalid, null, null, null); // defensive; should not happen

            var claimedUser = await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
            if (claimedUser is null)
                return new RefreshResult(RefreshOutcome.Invalid, null, null, null);

            var newRaw = await IssueAsync(claimedUser.Id, stored.FamilyId, ct); // same family = rotation, not a fresh chain
            var accessToken = tokens.CreateAccessToken(claimedUser);
            return new RefreshResult(RefreshOutcome.Ok, accessToken, newRaw, claimedUser);
        }

        // Zero rows claimed does NOT automatically mean reuse. Look the token up separately (no
        // WHERE filter this time) and only treat it as genuine reuse if the row exists, is not
        // expired, AND is already revoked - i.e. someone is presenting a token that was already
        // consumed by an earlier, successful rotation. A missing row, an expired row, or a garbage
        // hash is just an invalid token: 401, and the family is left alone.
        var lookedUp = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (lookedUp is not null && lookedUp.RevokedAt is not null && lookedUp.ExpiresAt > now)
        {
            await RevokeFamilyAsync(lookedUp.FamilyId, ct);
            return new RefreshResult(RefreshOutcome.ReuseDetected, null, null, null);
        }

        return new RefreshResult(RefreshOutcome.Invalid, null, null, null);
    }

    /// <summary>Revokes every still-active token in a family — used by reuse detection and by logout.</summary>
    public async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct = default)
    {
        await db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
    }

    /// <summary>Looks a raw token up (no rotation) — used by logout to find which family to kill.</summary>
    public Task<RefreshToken?> FindAsync(string rawToken, CancellationToken ct = default)
        => db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == Hash(rawToken), ct);

    public static string GenerateRawToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
