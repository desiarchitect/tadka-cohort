using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Delivery.Api.Data;
using Tadka.Delivery.Api.Domain;

namespace Tadka.Delivery.Api.Tests;

/// <summary>
/// The "no rider free" path. Before this fix, a 4th order on a fresh database (3 seeded riders, none ever
/// released) logged "left unassigned", the consumer marked the Kafka message done, and the order never got a
/// rider. Now it is parked durably and assigned as soon as a rider is released.
/// This class owns its own Postgres (its own 3 riders), so it can deliberately use all of them.
/// </summary>
public class PendingAssignmentTests(DeliveryApiFactory factory) : IClassFixture<DeliveryApiFactory>
{
    private readonly DeliveryApiFactory _factory = factory;

    [Fact]
    public async Task An_order_that_finds_no_free_rider_is_parked_then_assigned_when_a_rider_is_released()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DeliveryService>();
        var db = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();

        // Occupy all three riders.
        var busy = new List<AssignmentResult>();
        for (var i = 0; i < 3; i++)
            busy.Add((await svc.AssignAsync(Guid.NewGuid(), customerId: Guid.NewGuid()))!);
        Assert.Equal(3, busy.Select(b => b.AgentId).Distinct().Count());

        // The 4th order: no rider free → parked, not dropped.
        var waitingOrder = Guid.NewGuid();
        var waitingCustomer = Guid.NewGuid();
        Assert.Null(await svc.AssignAsync(waitingOrder, customerId: waitingCustomer, latitude: 12.93, longitude: 77.61));
        var parked = await db.PendingAssignments.AsNoTracking().SingleAsync(p => p.OrderId == waitingOrder);
        Assert.Equal(waitingCustomer, parked.CustomerId);
        Assert.False(await db.Assignments.AnyAsync(a => a.OrderId == waitingOrder));

        // A sweep while everyone is still busy changes nothing.
        Assert.Empty(await svc.RetryPendingAsync(10));

        // The first rider finishes their delivery (through the real endpoint, as that rider) → released.
        var first = busy[0];
        var riderUser = (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == first.AgentId)).UserId!.Value;
        var client = _factory.CreateClient();
        foreach (var status in new[] { "PickedUp", "Delivered" })
        {
            var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/deliveries/{first.OrderId}/status")
            { Content = JsonContent.Create(new { status }) };
            req.Headers.Add("X-Test-Auth", $"DeliveryAgent:{riderUser}");
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(req)).StatusCode);
        }
        Assert.Equal(AgentStatus.Available, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == first.AgentId)).Status);

        // The next sweep gives the waiting order that rider, keeps the customer, and clears the parked row.
        var assigned = Assert.Single(await svc.RetryPendingAsync(10));
        Assert.Equal(waitingOrder, assigned.OrderId);
        Assert.Equal(first.AgentId, assigned.AgentId);
        var assignment = await db.Assignments.AsNoTracking().SingleAsync(a => a.OrderId == waitingOrder);
        Assert.Equal(waitingCustomer, assignment.CustomerId);
        Assert.False(await db.PendingAssignments.AnyAsync(p => p.OrderId == waitingOrder));
    }
}

/// <summary>Status transitions and who may make them. Own Postgres, uses 2 of the 3 riders.</summary>
public class DeliveryStatusTests(DeliveryApiFactory factory) : IClassFixture<DeliveryApiFactory>
{
    private readonly DeliveryApiFactory _factory = factory;

    private async Task<(Guid OrderId, Guid Customer, Guid RiderUser, Guid AgentId)> AssignAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var customer = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var r = (await scope.ServiceProvider.GetRequiredService<DeliveryService>().AssignAsync(orderId, customerId: customer))!;
        var agent = await scope.ServiceProvider.GetRequiredService<DeliveryDbContext>().Agents.AsNoTracking().SingleAsync(a => a.Id == r.AgentId);
        return (orderId, customer, agent.UserId!.Value, agent.Id);
    }

    private static HttpRequestMessage Patch(Guid orderId, string status, string testAuth)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/deliveries/{orderId}/status") { Content = JsonContent.Create(new { status }) };
        req.Headers.Add("X-Test-Auth", testAuth);
        return req;
    }

    [Fact]
    public async Task Skipping_pickup_is_rejected_and_the_rider_stays_busy()
    {
        var (orderId, _, riderUser, agentId) = await AssignAsync();
        var client = _factory.CreateClient();

        var res = await client.SendAsync(Patch(orderId, "Delivered", $"DeliveryAgent:{riderUser}"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var agent = await scope.ServiceProvider.GetRequiredService<DeliveryDbContext>().Agents.AsNoTracking().SingleAsync(a => a.Id == agentId);
        Assert.Equal(AgentStatus.OnDelivery, agent.Status);
    }

    [Fact]
    public async Task The_customer_or_a_different_rider_cannot_change_the_delivery_status()
    {
        var (orderId, customer, _, _) = await AssignAsync();
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Patch(orderId, "Cancelled", $"Customer:{customer}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Patch(orderId, "Cancelled", $"DeliveryAgent:{Guid.NewGuid()}"))).StatusCode);
    }
}

/// <summary>
/// One rider, one order at a time. The one-assignment-per-order unique index never protected the RIDER:
/// two concurrent assignments could both read "Lakshmi is Available" and both take her. The claim is now an
/// atomic UPDATE ... WHERE Status = 'Available' (and the new sweeper makes concurrent assignment routine).
/// </summary>
public class ConcurrentAssignmentTests(DeliveryApiFactory factory) : IClassFixture<DeliveryApiFactory>
{
    private readonly DeliveryApiFactory _factory = factory;

    [Fact]
    public async Task Concurrent_assignments_never_give_one_rider_two_orders()
    {
        // 5 orders race for 3 riders, each through its own scope (its own DbContext and connection).
        var orders = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var results = await Task.WhenAll(orders.Select(async orderId =>
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<DeliveryService>().AssignAsync(orderId, customerId: Guid.NewGuid());
        }));

        var assigned = results.Where(r => r is not null).Select(r => r!).ToList();
        Assert.Equal(3, assigned.Count);                                   // exactly the riders that exist
        Assert.Equal(3, assigned.Select(a => a.AgentId).Distinct().Count()); // and never the same rider twice

        using var check = _factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<DeliveryDbContext>();
        Assert.Equal(2, await db.PendingAssignments.CountAsync(p => orders.Contains(p.OrderId))); // the other two wait
    }
}
