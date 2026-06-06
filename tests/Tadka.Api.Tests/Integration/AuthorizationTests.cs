using System.Net;
using System.Net.Http.Json;
using Tadka.Api.Contracts.Orders;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// AuthN/AuthZ behaviour (ADR-030/031). The TestAuthHandler lets each request pick its identity via
/// X-Test-* headers: no header → Admin; <c>X-Test-NoAuth</c> → anonymous; <c>X-Test-Auth: Role:sub:restId</c>.
/// </summary>
public class AuthorizationTests(TadkaApiFactory factory) : IClassFixture<TadkaApiFactory>
{
    private readonly TadkaApiFactory _factory = factory;

    private static readonly Guid Priya = new("c1b2c3d4-0001-4000-8000-000000000001");
    // NOTE: the "owner cannot edit another restaurant's menu" RBAC+ownership test moved to
    // Tadka.Restaurant.Api.Tests on Day 12 — that endpoint now lives in the Restaurant service (ADR-036).

    private static readonly object OrderBody = new
    {
        customerId = "c1b2c3d4-0001-4000-8000-000000000001",
        restaurantId = "a1b2c3d4-0001-4000-8000-000000000001",
        items = new[] { new { menuItemId = "b1b2c3d4-0001-4000-8000-000000000001", quantity = 1 } },
        deliveryAddress = new { line1 = "x", line2 = "y", city = "Bengaluru", pincode = "560038", latitude = 12.97, longitude = 77.59 }
    };

    [Fact]
    public async Task No_token_is_rejected_with_401()  // Demo 1 — auth bypass closed
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(OrderBody) };
        req.Headers.Add("X-Test-NoAuth", "true");
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Customer_cannot_read_another_customers_order_403()  // Demo 2 — resource ownership
    {
        var client = _factory.CreateClient();
        // Admin places an order owned by Priya.
        var created = await client.PostAsJsonAsync("/api/v1/orders", OrderBody);
        created.EnsureSuccessStatusCode();
        var order = await created.Content.ReadFromJsonAsync<OrderResponse>();

        // A DIFFERENT customer tries to read it → 403.
        var asOther = Get($"/api/v1/orders/{order!.Id}", $"Customer:{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(asOther)).StatusCode);

        // The owner (Priya) can read it → 200.
        var asOwner = Get($"/api/v1/orders/{order.Id}", $"Customer:{Priya}");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(asOwner)).StatusCode);
    }

    [Fact]
    public async Task Login_with_seeded_credentials_returns_a_token()  // real JWT path (anonymous endpoint)
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "priya@tadka.test", password = "Password123!" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.True(body!.ContainsKey("accessToken"));
        Assert.False(string.IsNullOrWhiteSpace(body["accessToken"]?.ToString()));
    }

    private static HttpRequestMessage Get(string url, string testAuth)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Test-Auth", testAuth);
        return req;
    }
}
