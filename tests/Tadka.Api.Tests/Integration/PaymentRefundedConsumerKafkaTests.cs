using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Api.Data;
using Tadka.Api.Data.Messaging;
using Tadka.Api.Infrastructure.Messaging;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Real-broker integration tests for <see cref="PaymentRefundedConsumer"/> — the Ordering-side consumer
/// this review found with an orphan trace span (ADR-041) AND missing poison-message/DLQ handling
/// (ADR-051), both fixed here. The DLQ half is verified directly below; the trace fix is only observable
/// as a parent/child span relationship in Jaeger, so it's separately verified by re-running the Day-13
/// live trace demo on a rejected order.
/// </summary>
public class PaymentRefundedConsumerKafkaTests : IClassFixture<TadkaKafkaApiFactory>, IAsyncLifetime
{
    private readonly TadkaKafkaApiFactory _factory;
    private IProducer<string, string>? _producer;
    private IConsumer<string, string>? _dlqConsumer;

    public PaymentRefundedConsumerKafkaTests(TadkaKafkaApiFactory factory) => _factory = factory;

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
        _dlqConsumer.Subscribe(Topics.PaymentRefundedDlq);

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
    public async Task A_valid_payment_refunded_message_is_recorded_in_the_inbox_and_never_reaches_the_DLQ()
    {
        var messageId = Guid.NewGuid();
        var message = new PaymentRefundedMessage(messageId, Guid.NewGuid(), "Refunded");

        await _producer!.ProduceAsync(Topics.PaymentRefunded,
            new Message<string, string> { Key = message.OrderId.ToString(), Value = JsonSerializer.Serialize(message) });

        var processed = await PollUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();
            return await db.Set<InboxMessage>().AnyAsync(i => i.MessageId == messageId);
        }, found => found, timeoutSeconds: 20);

        Assert.True(processed);
    }

    [Fact]
    public async Task A_poison_message_is_retried_then_quarantined_to_the_DLQ_and_the_partition_unblocks()
    {
        const string poisonPayload = "{ this is not valid json ";
        var poisonKey = Guid.NewGuid().ToString();

        await _producer!.ProduceAsync(Topics.PaymentRefunded,
            new Message<string, string> { Key = poisonKey, Value = poisonPayload });

        var healthyMessageId = Guid.NewGuid();
        var healthyMessage = new PaymentRefundedMessage(healthyMessageId, Guid.NewGuid(), "Refunded");
        await _producer!.ProduceAsync(Topics.PaymentRefunded,
            new Message<string, string> { Key = healthyMessage.OrderId.ToString(), Value = JsonSerializer.Serialize(healthyMessage) });

        var dlqMessage = PollConsumerUntil(_dlqConsumer!, cr => cr.Message.Key == poisonKey, timeoutSeconds: 25);
        Assert.NotNull(dlqMessage);

        var dlq = JsonSerializer.Deserialize<DlqMessage>(dlqMessage!.Message.Value);
        Assert.NotNull(dlq);
        Assert.Equal(Topics.PaymentRefunded, dlq!.OriginalTopic);
        Assert.Equal(poisonPayload, dlq.OriginalPayload);

        var healthyProcessed = await PollUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();
            return await db.Set<InboxMessage>().AnyAsync(i => i.MessageId == healthyMessageId);
        }, found => found, timeoutSeconds: 20);

        Assert.True(healthyProcessed);
    }

    private static async Task<T> PollUntilAsync<T>(Func<Task<T>> fetch, Func<T, bool> isDone, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var last = default(T)!;
        while (DateTime.UtcNow < deadline)
        {
            last = await fetch();
            if (isDone(last)) return last;
            await Task.Delay(500);
        }
        return last;
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
