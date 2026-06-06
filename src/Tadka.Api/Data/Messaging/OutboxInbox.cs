using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Tadka.Api.Data.Messaging;

/// <summary>
/// Transactional Outbox row (ADR-028). Written in the SAME transaction as the order, so the event can
/// never be lost on a crash (no dual-write). The OutboxRelay publishes unsent rows to Kafka and stamps
/// <see cref="ProcessedAt"/>. Also a built-in audit log of what Ordering emitted.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Topic { get; set; } = default!;
    public string Key { get; set; } = default!;
    public string Payload { get; set; } = default!;   // already-serialized JSON
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }

    /// <summary>W3C traceparent captured when the row was enqueued (ADR-041). The relay re-injects it as a
    /// Kafka header so the async hop rejoins the order's trace even though the request span is long gone.
    /// Nullable — when telemetry is off there's no ambient activity and this stays null (harmless).</summary>
    public string? TraceParent { get; set; }
}

/// <summary>
/// Inbox row (ADR-028): a processed message-id, so an at-least-once redelivery is skipped (idempotent
/// consumer). Here it dedups inbound <c>payment-results</c> on the Ordering side.
/// </summary>
public class InboxMessage
{
    public Guid MessageId { get; set; }
    public DateTime ConsumedAt { get; set; } = DateTime.UtcNow;
}

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages", "ordering");
        b.HasKey(x => x.Id);
        b.Property(x => x.Topic).IsRequired().HasMaxLength(100);
        b.Property(x => x.Key).IsRequired().HasMaxLength(200);
        b.Property(x => x.Payload).IsRequired();
        b.Property(x => x.TraceParent).HasMaxLength(64);   // W3C traceparent (ADR-041), nullable
        b.Property(x => x.CreatedAt).HasDefaultValueSql("NOW()");
        // The relay scans for unsent rows oldest-first.
        b.HasIndex(x => x.ProcessedAt);
    }
}

public class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> b)
    {
        b.ToTable("inbox_messages", "ordering");
        b.HasKey(x => x.MessageId);
        b.Property(x => x.ConsumedAt).HasDefaultValueSql("NOW()");
    }
}
