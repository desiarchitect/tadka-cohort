using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Tadka.Api.Data.Messaging;

/// <summary>
/// Persisted orchestration saga state (ADR-046/062). Written only when <c>Saga:Mode=Orchestration</c>
/// so operators can query in-flight compensation steps without grepping logs. Choreography mode
/// leaves this table empty — the flow is implicit in events (ADR-029).
/// </summary>
public sealed class SagaInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string SagaType { get; set; } = default!; // e.g. refund-compensation
    public string CurrentStep { get; set; } = default!;
    public string Status { get; set; } = "Running"; // Running | Completed | Failed
    public string? Detail { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class SagaInstanceConfiguration : IEntityTypeConfiguration<SagaInstance>
{
    public void Configure(EntityTypeBuilder<SagaInstance> b)
    {
        b.ToTable("saga_instances", "ordering");
        b.HasKey(x => x.Id);
        b.Property(x => x.SagaType).IsRequired().HasMaxLength(64);
        b.Property(x => x.CurrentStep).IsRequired().HasMaxLength(64);
        b.Property(x => x.Status).IsRequired().HasMaxLength(20);
        b.Property(x => x.Detail).HasMaxLength(500);
        b.Property(x => x.StartedAt).HasDefaultValueSql("NOW()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("NOW()");
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => new { x.SagaType, x.Status });
    }
}
