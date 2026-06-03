using MediatR;

namespace Tadka.Api.Domain.Common.Events;

/// <summary>
/// Cross-module event contracts (ADR-022). These live in the shared kernel, NOT inside either module:
/// the Payment module PUBLISHES them; the Ordering module REACTS to them. That way Ordering depends on
/// the contract, never on Payment — and Payment depends on the contract, never on Ordering. Exactly the
/// shape that becomes a Kafka topic at Week 5.
/// </summary>
public sealed record PaymentCompletedEvent(Guid OrderId, string GatewayReference) : INotification;

/// <summary>The payment for an order failed or timed out (ADR-021/023). Ordering reacts by cancelling.</summary>
public sealed record PaymentFailedEvent(Guid OrderId, string Reason) : INotification;
