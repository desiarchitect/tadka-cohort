using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Tadka.Payment.Api.Data;

/// <summary>
/// Transactional Outbox for Payment → Kafka (ADR-028): charge/refund and <c>payment-results</c> /
/// <c>payment-refunded</c> commit together. Relay publishes; crash mid-publish republishes (Inbox dedups).
/// Closes the dual-write gap that a direct <c>ProduceAsync</c> after SaveChanges left open.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Topic { get; set; } = default!;
    public string Key { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public string? TraceParent { get; set; }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages", "payment");
        b.HasKey(x => x.Id);
        b.Property(x => x.Topic).IsRequired().HasMaxLength(100);
        b.Property(x => x.Key).IsRequired().HasMaxLength(200);
        b.Property(x => x.Payload).IsRequired();
        b.Property(x => x.TraceParent).HasMaxLength(64);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("NOW()");
        b.HasIndex(x => x.ProcessedAt);
    }
}
