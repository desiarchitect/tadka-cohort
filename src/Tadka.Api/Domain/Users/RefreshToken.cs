namespace Tadka.Api.Domain.Users;

/// <summary>
/// A rotated, single-use refresh token (ADR-048). We never store the raw token — only its hash
/// (<see cref="TokenHash"/>), looked up directly on presentation. <see cref="FamilyId"/> ties every
/// token born from the same login into one rotation chain, so reuse of an already-rotated-away token
/// (a theft signal) can revoke the whole chain in one write, not just the one token.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>SHA-256 hex digest of the raw token (never the raw value itself — see ADR-048).</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Shared by every token in one rotation chain, starting at login/register.</summary>
    public Guid FamilyId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set when this token is rotated away (used once) or its family is revoked (reuse detected, logout).</summary>
    public DateTime? RevokedAt { get; set; }
}
