namespace Tadka.Payment.Api.Contracts;

/// <summary>
/// The HTTP contract — the Payment service's product surface (ADR-025). The monolith has its OWN copy of
/// these shapes (no shared code across services); this is the boundary that becomes a Kafka message on Day 9.
/// </summary>
public sealed record ChargeRequest(Guid OrderId, decimal Amount, string? Currency);

public sealed record ChargeResponse(Guid OrderId, string Status, string? GatewayReference, string? FailureReason);
