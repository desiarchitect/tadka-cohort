using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Delivery.Api.Domain;

namespace Tadka.Delivery.Api.Data;

/// <summary>The Delivery service's own data boundary (ADR-033/026): the <c>delivery</c> schema in its OWN Postgres.</summary>
public class DeliveryDbContext(DbContextOptions<DeliveryDbContext> options) : DbContext(options)
{
    public DbSet<DeliveryAgent> Agents => Set<DeliveryAgent>();
    public DbSet<DeliveryAssignment> Assignments => Set<DeliveryAssignment>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<PendingAssignment> PendingAssignments => Set<PendingAssignment>();
    public DbSet<CancelledOrder> CancelledOrders => Set<CancelledOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("delivery");

        modelBuilder.ApplyConfiguration(new DeliveryAgentConfiguration());
        modelBuilder.Entity<DeliveryAssignment>(b =>
        {
            b.ToTable("assignments", "delivery");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).HasDefaultValue(AssignmentStatus.Assigned);
            b.HasIndex(x => x.OrderId).IsUnique(); // one assignment per order — the idempotency guard
        });
        modelBuilder.Entity<PendingAssignment>(b =>
        {
            b.ToTable("pending_assignments", "delivery");
            b.HasKey(x => x.OrderId); // one waiting row per order: parking the same order twice is a no-op
            b.Property(x => x.CreatedAt).HasDefaultValueSql("NOW()");
            b.HasIndex(x => x.CreatedAt); // the sweeper serves the oldest waiting order first
        });
        modelBuilder.Entity<CancelledOrder>(b =>
        {
            b.ToTable("cancelled_orders", "delivery");
            b.HasKey(x => x.OrderId); // one row per order: cancelling twice is a no-op
            b.Property(x => x.CancelledAt).HasDefaultValueSql("NOW()");
        });
        modelBuilder.Entity<InboxMessage>(b =>
        {
            b.ToTable("inbox_messages", "delivery");
            b.HasKey(x => x.MessageId);
            b.Property(x => x.ConsumedAt).HasDefaultValueSql("NOW()");
        });
    }
}

public class DeliveryAgentConfiguration : IEntityTypeConfiguration<DeliveryAgent>
{
    private static readonly DateTime Seed = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Configure(EntityTypeBuilder<DeliveryAgent> b)
    {
        b.ToTable("agents", "delivery");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(100);
        b.Property(x => x.Phone).HasMaxLength(15);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).HasDefaultValue(AgentStatus.Available);

        b.HasIndex(x => x.UserId).IsUnique(); // one login <-> one rider record

        // Seed a few available riders so assignment works out of the box. UserId matches the DeliveryAgent
        // accounts the monolith's AuthSeeder creates (suresh.rider@ / lakshmi.rider@ / imran.rider@tadka.test),
        // so a rider can log in and post their own location (and only their own).
        b.HasData(
            new DeliveryAgent { Id = new("f0000000-0000-4000-8000-000000000001"), Name = "Suresh", Phone = "+919876600001", Status = AgentStatus.Available, UserId = RiderUsers.Suresh },
            new DeliveryAgent { Id = new("f0000000-0000-4000-8000-000000000002"), Name = "Lakshmi", Phone = "+919876600002", Status = AgentStatus.Available, UserId = RiderUsers.Lakshmi },
            new DeliveryAgent { Id = new("f0000000-0000-4000-8000-000000000003"), Name = "Imran", Phone = "+919876600003", Status = AgentStatus.Available, UserId = RiderUsers.Imran });
    }
}
