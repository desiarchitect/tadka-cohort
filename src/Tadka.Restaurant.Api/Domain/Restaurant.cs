namespace Tadka.Restaurant.Api.Domain;

// Value objects — the Restaurant service owns its OWN copies (database-per-service, ADR-026/036).
// It does NOT reference the monolith's types; a service boundary is also a code boundary.
public record Money(decimal Amount, string Currency = "INR");

public record Address(
    string Line1,
    string Line2,
    string City,
    string Pincode,
    double Latitude,
    double Longitude);

/// <summary>A restaurant. This service OWNS this type + its data (ADR-036) — its own Postgres.</summary>
public class Restaurant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Address Address { get; set; } = null!;
    public bool IsActive { get; set; }
    public List<MenuItem> Menu { get; set; } = [];
    public int AvgPrepTimeMinutes { get; set; } = 30;
    public DateTime CreatedAt { get; set; }
}

public class MenuItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Money Price { get; set; } = null!;
    public string Category { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public bool IsVeg { get; set; }
}

/// <summary>
/// Transactional Outbox row (ADR-028). Written in the SAME transaction as a menu change, so the
/// <c>menu-updated</c> event can never be lost on a crash (no dual-write). The OutboxRelay publishes
/// unsent rows to Kafka; the monolith's read-model consumer upserts its local price replica (ADR-037).
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Topic { get; set; } = default!;
    public string Key { get; set; } = default!;
    public string Payload { get; set; } = default!; // already-serialized JSON
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }

    /// <summary>W3C traceparent captured at enqueue (ADR-041) — re-injected as a Kafka header at relay so
    /// the menu-updated → replica flow shows as one trace. Null when telemetry is off (harmless).</summary>
    public string? TraceParent { get; set; }
}
