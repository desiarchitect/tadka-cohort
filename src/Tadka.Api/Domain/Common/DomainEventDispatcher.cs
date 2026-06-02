namespace Tadka.Api.Domain.Common;

/// <summary>
/// In-process, synchronous dispatcher. Resolves every <see cref="IDomainEventHandler{TEvent}"/>
/// registered for the event's runtime type and invokes them in turn.
/// Monolith-phase only: synchronous + in-memory. Week 5 swaps this for a broker producer.
/// </summary>
public class DomainEventDispatcher(IServiceProvider serviceProvider) : IDomainEventDispatcher
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public async Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken cancellationToken = default)
    {
        foreach (var domainEvent in events)
        {
            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());
            var handlers = _serviceProvider.GetServices(handlerType);

            foreach (var handler in handlers)
            {
                if (handler is null) continue;
                var method = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!;
                await (Task)method.Invoke(handler, [domainEvent, cancellationToken])!;
            }
        }
    }
}
