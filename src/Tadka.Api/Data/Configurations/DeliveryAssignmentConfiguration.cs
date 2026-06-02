using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Delivery;

namespace Tadka.Api.Data.Configurations;

public class DeliveryAssignmentConfiguration : IEntityTypeConfiguration<DeliveryAssignment>
{
    public void Configure(EntityTypeBuilder<DeliveryAssignment> builder)
    {
        builder.ToTable("assignments", "delivery");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(d => d.OrderId).IsRequired();
        builder.Property(d => d.AgentId).IsRequired();

        builder.Property(d => d.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(AssignmentStatus.Assigned);

        builder.Property(d => d.AssignedAt).HasDefaultValueSql("NOW()");

        builder.HasOne<DeliveryAgent>().WithMany().HasForeignKey(d => d.AgentId).OnDelete(DeleteBehavior.Restrict);
    }
}
