using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tadka.Delivery.Api.Data;

namespace Tadka.Delivery.Api.Tests;

/// <summary>
/// The same ownership rules, but through the service's REAL JWT bearer validation with real HS256 tokens shaped
/// exactly like the monolith's TokenService issues them (<c>sub</c>, <c>role</c>). TestAuthHandler builds claims
/// directly and never goes through the handler's inbound claim renaming, so without these tests a missing
/// <c>MapInboundClaims = false</c> would make every real rider's IsInRole("DeliveryAgent") false (403 for the
/// assigned rider) while the whole suite stayed green.
/// </summary>
public class RealJwtAuthorizationTests(RealJwtDeliveryApiFactory factory) : IClassFixture<RealJwtDeliveryApiFactory>
{
    private const string SigningKey = "tadka-dev-signing-key-change-in-prod-0123456789abcdef"; // Delivery's appsettings.json (dev only)
    private readonly RealJwtDeliveryApiFactory _factory = factory;

    private static string Token(Guid sub, string role)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "tadka",
            Audience = "tadka",
            Subject = new ClaimsIdentity([new Claim("sub", sub.ToString()), new Claim("role", role)]),
            Expires = DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static HttpRequestMessage With(HttpMethod method, string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    [Fact]
    public async Task A_real_rider_token_can_post_its_own_location_and_real_customer_tokens_get_the_right_answer()
    {
        var orderId = Guid.NewGuid();
        var customer = Guid.NewGuid();
        Guid riderUser;
        using (var scope = _factory.Services.CreateScope())
        {
            var r = (await scope.ServiceProvider.GetRequiredService<DeliveryService>().AssignAsync(orderId, customerId: customer))!;
            riderUser = (await scope.ServiceProvider.GetRequiredService<DeliveryDbContext>().Agents.AsNoTracking()
                .SingleAsync(a => a.Id == r.AgentId)).UserId!.Value;
        }
        var client = _factory.CreateClient();

        var put = await client.SendAsync(With(HttpMethod.Put, $"/api/v1/deliveries/{orderId}/location",
            Token(riderUser, "DeliveryAgent"), new { Latitude = 12.95, Longitude = 77.64 }));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); // 403 here = the role claim was renamed away

        var owner = await client.SendAsync(With(HttpMethod.Get, $"/api/v1/deliveries/{orderId}/track", Token(customer, "Customer")));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        var other = await client.SendAsync(With(HttpMethod.Get, $"/api/v1/deliveries/{orderId}/track", Token(Guid.NewGuid(), "Customer")));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);

        var none = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/deliveries/{orderId}/track"));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
    }
}
