using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Delivery.Api.Data;
using Tadka.Delivery.Api.Domain;
using Tadka.Delivery.Api.Messaging;

namespace Tadka.Delivery.Api.Tests;

/// <summary>
/// An order the restaurant rejected must give its rider back. In Service decision mode Delivery assigns a rider
/// when <c>order-confirmed</c> arrives, before the reject; nothing used to release that rider, so three rejected
/// orders left every rider busy. These tests drive <see cref="DeliveryService.CancelOrderAsync"/> directly.
/// The class owns its own Postgres (its own 3 riders).
/// </summary>
public class CancelledOrderTests(DeliveryApiFactory factory) : IClassFixture<DeliveryApiFactory>
{
    private readonly DeliveryApiFactory _factory = factory;

    private (IServiceScope Scope, DeliveryService Svc, DeliveryDbContext Db) Open()
    {
        var scope = _factory.Services.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<DeliveryService>(), scope.ServiceProvider.GetRequiredService<DeliveryDbContext>());
    }

    [Fact]
    public async Task A_cancelled_order_releases_its_rider_and_marks_the_delivery_Cancelled()
    {
        var (scope, svc, db) = Open();
        using var _ = scope;
        await ResetAsync(db);

        var order = Guid.NewGuid();
        var assigned = (await svc.AssignAsync(order, customerId: Guid.NewGuid()))!;
        Assert.Equal(AgentStatus.OnDelivery, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == assigned.AgentId)).Status);

        await svc.CancelOrderAsync(order);

        Assert.Equal(AgentStatus.Available, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == assigned.AgentId)).Status);
        Assert.Equal(AssignmentStatus.Cancelled, (await db.Assignments.AsNoTracking().SingleAsync(a => a.OrderId == order)).Status);
    }

    [Fact]
    public async Task Three_rejected_orders_do_not_use_up_the_riders()
    {
        var (scope, svc, db) = Open();
        using var _ = scope;
        await ResetAsync(db);

        for (var i = 0; i < 6; i++) // twice the fleet: only possible if each cancellation gives its rider back
        {
            var order = Guid.NewGuid();
            Assert.NotNull(await svc.AssignAsync(order, customerId: Guid.NewGuid()));
            await svc.CancelOrderAsync(order);
        }

        Assert.Equal(3, await db.Agents.CountAsync(a => a.Status == AgentStatus.Available));
        Assert.Empty(await db.PendingAssignments.ToListAsync());
    }

    [Fact]
    public async Task A_cancelled_order_that_was_still_waiting_for_a_rider_is_dropped_from_the_waiting_list()
    {
        var (scope, svc, db) = Open();
        using var _ = scope;
        await ResetAsync(db);

        var busy = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var o = Guid.NewGuid();
            busy.Add(o);
            await svc.AssignAsync(o, customerId: Guid.NewGuid());
        }
        var waiting = Guid.NewGuid();
        Assert.Null(await svc.AssignAsync(waiting, customerId: Guid.NewGuid()));
        Assert.True(await db.PendingAssignments.AnyAsync(p => p.OrderId == waiting));

        await svc.CancelOrderAsync(waiting);
        await svc.CancelOrderAsync(busy[0]); // frees one rider; the cancelled order must not take it

        Assert.False(await db.PendingAssignments.AnyAsync(p => p.OrderId == waiting));
        Assert.Empty(await svc.RetryPendingAsync(10));
        Assert.False(await db.Assignments.AnyAsync(a => a.OrderId == waiting));
        Assert.Equal(1, await db.Agents.CountAsync(a => a.Status == AgentStatus.Available));
    }

    [Fact]
    public async Task If_the_cancellation_arrives_before_order_confirmed_the_order_never_gets_a_rider()
    {
        var (scope, svc, db) = Open();
        using var _ = scope;
        await ResetAsync(db);

        var order = Guid.NewGuid();
        await svc.CancelOrderAsync(order);            // payment-refunded read first (Delivery was catching up)
        Assert.Null(await svc.AssignAsync(order));    // order-confirmed read second

        Assert.False(await db.Assignments.AnyAsync(a => a.OrderId == order));
        Assert.False(await db.PendingAssignments.AnyAsync(p => p.OrderId == order));
        Assert.Equal(3, await db.Agents.CountAsync(a => a.Status == AgentStatus.Available));
    }

    [Fact]
    public async Task Cancelling_twice_or_cancelling_a_delivered_order_changes_nothing()
    {
        var (scope, svc, db) = Open();
        using var _ = scope;
        await ResetAsync(db);

        var order = Guid.NewGuid();
        await svc.AssignAsync(order);
        await svc.ChangeStatusAsync(order, AssignmentStatus.PickedUp);
        await svc.ChangeStatusAsync(order, AssignmentStatus.Delivered);
        var other = (await svc.AssignAsync(Guid.NewGuid()))!;   // the freed rider (or another) takes a new order

        await svc.CancelOrderAsync(order);
        await svc.CancelOrderAsync(order);

        Assert.Equal(AssignmentStatus.Delivered, (await db.Assignments.AsNoTracking().SingleAsync(a => a.OrderId == order)).Status);
        Assert.Equal(AgentStatus.OnDelivery, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == other.AgentId)).Status);
        Assert.Equal(1, await db.CancelledOrders.CountAsync(c => c.OrderId == order));

    }

    private static async Task ResetAsync(DeliveryDbContext db)
    {
        // Each test starts from "three free riders, nothing assigned or waiting" in the class's shared database.
        await db.Assignments.ExecuteDeleteAsync();
        await db.PendingAssignments.ExecuteDeleteAsync();
        await db.CancelledOrders.ExecuteDeleteAsync();
        await db.Agents.ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentStatus.Available));
    }
}

/// <summary>Real-broker test: a <c>payment-refunded</c> message on Kafka frees the rider.</summary>
public class PaymentRefundedConsumerKafkaTests(DeliveryKafkaApiFactory factory) : IClassFixture<DeliveryKafkaApiFactory>
{
    private readonly DeliveryKafkaApiFactory _factory = factory;

    [Fact]
    public async Task A_payment_refunded_message_releases_the_rider_of_that_order()
    {
        var order = Guid.NewGuid();
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _factory.BootstrapServers, Acks = Acks.All }).Build();

        await producer.ProduceAsync(Topics.OrderConfirmed, new Message<string, string>
        {
            Key = order.ToString(),
            Value = JsonSerializer.Serialize(new OrderConfirmedMessage(Guid.NewGuid(), order, 12.97, 77.59, Guid.NewGuid()))
        });
        var agentId = (await PollAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<DeliveryDbContext>().Assignments.AsNoTracking()
                .SingleOrDefaultAsync(a => a.OrderId == order))?.AgentId;
        }, id => id is not null))!.Value;

        await producer.ProduceAsync(Topics.PaymentRefunded, new Message<string, string>
        {
            Key = order.ToString(),
            Value = JsonSerializer.Serialize(new PaymentRefundedMessage(Guid.NewGuid(), order, "Refunded"))
        });

        var released = await PollAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<DeliveryDbContext>().Agents.AsNoTracking()
                .SingleAsync(a => a.Id == agentId)).Status;
        }, status => status == AgentStatus.Available);

        Assert.Equal(AgentStatus.Available, released);
    }

    private static async Task<T?> PollAsync<T>(Func<Task<T?>> fetch, Func<T?, bool> isDone)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            var result = await fetch();
            if (isDone(result)) return result;
            await Task.Delay(500);
        }
        return default;
    }
}
