using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Restaurant.Api.Data;
using Tadka.Restaurant.Api.Messaging;

namespace Tadka.Restaurant.Api.Tests;

/// <summary>
/// Real-broker integration tests for <see cref="OrderConfirmedConsumer"/>'s poison-message/DLQ path
/// (ADR-051). The rest of the suite runs with Kafka off (deterministic, fast); these two tests exist
/// specifically to lock in the fix that added retry-then-DLQ handling to this consumer, so a regression
/// here — e.g. someone "simplifying" the catch block back to a bare log-and-swallow — fails CI instead of
/// silently reintroducing the exact bug the review found.
/// </summary>
public class OrderConfirmedConsumerKafkaTests : IClassFixture<RestaurantKafkaApiFactory>, IAsyncLifetime
{
    private readonly RestaurantKafkaApiFactory _factory;
    private IProducer<string, string>? _producer;
    private IConsumer<string, string>? _dlqConsumer;

    public OrderConfirmedConsumerKafkaTests(RestaurantKafkaApiFactory factory) => _factory = factory;

    public Task InitializeAsync()
    {
        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _factory.BootstrapServers, Acks = Acks.All }).Build();

        _dlqConsumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _factory.BootstrapServers,
            GroupId = $"dlq-test-{Guid.NewGuid()}", // fresh group per test run — read from the start
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
    public async Task A_valid_order_confirmed_message_is_decided_and_never_reaches_the_DLQ()
    {
        var orderId = Guid.NewGuid();
        var message = new OrderConfirmedMessage(Guid.NewGuid(), orderId, 12.9716, 77.5946, Guid.NewGuid());

        await _producer!.ProduceAsync(Topics.OrderConfirmed,
            new Message<string, string> { Key = orderId.ToString(), Value = JsonSerializer.Serialize(message) });

        var decision = await PollUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
            return await db.OrderDecisions.SingleOrDefaultAsync(d => d.OrderId == orderId);
        }, found => found is not null, timeoutSeconds: 20);

        Assert.NotNull(decision);
    }

    [Fact]
    public async Task A_poison_message_is_retried_then_quarantined_to_the_DLQ_and_the_partition_unblocks()
    {
        // Not valid JSON at all — JsonSerializer.Deserialize throws, which is exactly the failure mode
        // OrderConfirmedConsumer's HandlePoisonAsync exists to catch (ADR-051).
        const string poisonPayload = "{ this is not valid json ";
        var poisonKey = Guid.NewGuid().ToString();

        await _producer!.ProduceAsync(Topics.OrderConfirmed,
            new Message<string, string> { Key = poisonKey, Value = poisonPayload });

        // A healthy message right behind it on the same partition — proves the partition unblocks
        // after quarantine instead of the poison message wedging every message behind it forever.
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
        Assert.True(dlq.Attempts >= 1);

        // The healthy message that queued up behind the poison one must still get processed —
        // this is the "partition unblocked" half of the fix, not just "poison eventually quarantined".
        var healthyDecision = await PollUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
            return await db.OrderDecisions.SingleOrDefaultAsync(d => d.OrderId == healthyOrderId);
        }, found => found is not null, timeoutSeconds: 20);

        Assert.NotNull(healthyDecision);
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
