using System.Threading.Channels;
using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Modules.Payments;

/// <summary>An order waiting to be charged, off the request path.</summary>
public sealed record PaymentWorkItem(Guid OrderId, Money Amount);

/// <summary>
/// The in-process queue between order creation and payment (ADR-023). A bounded <see cref="Channel{T}"/>
/// — bounded so a payment backlog applies back-pressure instead of growing without limit. This is the
/// right-sized step today; its durable successor is Kafka + the Outbox pattern (Week 5), so a crash
/// can't lose a queued payment. Registered as a singleton: one queue, many writers, one reader.
/// </summary>
public sealed class PaymentWorkChannel
{
    private readonly Channel<PaymentWorkItem> _channel =
        Channel.CreateBounded<PaymentWorkItem>(new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    public ValueTask EnqueueAsync(PaymentWorkItem item, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(item, cancellationToken);

    public IAsyncEnumerable<PaymentWorkItem> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
