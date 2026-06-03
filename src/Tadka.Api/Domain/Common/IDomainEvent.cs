using MediatR;

namespace Tadka.Api.Domain.Common;

/// <summary>
/// Marker for something that has happened in the domain (past tense): OrderPlaced, OrderConfirmed.
/// In the monolith these are dispatched in-process, synchronously, AFTER the state is persisted.
/// At service extraction (Week 5) the same events become messages on a broker (Kafka) — the
/// shape stays, the transport changes. See ADR-013.
///
/// Day 7 (ADR-022): a domain event IS a MediatR <see cref="INotification"/> — we publish it via
/// <c>IMediator.Publish(...)</c> after commit and let MediatR fan out to every
/// <c>INotificationHandler&lt;T&gt;</c>. The hand-rolled dispatcher is gone; the seam is unchanged.
/// </summary>
public interface IDomainEvent : INotification
{
    DateTime OccurredAt { get; }
}
