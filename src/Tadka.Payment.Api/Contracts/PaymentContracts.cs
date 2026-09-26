namespace Tadka.Payment.Api.Contracts;

/// <summary>
/// The HTTP contract — the Payment service's product surface (ADR-025). The monolith has its OWN copy of
/// these shapes (no shared code across services); this is the boundary that becomes a Kafka message on Day 9.
/// </summary>
/// <summary><see cref="CardNumber"/> is the raw PAN as the client would send it at checkout — it is
/// tokenized (ADR-046) inside <c>PaymentService.ChargeAsync</c> before anything else touches it, and
/// this field itself is never logged or persisted. Optional: a charge can arrive without one (e.g. a
/// saved/tokenized payment method chosen client-side). <see cref="CustomerId"/> is optional too: the real
/// flow tags it automatically from the <c>order-placed</c> Kafka event (ADR-031); this HTTP endpoint is
/// now Admin-only (a support/ops re-charge), so a caller who happens to know which customer the order
/// belongs to can still leave the resulting payment row readable by that customer afterwards.</summary>
public sealed record ChargeRequest(Guid OrderId, decimal Amount, string? Currency, string? CardNumber = null, Guid? CustomerId = null);

public sealed record ChargeResponse(Guid OrderId, string Status, string? GatewayReference, string? FailureReason);
