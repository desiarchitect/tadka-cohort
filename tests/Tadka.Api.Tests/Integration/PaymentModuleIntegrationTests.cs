using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MediatR;
using Tadka.Api.Contracts.Orders;
using Tadka.Api.Data;
using Tadka.Api.Domain.Orders;
using Tadka.Api.Domain.Payments;
using Tadka.Api.Domain.ValueObjects;
using Tadka.Api.Infrastructure.Resilience;
using Tadka.Api.Modules.Payments;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Deterministic tests for the Day-7 Payment module (ADR-021/022/023). We place a real order over HTTP
/// (the shared factory runs Payment in "Off" mode, so it stays Created), then drive <see cref="PaymentService"/>
/// directly with a controllable gateway — no background timing, no flake. The REAL MediatR handlers and the
/// REAL order state machine run, so these assert the whole seam: charge → event → order reaction.
/// </summary>
public class PaymentModuleIntegrationTests(TadkaApiFactory factory) : IClassFixture<TadkaApiFactory>
{
    private readonly TadkaApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task SuccessfulPayment_completes_payment_and_auto_confirms_the_order()
    {
        var (orderId, amount) = await PlaceOrderAsync();

        await ProcessPaymentAsync(orderId, amount, new StubGateway(GatewayMode.Succeed));

        Assert.Equal(PaymentStatus.Completed, await PaymentStatusAsync(orderId));
        Assert.Equal(OrderStatus.Confirmed, await OrderStatusAsync(orderId)); // converged via PaymentCompleted
    }

    [Fact]
    public async Task DeclinedPayment_marks_failed_and_cancels_the_order()
    {
        var (orderId, amount) = await PlaceOrderAsync();

        await ProcessPaymentAsync(orderId, amount, new StubGateway(GatewayMode.Decline));

        Assert.Equal(PaymentStatus.Failed, await PaymentStatusAsync(orderId));
        Assert.Equal(OrderStatus.Cancelled, await OrderStatusAsync(orderId)); // converged via PaymentFailed
    }

    [Fact]
    public async Task SlowGateway_is_abandoned_by_the_Polly_timeout_and_fails_fast()
    {
        var (orderId, amount) = await PlaceOrderAsync();

        // Gateway would take 5s; the pipeline's timeout is 200ms → it must fail fast, not hang.
        var slowGateway = new StubGateway(GatewayMode.Slow, TimeSpan.FromSeconds(5));
        var tinyTimeout = new PaymentResiliencePipeline(
            Options.Create(new PaymentOptions { TimeoutSeconds = 0.2, MaxConcurrentCharges = 10 }));

        var start = DateTime.UtcNow;
        await ProcessPaymentAsync(orderId, amount, slowGateway, tinyTimeout);
        var elapsed = DateTime.UtcNow - start;

        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Expected fail-fast (~200ms), took {elapsed.TotalMilliseconds:N0}ms");
        Assert.Equal(PaymentStatus.Failed, await PaymentStatusAsync(orderId)); // timed out → failed
        Assert.Equal(OrderStatus.Cancelled, await OrderStatusAsync(orderId));
    }

    [Fact]
    public async Task ChargingTheSameOrderTwice_creates_only_one_payment()
    {
        var (orderId, amount) = await PlaceOrderAsync();

        await ProcessPaymentAsync(orderId, amount, new StubGateway(GatewayMode.Succeed));
        await ProcessPaymentAsync(orderId, amount, new StubGateway(GatewayMode.Succeed)); // redelivery

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        Assert.Equal(1, await db.Payments.CountAsync(p => p.OrderId == orderId)); // idempotent — no double charge
    }

    // --- harness -------------------------------------------------------------

    private async Task ProcessPaymentAsync(
        Guid orderId, Money amount, IPaymentGateway gateway, PaymentResiliencePipeline? pipeline = null)
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = new PaymentService(
            sp.GetRequiredService<PaymentDbContext>(),
            gateway,
            pipeline ?? sp.GetRequiredService<PaymentResiliencePipeline>(),
            sp.GetRequiredService<IMediator>(), // the REAL mediator → real order-reaction handlers run
            NullLogger<PaymentService>.Instance);

        await service.ProcessAsync(orderId, amount);
    }

    private async Task<PaymentStatus> PaymentStatusAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return (await db.Payments.SingleAsync(p => p.OrderId == orderId)).Status;
    }

    private async Task<OrderStatus> OrderStatusAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();
        return (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status;
    }

    private async Task<(Guid OrderId, Money Amount)> PlaceOrderAsync()
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
        return (order!.Id, new Money(order.TotalAmount.Amount, order.TotalAmount.Currency));
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

    private enum GatewayMode { Succeed, Decline, Slow }

    private sealed class StubGateway(GatewayMode mode, TimeSpan delay = default) : IPaymentGateway
    {
        public async Task<string> ChargeAsync(Guid orderId, Money amount, CancellationToken cancellationToken)
        {
            if (mode == GatewayMode.Slow)
                await Task.Delay(delay, cancellationToken); // honours the token so Polly's timeout can cancel
            if (mode == GatewayMode.Decline)
                throw new PaymentDeclinedException("declined (test)");
            return "STUBREF-0000000001";
        }
    }
}
