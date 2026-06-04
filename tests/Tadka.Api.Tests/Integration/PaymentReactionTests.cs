using System.Net.Http.Json;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Api.Contracts.Orders;
using Tadka.Api.Data;
using Tadka.Api.Domain.Common.Events;
using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// After Day 8 the charge happens in a SEPARATE service; the monolith only reacts to the shared payment
/// events (ADR-024/025). These tests pin that reaction seam deterministically: place a real order (Payment
/// is "Off", so it stays Created), publish the shared <see cref="PaymentCompletedEvent"/>/<see cref="PaymentFailedEvent"/>
/// through the REAL mediator, and assert the order converges via the REAL state machine — no network, no flake.
/// </summary>
public class PaymentReactionTests(TadkaApiFactory factory) : IClassFixture<TadkaApiFactory>
{
    private readonly TadkaApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task PaymentCompletedEvent_auto_confirms_the_order()
    {
        var orderId = await PlaceOrderAsync();

        await PublishAsync(new PaymentCompletedEvent(orderId, "FAKEPAY-TEST-0001"));

        Assert.Equal(OrderStatus.Confirmed, await OrderStatusAsync(orderId));
    }

    [Fact]
    public async Task PaymentFailedEvent_cancels_the_order()
    {
        var orderId = await PlaceOrderAsync();

        await PublishAsync(new PaymentFailedEvent(orderId, "declined (test)"));

        Assert.Equal(OrderStatus.Cancelled, await OrderStatusAsync(orderId));
    }

    // --- harness -------------------------------------------------------------

    private async Task PublishAsync(INotification evt)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt);
    }

    private async Task<OrderStatus> OrderStatusAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();
        return (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status;
    }

    private async Task<Guid> PlaceOrderAsync()
    {
        var (restaurantId, menuItemId) = await DiscoverSeedAsync();
        var request = new CreateOrderRequest(
            CustomerId: Guid.NewGuid(),
            RestaurantId: restaurantId,
            Items: [new CreateOrderItemRequest(menuItemId, Quantity: 1, SpecialInstructions: null)],
            DeliveryAddress: new OrderAddressRequest("1 St", "Area", "Bengaluru", "560038", 12.97, 77.59));

        var response = await _client.PostAsJsonAsync("/api/v1/orders", request);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>();
        return order!.Id;
    }

    private async Task<(Guid RestaurantId, Guid MenuItemId)> DiscoverSeedAsync()
    {
        using var rDoc = JsonDocument.Parse(await _client.GetStringAsync("/api/v1/restaurants"));
        var restaurantId = Unwrap(rDoc.RootElement).EnumerateArray().First().GetProperty("id").GetGuid();

        using var mDoc = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/restaurants/{restaurantId}/menu"));
        foreach (var item in Unwrap(mDoc.RootElement).EnumerateArray())
        {
            var available = !item.TryGetProperty("isAvailable", out var a) || a.GetBoolean();
            if (available) return (restaurantId, item.GetProperty("id").GetGuid());
        }
        throw new InvalidOperationException("No available menu item found.");
    }

    private static JsonElement Unwrap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root
        : root.TryGetProperty("items", out var items) ? items
        : root;
}
