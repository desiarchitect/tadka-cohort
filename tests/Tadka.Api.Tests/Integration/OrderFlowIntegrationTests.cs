using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Api.Contracts.Orders;
using Tadka.Api.Data;
using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// HTTP-level tests of the Day-4 hardening: server-side pricing, idempotency (ADR-011),
/// optimistic concurrency (ADR-012), and the error contract (404 / 422 / 409).
/// These exercise the full pipeline — routing, validation, EF on real Postgres, middleware.
/// </summary>
public class OrderFlowIntegrationTests(TadkaApiFactory factory) : IClassFixture<TadkaApiFactory>
{
    private readonly TadkaApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task PlaceOrder_returns_201_with_server_calculated_total()
    {
        var request = await BuildOrderRequestAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/orders", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>();
        Assert.NotNull(order);
        Assert.Equal("Created", order!.Status);
        Assert.True(order.TotalAmount.Amount > 0); // priced by the server, not the client
    }

    [Fact]
    public async Task PlaceOrder_twice_with_same_Idempotency_Key_creates_only_one_order()
    {
        var request = await BuildOrderRequestAsync();
        var key = Guid.NewGuid().ToString();

        var first = await PostOrderWithKeyAsync(request, key);
        var second = await PostOrderWithKeyAsync(request, key); // the "double-tap"

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // replay, not a new resource

        var firstOrder = await first.Content.ReadFromJsonAsync<OrderResponse>();
        var secondOrder = await second.Content.ReadFromJsonAsync<OrderResponse>();
        Assert.Equal(firstOrder!.Id, secondOrder!.Id); // SAME order — no duplicate
    }

    [Fact]
    public async Task PlaceOrder_concurrent_same_Idempotency_Key_creates_only_one_order()
    {
        // Sequential replay is 201 then 200 via Find. Two requests that both miss Find
        // hit the unique constraint; the loser must still 200 with the same id, not 500.
        var request = await BuildOrderRequestAsync();
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(
            PostOrderWithKeyAsync(request, key),
            PostOrderWithKeyAsync(request, key));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, created);
        Assert.Equal(1, ok);

        var a = await responses[0].Content.ReadFromJsonAsync<OrderResponse>();
        var b = await responses[1].Content.ReadFromJsonAsync<OrderResponse>();
        Assert.Equal(a!.Id, b!.Id);
    }

    [Fact]
    public async Task IllegalTransition_returns_422()
    {
        var orderId = await PlaceOrderAsync();

        // Created -> Delivered skips the whole machine: a domain-rule violation, not a race.
        var response = await PatchStatusAsync(orderId, "Delivered");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UnknownOrder_returns_404()
    {
        var response = await _client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TwoConcurrent_status_updates_do_not_both_succeed()
    {
        var orderId = await PlaceOrderAsync();

        // Fire two confirmations at the same moment. Optimistic concurrency (xmin) guarantees they
        // cannot both win: the racer that commits second gets 409 (lost the race), or — if they
        // happen to serialise — the second sees an already-Confirmed order and gets 422. Either way
        // there is no lost update and the order is confirmed exactly once.
        var a = PatchStatusAsync(orderId, "Confirmed");
        var b = PatchStatusAsync(orderId, "Confirmed");
        var responses = await Task.WhenAll(a, b);

        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        var conflictCount = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict || r.StatusCode == HttpStatusCode.UnprocessableEntity);

        Assert.Equal(1, okCount);
        Assert.Equal(1, conflictCount);
    }

    [Fact]
    public async Task xmin_concurrency_token_rejects_a_stale_write()
    {
        // Deterministically reproduce the true race the HTTP layer rarely hits by hand:
        // two DbContexts read the SAME order (same xmin), both transition, both save.
        var orderId = await PlaceOrderAsync();

        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<TadkaDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<TadkaDbContext>();

        var orderA = await dbA.Orders.FirstAsync(o => o.Id == orderId); // reads version N
        var orderB = await dbB.Orders.FirstAsync(o => o.Id == orderId); // reads the SAME version N

        orderA.Transition(OrderStatus.Confirmed);
        await dbA.SaveChangesAsync(); // commits → row's xmin moves to N+1

        orderB.Transition(OrderStatus.Confirmed);
        // B's UPDATE carries the stale xmin (N): it matches 0 rows → EF raises the conflict.
        // This is the exception the middleware maps to HTTP 409 (ADR-012).
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    // --- helpers -------------------------------------------------------------

    private async Task<HttpResponseMessage> PostOrderWithKeyAsync(CreateOrderRequest request, string key)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", key);
        return await _client.SendAsync(message);
    }

    private Task<HttpResponseMessage> PatchStatusAsync(Guid orderId, string status) =>
        _client.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new UpdateOrderStatusRequest(status));

    private async Task<Guid> PlaceOrderAsync()
    {
        var request = await BuildOrderRequestAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/orders", request);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>();
        return order!.Id;
    }

    /// <summary>Builds a valid order from seeded data: first restaurant, its first available menu item.</summary>
    private async Task<CreateOrderRequest> BuildOrderRequestAsync()
    {
        var (restaurantId, menuItemId) = await DiscoverSeedAsync();
        return new CreateOrderRequest(
            CustomerId: Guid.NewGuid(), // no cross-schema FK on CustomerId (ADR-008), so any id is valid
            RestaurantId: restaurantId,
            Items: [new CreateOrderItemRequest(menuItemId, Quantity: 2, SpecialInstructions: "extra spicy")],
            DeliveryAddress: new OrderAddressRequest(
                "12 MG Road", "Indiranagar", "Bengaluru", "560038", 12.9716, 77.5946));
    }

    private async Task<(Guid RestaurantId, Guid MenuItemId)> DiscoverSeedAsync()
    {
        var restaurantId = (await GetFirstIdAsync("/api/v1/restaurants"))
            ?? throw new InvalidOperationException("No seeded restaurants found.");

        using var menuDoc = JsonDocument.Parse(
            await _client.GetStringAsync($"/api/v1/restaurants/{restaurantId}/menu"));
        var items = Unwrap(menuDoc.RootElement);
        foreach (var item in items.EnumerateArray())
        {
            var available = !item.TryGetProperty("isAvailable", out var a) || a.GetBoolean();
            if (available)
                return (restaurantId, item.GetProperty("id").GetGuid());
        }
        throw new InvalidOperationException("No available menu item found.");
    }

    private async Task<Guid?> GetFirstIdAsync(string url)
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync(url));
        var array = Unwrap(doc.RootElement);
        foreach (var element in array.EnumerateArray())
            return element.GetProperty("id").GetGuid();
        return null;
    }

    /// <summary>Accepts either a bare JSON array or a paged envelope ({ items: [...] }).</summary>
    private static JsonElement Unwrap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root
        : root.TryGetProperty("items", out var items) ? items
        : root;
}
