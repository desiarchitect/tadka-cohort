using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Auth;

/// <summary>
/// Issues short-lived RS256 access tokens (ADR-030, signing switched from HS256 in ADR-049). Claims:
/// <c>sub</c> (userId), <c>role</c>, <c>email</c>, and <c>restaurantId</c> for owners (used by
/// resource-ownership authz, ADR-031). Refresh-token issuance/rotation lives in <see cref="RefreshTokenService"/>
/// (ADR-048) — this class only ever mints the access token.
/// </summary>
public sealed class TokenService(IOptions<JwtOptions> options, SigningKeyStore keys)
{
    private readonly JwtOptions _o = options.Value;

    public string CreateAccessToken(User user)
    {
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("role", user.Role.ToString()),
            new("email", user.Email),
            // Standard practice (RFC 7519 §4.1.7): a unique token ID. Without it, two tokens minted for
            // the same user in the same second (e.g. login immediately followed by refresh) would carry
            // identical claims + identical `exp` and — since RS256 is deterministic — serialize to the
            // exact same bytes. `jti` also gives an on-call engineer something to grep logs for one token.
            new("jti", Guid.NewGuid().ToString())
        };
        if (user.OwnedRestaurantId is { } restaurantId)
            claims.Add(new Claim("restaurantId", restaurantId.ToString()));

        var signingKey = keys.Current;
        var rsaKey = new RsaSecurityKey(signingKey.Rsa) { KeyId = signingKey.Kid };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(_o.AccessTokenMinutes),
            // RS256: the monolith holds the private half; every verifier — including this same process,
            // via the identical JWKS-shaped lookup (ADR-049) — checks only the public half.
            SigningCredentials = new SigningCredentials(rsaKey, SecurityAlgorithms.RsaSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public int AccessTokenSeconds => _o.AccessTokenMinutes * 60;
}
