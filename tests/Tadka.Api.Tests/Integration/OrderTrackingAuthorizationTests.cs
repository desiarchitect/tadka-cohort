using System.Net;
using System.Net.Http.Json;
using Tadka.Api.Contracts.Orders;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Fix 1 (BOLA/IDOR on the live-tracking SSE stream): <c>OrderTrackingController.GetEvents</c> must
/// apply the same resource-ownership check as <c>OrdersController.GetById</c> (ADR-031) — before this
/// fix, any authenticated customer could stream ANY order id just by guessing a guid.
///
/// The test suite runs with no Redis (see <see cref="TadkaApiFactory"/>), so a request that clears the
/// ownership check still gets 503 from the "Redis required" branch further down, not 200 — that's fine
/// and deterministic; 503 proves the ownership gate was passed, which is exactly the boundary this test
/// is protecting. 403/404 must be returned BEFORE that branch is ever reached.
/// </summary>
public class OrderTrackingAuthorizationTests(TadkaApiFactory factory) : IClassFixture<TadkaApiFactory>
{
    private readonly TadkaApiFactory _factory = factory;

    private static readonly object OrderBody = new
    {
        customerId = "c1b2c3d4-0001-4000-8000-000000000001",
        restaurantId = "a1b2c3d4-0001-4000-8000-000000000001",
        items = new[] { new { menuItemId = "b1b2c3d4-0001-4000-8000-000000000001", quantity = 1 } },
        deliveryAddress = new { line1 = "x", line2 = "y", city = "Bengaluru", pincode = "560038", latitude = 12.97, longitude = 77.59 }
    };

    private static readonly Guid Priya = new("c1b2c3d4-0001-4000-8000-000000000001");

    [Fact]
    public async Task Another_customer_streaming_someone_elses_order_gets_403_not_the_stream()
    {
        var client = _factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/orders", OrderBody);
        created.EnsureSuccessStatusCode();
        var order = await created.Content.ReadFromJsonAsync<OrderResponse>();

        var asOther = Get($"/api/v1/orders/{order!.Id}/events", $"Customer:{Guid.NewGuid()}");
        var resp = await client.SendAsync(asOther);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_streaming_any_order_clears_the_ownership_gate()
    {
        var client = _factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/orders", OrderBody);
        created.EnsureSuccessStatusCode();
        var order = await created.Content.ReadFromJsonAsync<OrderResponse>();

        // No X-Test-Auth header → TestAuthHandler defaults to Admin (see AuthorizationTests).
        var resp = await client.GetAsync($"/api/v1/orders/{order!.Id}/events");
        // Never 403/404 for an admin on a real order; 503 here is the test suite's deterministic
        // no-Redis outcome, proving the ownership gate did not block the request.
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Owner_streaming_their_own_order_clears_the_ownership_gate()
    {
        var client = _factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/orders", OrderBody);
        created.EnsureSuccessStatusCode();
        var order = await created.Content.ReadFromJsonAsync<OrderResponse>();

        var asOwner = Get($"/api/v1/orders/{order!.Id}/events", $"Customer:{Priya}");
        var resp = await client.SendAsync(asOwner);
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Streaming_a_nonexistent_order_returns_404()
    {
        var client = _factory.CreateClient();
        var req = Get($"/api/v1/orders/{Guid.NewGuid()}/events", $"Customer:{Priya}");
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static HttpRequestMessage Get(string url, string testAuth)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Test-Auth", testAuth);
        return req;
    }
}
