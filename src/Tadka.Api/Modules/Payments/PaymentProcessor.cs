namespace Tadka.Api.Modules.Payments;

/// <summary>
/// The background worker that drains <see cref="PaymentWorkChannel"/> and charges each order off the
/// request path (ADR-023). Because it's a <see cref="BackgroundService"/> (a singleton), it opens a
/// fresh DI scope per item to resolve the scoped <see cref="PaymentService"/> (and its DbContext).
/// A failed charge never crashes the loop — it's logged and the next item proceeds.
/// </summary>
public sealed class PaymentProcessor(
    PaymentWorkChannel channel,
    IServiceScopeFactory scopeFactory,
    ILogger<PaymentProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PaymentProcessor started — draining the payment queue.");

        await foreach (var item in channel.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var paymentService = scope.ServiceProvider.GetRequiredService<PaymentService>();
                await paymentService.ProcessAsync(item.OrderId, item.Amount, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down
            }
            catch (Exception ex)
            {
                // A poison item must not take down the worker. (Week 5: a DLQ owns this properly.)
                logger.LogError(ex, "PaymentProcessor failed to process order {OrderId}.", item.OrderId);
            }
        }
    }
}
