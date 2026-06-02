using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Delivery;

namespace Tadka.Api.Data.Configurations;

public class DeliveryAgentConfiguration : IEntityTypeConfiguration<DeliveryAgent>
{
    public void Configure(EntityTypeBuilder<DeliveryAgent> builder)
    {
        builder.ToTable("agents", "delivery");

        builder.HasKey(da => da.Id);
        builder.Property(da => da.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(da => da.Name).IsRequired().HasMaxLength(100);
        builder.Property(da => da.Phone).IsRequired().HasMaxLength(15);

        builder.Property(da => da.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(AgentStatus.Available);

        builder.OwnsOne(da => da.CurrentLocation, loc =>
        {
            loc.Property(l => l.Latitude).HasColumnName("current_latitude");
            loc.Property(l => l.Longitude).HasColumnName("current_longitude");
        });

        // Seed data: a couple of delivery agents
        builder.HasData(
            new { Id = new Guid("d1b2c3d4-0001-4000-8000-000000000001"), Name = "Ramesh Kumar", Phone = "+919876543210", Status = AgentStatus.Available },
            new { Id = new Guid("d1b2c3d4-0002-4000-8000-000000000002"), Name = "Suresh Patel", Phone = "+919876543211", Status = AgentStatus.Available }
        );

        builder.OwnsOne(da => da.CurrentLocation).HasData(
            new { DeliveryAgentId = new Guid("d1b2c3d4-0001-4000-8000-000000000001"), Latitude = 12.9352, Longitude = 77.6245 },
            new { DeliveryAgentId = new Guid("d1b2c3d4-0002-4000-8000-000000000002"), Latitude = 12.9784, Longitude = 77.6408 }
        );
    }
}
