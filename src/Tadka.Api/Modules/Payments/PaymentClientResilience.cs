using Microsoft.Extensions.Options;
using Polly;
using Polly.RateLimiting;

namespace Tadka.Api.Modules.Payments;

/// <summary>
/// The Day-7 Polly pipeline (timeout + bulkhead, ADR-021), REUSED around the HTTP call to the Payment
/// service (ADR-025). The resilience pattern travels unchanged from "in-process gateway call" to "network
/// hop": a slow Payment service fails fast (~2 s) instead of hanging the background processor, and no more
/// than N charges are in flight at once.
/// </summary>
public sealed class PaymentClientResilience
{
    public ResiliencePipeline Pipeline { get; }

    public PaymentClientResilience(IOptions<PaymentClientOptions> options)
    {
        var o = options.Value;

        Pipeline = new ResiliencePipelineBuilder()
            .AddConcurrencyLimiter(
                permitLimit: Math.Max(1, o.MaxConcurrentCharges),
                queueLimit: 0)
            .AddTimeout(TimeSpan.FromSeconds(o.TimeoutSeconds <= 0 ? 2 : o.TimeoutSeconds))
            .Build();
    }
}
