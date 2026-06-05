using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Auth;

/// <summary>
/// Issues short-lived HS256 access tokens (ADR-030). Claims: <c>sub</c> (userId), <c>role</c>,
/// <c>email</c>, and <c>restaurantId</c> for owners (used by resource-ownership authz, ADR-031).
/// Refresh-token rotation is the documented production strategy (ADR-030); only the access token is wired.
/// </summary>
public sealed class TokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _o = options.Value;

    public TokenResponse CreateAccessToken(User user)
    {
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("role", user.Role.ToString()),
            new("email", user.Email)
        };
        if (user.OwnedRestaurantId is { } restaurantId)
            claims.Add(new Claim("restaurantId", restaurantId.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(_o.AccessTokenMinutes),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new TokenResponse(token, _o.AccessTokenMinutes * 60, user.Role.ToString());
    }
}
