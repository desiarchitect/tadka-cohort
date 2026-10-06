using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Tadka.Telemetry;

namespace Tadka.Delivery.Api.Messaging;

/// <summary>
/// Gives a rider back when an order is cancelled. In <c>Restaurant:DecisionMode=Service</c> the monolith publishes
/// <c>order-confirmed</c> to Restaurant and Delivery at the same moment, so Delivery assigns a rider BEFORE the
/// restaurant's reject arrives. The reject runs the refund saga (cancel, refund-requested, Payment refunds,
/// <c>payment-refunded</c>); this consumer listens for that last event and calls
/// <see cref="DeliveryService.CancelOrderAsync"/>. The work is naturally idempotent (no Inbox needed), and a
/// message that keeps failing is quarantined to a DLQ after 3 attempts (ADR-051), same as its sibling.
/// </summary>
public sealed class PaymentRefundedConsumer(
    IServiceScopeFactory scopeFactory,
    KafkaProducer producer,
    IOptions<KafkaOptions> options,
    ILogger<PaymentRefundedConsumer> logger) : BackgroundService
{
    private readonly PoisonMessageTracker _poison = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = options.Value.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }.ApplySasl(options.Value);

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topics.PaymentRefunded);
        logger.LogInformation("PaymentRefundedConsumer subscribed to {Topic}.", Topics.PaymentRefunded);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? cr = null;
            Activity? activity = null;
            try
            {
                cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;
                activity = TadkaDiagnostics.ActivitySource.StartActivity(   // rejoin the trace (ADR-041)
                    $"consume {Topics.PaymentRefunded}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));
                await HandleAsync(cr.Message.Value, stoppingToken);
                consumer.Commit(cr);
                _poison.Clear(cr.TopicPartitionOffset);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                logger.LogError(ex, "PaymentRefundedConsumer consume error.");
            }
            catch (Exception ex) when (cr is not null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                await HandlePoisonAsync(consumer, cr, ex, stoppingToken);
            }
            finally
            {
                activity?.Dispose();
            }
        }

        consumer.Close();
    }, stoppingToken);

    private static string? ReadTraceParent(ConsumeResult<string, string> cr) =>
        cr.Message.Headers is not null && cr.Message.Headers.TryGetLastBytes(TadkaTrace.TraceParentHeader, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;

    private async Task HandlePoisonAsync(IConsumer<string, string> consumer, ConsumeResult<string, string> cr, Exception ex, CancellationToken ct)
    {
        if (!_poison.RecordFailureAndShouldDlq(cr.TopicPartitionOffset))
        {
            logger.LogWarning(ex, "payment-refunded at {Offset} failed — will retry (idempotent).", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); } catch (OperationCanceledException) { }
            return;
        }

        logger.LogError(ex, "payment-refunded at {Offset} failed {Attempts}x — routing to DLQ, partition unblocked.", cr.TopicPartitionOffset, _poison.MaxAttempts);
        try
        {
            await producer.PublishAsync(Topics.PaymentRefundedDlq, cr.Message.Key,
                new DlqMessage(Topics.PaymentRefunded, cr.Message.Value, ex.Message, _poison.MaxAttempts, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception dlqEx) when (!ct.IsCancellationRequested)
        {
            logger.LogError(dlqEx, "Could not publish payment-refunded at {Offset} to the DLQ — not committing; will retry.", cr.TopicPartitionOffset);
            consumer.Seek(cr.TopicPartitionOffset);
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }
            return;
        }

        consumer.Commit(cr);
        _poison.Clear(cr.TopicPartitionOffset);
    }

    private async Task HandleAsync(string value, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<PaymentRefundedMessage>(value);
        if (msg is null) return;

        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DeliveryService>().CancelOrderAsync(msg.OrderId, ct);
    }
}
