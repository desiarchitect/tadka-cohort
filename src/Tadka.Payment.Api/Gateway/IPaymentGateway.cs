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
}

/// <summary>The gateway rejected the charge (insufficient funds, fraud hold, etc.).</summary>
public sealed class PaymentDeclinedException(string message) : Exception(message);
