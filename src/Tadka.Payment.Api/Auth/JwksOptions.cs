namespace Tadka.Payment.Api.Auth;

/// <summary>
/// Where this service fetches Tadka.Api's public signing keys from, and how long it trusts a fetched
/// copy before refetching (ADR-049). Bound from the same <c>Jwt</c> section Issuer/Audience already live in.
/// </summary>
public sealed class JwksOptions
{
    public const string SectionName = "Jwt";
    public string JwksBaseUrl { get; set; } = "http://localhost:5224";
    public int JwksCacheMinutes { get; set; } = 5;
}
