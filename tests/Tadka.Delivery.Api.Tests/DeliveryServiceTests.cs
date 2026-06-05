using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Delivery.Api.Data;
using Tadka.Delivery.Api.Domain;

namespace Tadka.Delivery.Api.Tests;

public class DeliveryServiceTests(DeliveryApiFactory factory) : IClassFixture<DeliveryApiFactory>
{
    private readonly DeliveryApiFactory _factory = factory;

    [Fact]
    public async Task Assign_picks_an_available_rider_and_marks_them_on_delivery()
    {
        var orderId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<Tadka.Delivery.Api.DeliveryService>();

        var result = await svc.AssignAsync(orderId);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.AgentName));

        var db = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
        Assert.Equal(1, await db.Assignments.CountAsync(a => a.OrderId == orderId));
        Assert.Equal(AgentStatus.OnDelivery, (await db.Agents.SingleAsync(a => a.Id == result.AgentId)).Status);
    }

    [Fact]
    public async Task Assigning_the_same_order_twice_creates_one_assignment()
    {
        var orderId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<Tadka.Delivery.Api.DeliveryService>();

        var first = await svc.AssignAsync(orderId);
        var second = await svc.AssignAsync(orderId); // redelivery

        Assert.Equal(first!.AgentId, second!.AgentId);
        var db = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
        Assert.Equal(1, await db.Assignments.CountAsync(a => a.OrderId == orderId)); // idempotent
    }

    [Fact]
    public async Task Track_without_a_token_is_rejected_401()  // per-service validation (ADR-031)
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/deliveries/{Guid.NewGuid()}/track");
        req.Headers.Add("X-Test-NoAuth", "true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(req)).StatusCode);
    }
}
