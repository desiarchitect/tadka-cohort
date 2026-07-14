using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Delivery.Api.Data;
using Tadka.Delivery.Api.Messaging;

namespace Tadka.Delivery.Api.Tests;

/// <summary>
/// Real-broker integration tests for <see cref="OrderConfirmedConsumer"/>'s poison-message/DLQ path
/// (ADR-051). Unlike its Ordering/Payment/Restaurant siblings, this consumer already published directly
/// to Kafka (no Outbox — ADR-034's polyglot design), so it only needed the poison-handling half of the
/// review fix, not a trace-propagation fix.
/// </summary>
public class OrderConfirmedConsumerKafkaTests : IClassFixture<DeliveryKafkaApiFactory>, IAsyncLifetime
{
    private readonly DeliveryKafkaApiFactory _factory;
    private IProducer<string, string>? _producer;
    private IConsumer<string, string>? _dlqConsumer;

    public OrderConfirmedConsumerKafkaTests(DeliveryKafkaApiFactory factory) => _factory = factory;

    public Task InitializeAsync()
    {
        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _factory.BootstrapServers, Acks = Acks.All }).Build();

        _dlqConsumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _factory.BootstrapServers,
            GroupId = $"dlq-test-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        _dlqConsumer.Subscribe(Topics.OrderConfirmedDlq);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _producer?.Dispose();
        _dlqConsumer?.Close();
        _dlqConsumer?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_valid_order_confirmed_message_gets_a_rider_assigned_and_never_reaches_the_DLQ()
    {
        var orderId = Guid.NewGuid();
        var message = new OrderConfirmedMessage(Guid.NewGuid(), orderId, 12.9716, 77.5946, Guid.NewGuid());

        await _producer!.ProduceAsync(Topics.OrderConfirmed,
            new Message<string, string> { Key = orderId.ToString(), Value = JsonSerializer.Serialize(message) });

        var assignment = await PollUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
            return await db.Assignments.SingleOrDefaultAsync(a => a.OrderId == orderId);
        }, found => found is not null, timeoutSeconds: 20);

        Assert.NotNull(assignment);
    }

    [Fact]
    public async Task A_poison_message_is_retried_then_quarantined_to_the_DLQ_and_the_partition_unblocks()
    {
        const string poisonPayload = "{ this is not valid json ";
        var poisonKey = Guid.NewGuid().ToString();

        await _producer!.ProduceAsync(Topics.OrderConfirmed,
            new Message<string, string> { Key = poisonKey, Value = poisonPayload });

        var healthyOrderId = Guid.NewGuid();
        var healthyMessage = new OrderConfirmedMessage(Guid.NewGuid(), healthyOrderId, 12.9716, 77.5946, Guid.NewGuid());
        await _producer!.ProduceAsync(Topics.OrderConfirmed,
            new Message<string, string> { Key = healthyOrderId.ToString(), Value = JsonSerializer.Serialize(healthyMessage) });

        var dlqMessage = PollConsumerUntil(_dlqConsumer!, cr => cr.Message.Key == poisonKey, timeoutSeconds: 25);
        Assert.NotNull(dlqMessage);

        var dlq = JsonSerializer.Deserialize<DlqMessage>(dlqMessage!.Message.Value);
        Assert.NotNull(dlq);
        Assert.Equal(Topics.OrderConfirmed, dlq!.OriginalTopic);
        Assert.Equal(poisonPayload, dlq.OriginalPayload);

        var healthyAssignment = await PollUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
            return await db.Assignments.SingleOrDefaultAsync(a => a.OrderId == healthyOrderId);
        }, found => found is not null, timeoutSeconds: 20);

        Assert.NotNull(healthyAssignment);
    }

    private static async Task<T?> PollUntilAsync<T>(Func<Task<T?>> fetch, Func<T?, bool> isDone, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var result = await fetch();
            if (isDone(result)) return result;
            await Task.Delay(500);
        }
        return default;
    }

    private static ConsumeResult<string, string>? PollConsumerUntil(
        IConsumer<string, string> consumer, Func<ConsumeResult<string, string>, bool> match, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is not null && match(cr)) return cr;
            }
            catch (ConsumeException ex) when (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
            {
                // The DLQ topic doesn't exist until the app first publishes to it (after MaxAttempts
                // retries, a few seconds in) — this consumer subscribed before that. Keep polling.
                Thread.Sleep(500);
            }
        }
        return null;
    }
}
