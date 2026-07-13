using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Tadka.Telemetry;

namespace Tadka.Restaurant.Api.Messaging;

/// <summary>Kafka topic names — the cross-service event contract (ADR-027). Each service owns its own copy.</summary>
public static class Topics
{
    // Published whenever a restaurant or its menu changes. Carries the FULL current snapshot
    // (event-carried state transfer, ADR-037) so the consumer never has to call back (ADR-008).
    public const string MenuUpdated = "menu-updated";
    public const string OrderConfirmed = "order-confirmed";       // consumed
    public const string RestaurantResponse = "restaurant-response"; // published
}

/// <summary>Consumed from Ordering when payment settled and order confirmed.
/// <see cref="RestaurantId"/> is additive (ADR-050) — use for per-restaurant decisions when present.</summary>
public sealed record OrderConfirmedMessage(
    Guid MessageId, Guid OrderId, double Latitude, double Longitude, Guid RestaurantId = default);

/// <summary>Restaurant accept/reject decision for the order saga (ADR-062).</summary>
public sealed record RestaurantResponseMessage(
    Guid MessageId, Guid OrderId, string Status, string? Reason, string? GatewayReference);

/// <summary>One menu item, as carried in the snapshot.</summary>
public sealed record MenuItemSnapshot(
    Guid MenuItemId, string Name, decimal PriceAmount, string PriceCurrency, bool IsAvailable, string Category, bool IsVeg);

public sealed record AddressSnapshot(
    string Line1, string Line2, string City, string Pincode, double Latitude, double Longitude);

/// <summary>
/// The full state of a restaurant + its menu at the moment of a change (ADR-037, event-carried state
/// transfer). The monolith upserts its local price replica from this — no back-call needed (ADR-008).
/// </summary>
public sealed record RestaurantSnapshotMessage(
    Guid MessageId, Guid RestaurantId, string Name, bool IsActive, AddressSnapshot Address, List<MenuItemSnapshot> Menu);

/// <summary>Kafka config (ADR-027). Empty BootstrapServers ⇒ Kafka OFF (the relay doesn't start), so
/// single-process dev and the test suite run unchanged.</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";
    public string? BootstrapServers { get; set; }
    public string ConsumerGroup { get; set; } = "tadka-restaurant";
    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
}

/// <summary>Thin singleton Kafka producer for <c>menu-updated</c> (ADR-027). Hand-rolled to show the
/// mechanics; production uses MassTransit / NServiceBus + the outbox.</summary>
public sealed class KafkaProducer : IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaProducer(IOptions<KafkaOptions> options)
        => _producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = options.Value.BootstrapServers, Acks = Acks.All }).Build();

    public Task PublishRawAsync(string topic, string key, string value, string? traceParent = null, CancellationToken ct = default)
    {
        var message = new Message<string, string> { Key = key, Value = value };
        if (!string.IsNullOrEmpty(traceParent))   // carry the trace across Kafka (ADR-041)
            message.Headers = new Headers { { TadkaTrace.TraceParentHeader, Encoding.UTF8.GetBytes(traceParent) } };
        return _producer.ProduceAsync(topic, message, ct);
    }

    public void Dispose() => _producer.Dispose();
}
