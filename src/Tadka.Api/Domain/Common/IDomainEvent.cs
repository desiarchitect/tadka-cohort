namespace Tadka.Api.Domain.Common;

/// <summary>
/// Marker for something that has happened in the domain (past tense): OrderPlaced, OrderConfirmed.
/// In the monolith these are dispatched in-process, synchronously, AFTER the state is persisted.
/// At service extraction (Week 5) the same events become messages on a broker (Kafka) — the
/// shape stays, the transport changes. See ADR-013.
/// </summary>
public interface IDomainEvent
{
    DateTime OccurredAt { get; }
}
