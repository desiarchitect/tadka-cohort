using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Payment.Api.Data;
using Tadka.Payment.Api.Domain;
using Tadka.Payment.Api.Messaging;

namespace Tadka.Payment.Api.Tests;

/// <summary>
/// Real-broker integration tests for <see cref="RefundRequestedConsumer"/> (ADR-045's compensation
/// consumer) — the single consumer this review found with BOTH bugs: an orphan trace span (ADR-041) and
/// missing poison-message/DLQ handling (ADR-051). These tests lock in the DLQ half; the trace fix isn't
/// independently observable from a unit/integration test (it only shows up as a parent/child span
/// relationship in Jaeger), so it's verified by re-running the Day-13 live trace demo instead.
/// </summary>
public class RefundRequestedConsumerKafkaTests : IClassFixture<PaymentKafkaApiFactory>, IAsyncLifetime
{
    private readonly PaymentKafkaApiFactory _factory;
    private IProducer<string, string>? _producer;
    private IConsumer<string, string>? _dlqConsumer;

    public RefundRequestedConsumerKafkaTests(PaymentKafkaApiFactory factory) => _factory = factory;

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
        _dlqConsumer.Subscribe(Topics.RefundRequestedDlq);

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
    public async Task A_refund_request_for_a_completed_payment_settles_and_never_reaches_the_DLQ()
    {
        var orderId = Guid.NewGuid();

        // Seed a completed charge first — RefundAsync needs an existing payment for this order.
        using (var scope = _factory.Services.CreateScope())
        {
            var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
            await payments.ChargeAsync(orderId, new Money(500m, "INR"));
        }

        var refundRequest = new RefundRequestedMessage(Guid.NewGuid(), orderId, GatewayReference: null);
        await _producer!.ProduceAsync(Topics.RefundRequested,
            new Message<string, string> { Key = orderId.ToString(), Value = JsonSerializer.Serialize(refundRequest) });

        var refunded = await PollUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            return await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.OrderId == orderId);
        }, p => p is not null && p.Status == PaymentStatus.Refunded, timeoutSeconds: 20);

        Assert.NotNull(refunded);
        Assert.Equal(PaymentStatus.Refunded, refunded!.Status);
    }

    [Fact]
    public async Task A_poison_refund_message_is_retried_then_quarantined_to_the_DLQ_and_the_partition_unblocks()
    {
        const string poisonPayload = "{ this is not valid json ";
        var poisonKey = Guid.NewGuid().ToString();

        await _producer!.ProduceAsync(Topics.RefundRequested,
            new Message<string, string> { Key = poisonKey, Value = poisonPayload });

        // A healthy refund request right behind it — proves the partition unblocks after quarantine.
        var healthyOrderId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var payments = scope.ServiceProvider.GetRequiredService<PaymentService>();
            await payments.ChargeAsync(healthyOrderId, new Money(250m, "INR"));
        }
        var healthyRequest = new RefundRequestedMessage(Guid.NewGuid(), healthyOrderId, GatewayReference: null);
        await _producer!.ProduceAsync(Topics.RefundRequested,
            new Message<string, string> { Key = healthyOrderId.ToString(), Value = JsonSerializer.Serialize(healthyRequest) });

        var dlqMessage = PollConsumerUntil(_dlqConsumer!, cr => cr.Message.Key == poisonKey, timeoutSeconds: 25);
        Assert.NotNull(dlqMessage);

        var dlq = JsonSerializer.Deserialize<DlqMessage>(dlqMessage!.Message.Value);
        Assert.NotNull(dlq);
        Assert.Equal(Topics.RefundRequested, dlq!.OriginalTopic);
        Assert.Equal(poisonPayload, dlq.OriginalPayload);

        var healthyRefunded = await PollUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            return await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.OrderId == healthyOrderId);
        }, p => p is not null && p.Status == PaymentStatus.Refunded, timeoutSeconds: 20);

        Assert.NotNull(healthyRefunded);
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
