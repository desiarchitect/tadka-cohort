using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Tadka.Restaurant.Api.Messaging;

/// <summary>Kafka topic names — the cross-service event contract (ADR-027). Each service owns its own copy.</summary>
public static class Topics
{
    // Published whenever a restaurant or its menu changes. Carries the FULL current snapshot
    // (event-carried state transfer, ADR-037) so the consumer never has to call back (ADR-008).
    public const string MenuUpdated = "menu-updated";
}

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

    public Task PublishRawAsync(string topic, string key, string value, CancellationToken ct = default)
        => _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value }, ct);

    public void Dispose() => _producer.Dispose();
}
