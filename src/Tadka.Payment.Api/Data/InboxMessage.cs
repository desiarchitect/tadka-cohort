using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Tadka.Payment.Api.Data;

/// <summary>
/// Inbox row (ADR-028): a processed inbound message-id, so an at-least-once redelivery of `order-placed`
/// is skipped. Belt-and-braces with the one-charge unique index — together they give an exactly-once
/// *effect* on at-least-once delivery.
/// </summary>
public class InboxMessage
{
    public Guid MessageId { get; set; }
    public DateTime ConsumedAt { get; set; } = DateTime.UtcNow;
}

public class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> b)
    {
        b.ToTable("inbox_messages", "payment");
        b.HasKey(x => x.MessageId);
        b.Property(x => x.ConsumedAt).HasDefaultValueSql("NOW()");
    }
}
