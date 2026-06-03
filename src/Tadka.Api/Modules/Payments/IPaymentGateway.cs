using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Modules.Payments;

/// <summary>
/// The seam to the external payment provider (Razorpay/Stripe-class) — the first dependency Tadka
/// does not own. Every call to it goes through the Polly pipeline (ADR-021). The cancellation token
/// is the one Polly's timeout drives, so a slow gateway is actually cancelled at the deadline.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Charges the customer. Returns a gateway reference on success; throws on decline.</summary>
    Task<string> ChargeAsync(Guid orderId, Money amount, CancellationToken cancellationToken);
}

/// <summary>The gateway rejected the charge (insufficient funds, fraud hold, etc.).</summary>
public sealed class PaymentDeclinedException(string message) : Exception(message);
