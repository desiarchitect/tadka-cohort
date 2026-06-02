namespace Tadka.Api.Domain.Common;

/// <summary>
/// Dispatches domain events to their handlers. Called by the controller AFTER SaveChanges:
/// a failed notification must never roll back a committed state transition (that decoupling
/// is the whole point — see ADR-013).
/// </summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken cancellationToken = default);
}
