namespace Tadka.Api.Domain.Common;

/// <summary>
/// Reacts to a domain event. Many handlers can subscribe to one event (fan-out) — exactly the
/// shape that becomes independent Kafka consumers after extraction.
/// </summary>
public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}
