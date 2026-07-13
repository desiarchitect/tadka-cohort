using Tadka.Payment.Api.Domain;

namespace Tadka.Payment.Api.Gateway;

/// <summary>
/// The seam to the external payment provider (Razorpay/Stripe-class) — the dependency this service owns.
/// Every call goes through the Polly pipeline (ADR-021). The cancellation token is the one Polly's timeout
/// drives, so a slow gateway is actually cancelled at the deadline.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Charges the customer. Returns a gateway reference on success; throws on decline.</summary>
    Task<string> ChargeAsync(Guid orderId, Money amount, CancellationToken cancellationToken);

    /// <summary>
    /// Refunds a previously completed charge (ADR-045 compensation). Returns a refund reference.
    /// Idempotent at the gateway layer in production; our Fake always succeeds for demo reliability.
    /// </summary>
    Task<string> RefundAsync(Guid orderId, string? originalGatewayReference, Money amount, CancellationToken cancellationToken);
}

/// <summary>The gateway rejected the charge (insufficient funds, fraud hold, etc.) — a BUSINESS outcome.
/// Final: never retried, never trips the circuit breaker (ADR-043). A decline is an answer, not an outage.</summary>
public sealed class PaymentDeclinedException(string message) : Exception(message);

/// <summary>The gateway itself is unreachable/erroring (5xx, connection refused) — a TRANSPORT failure.
/// Transient: eligible for retry, and counts toward the circuit breaker (ADR-043). Distinct from a decline.</summary>
public sealed class PaymentGatewayUnavailableException(string message) : Exception(message);
