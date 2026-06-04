using MediatR;
using Tadka.Api.Domain.Common.Events;

namespace Tadka.Api.Modules.Payments;

/// <summary>
/// The background worker that drains <see cref="PaymentWorkChannel"/> and settles each order off the
/// request path (ADR-023). On Day 8 the charge moved out-of-process: instead of an in-proc PaymentService,
/// it now calls the extracted Payment service via <see cref="IPaymentClient"/> over HTTP (ADR-024/025),
/// then publishes the shared <see cref="PaymentCompletedEvent"/>/<see cref="PaymentFailedEvent"/> so
/// Ordering reacts. A BackgroundService is a singleton, so it opens a scope per item for the scoped
/// HttpClient + mediator. The async hand-off (POST /orders → ms) is unchanged.
/// </summary>
public sealed class PaymentProcessor(
    PaymentWorkChannel channel,
    IServiceScopeFactory scopeFactory,
    ILogger<PaymentProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PaymentProcessor started — draining the payment queue (→ Payment service over HTTP).");

        await foreach (var item in channel.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var client = scope.ServiceProvider.GetRequiredService<IPaymentClient>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                var result = await client.ChargeAsync(item.OrderId, item.Amount, stoppingToken);

                if (result is null)
                {
                    // Payment service unreachable / timed out → the charge is lost and the order stays
                    // pending. The visible gap that earns Kafka + Outbox on Day 9 (ADR-025).
                    logger.LogWarning("Order {OrderId} left PENDING — Payment service did not settle it.", item.OrderId);
                    continue;
                }

                if (string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                    await mediator.Publish(new PaymentCompletedEvent(item.OrderId, result.GatewayReference ?? ""), stoppingToken);
                else
                    await mediator.Publish(new PaymentFailedEvent(item.OrderId, result.FailureReason ?? "Payment failed"), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down
            }
            catch (Exception ex)
            {
                // A poison item must not take down the worker. (Week 5: a DLQ owns this properly.)
                logger.LogError(ex, "PaymentProcessor failed to settle order {OrderId}.", item.OrderId);
            }
        }
    }
}
