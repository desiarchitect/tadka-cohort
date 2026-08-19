using Microsoft.EntityFrameworkCore;
using Tadka.Api.Domain.Delivery;
using Tadka.Api.Domain.Orders;
using Tadka.Api.Domain.Payments;
using Tadka.Api.Domain.Restaurants;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Data;

// One DbContext for the whole monolith — but every table is mapped into a
// PostgreSQL SCHEMA per domain (ordering, restaurant, delivery, identity,
// payment). When we extract a service in Week 4+, it takes its schema. There
// are NO cross-schema foreign keys (ADR-008); cross-domain links are by ID only.
//
// Day 2: model + schema boundaries. Program.cs applies InitialDomainModel on startup.
public class TadkaDbContext : DbContext
{
    public TadkaDbContext(DbContextOptions<TadkaDbContext> options) : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Restaurant> Restaurants => Set<Restaurant>();
    public DbSet<DeliveryAgent> DeliveryAgents => Set<DeliveryAgent>();
    public DbSet<DeliveryAssignment> DeliveryAssignments => Set<DeliveryAssignment>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── ordering schema ──────────────────────────────────────────────
        modelBuilder.Entity<Order>(e =>
        {
            e.ToTable("orders", "ordering");
            e.HasKey(o => o.Id);
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            e.OwnsOne(o => o.TotalAmount);
            e.OwnsOne(o => o.DeliveryAddress);
            e.HasMany(o => o.Items).WithOne().HasForeignKey("OrderId").OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderItem>(e =>
        {
            e.ToTable("order_items", "ordering");
            e.HasKey(i => i.Id);
            e.OwnsOne(i => i.UnitPrice);
        });

        // ── restaurant schema ────────────────────────────────────────────
        modelBuilder.Entity<Restaurant>(e =>
        {
            e.ToTable("restaurants", "restaurant");
            e.HasKey(r => r.Id);
            e.OwnsOne(r => r.Address);
            e.HasMany(r => r.Menu).WithOne().HasForeignKey("RestaurantId").OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MenuItem>(e =>
        {
            e.ToTable("menu_items", "restaurant");
            e.HasKey(m => m.Id);
            e.OwnsOne(m => m.Price);
        });

        // ── delivery schema ──────────────────────────────────────────────
        modelBuilder.Entity<DeliveryAgent>(e =>
        {
            e.ToTable("delivery_agents", "delivery");
            e.HasKey(a => a.Id);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            e.OwnsOne(a => a.CurrentLocation);
        });

        modelBuilder.Entity<DeliveryAssignment>(e =>
        {
            e.ToTable("delivery_assignments", "delivery");
            e.HasKey(a => a.Id);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
        });

        // ── identity schema ──────────────────────────────────────────────
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users", "identity");
            e.HasKey(u => u.Id);
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(u => u.Email).IsUnique();
            e.HasMany(u => u.SavedAddresses).WithOne().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserAddress>(e =>
        {
            e.ToTable("user_addresses", "identity");
            e.HasKey(a => a.Id);
            e.OwnsOne(a => a.Address);
        });

        // ── payment schema ───────────────────────────────────────────────
        modelBuilder.Entity<Payment>(e =>
        {
            e.ToTable("payments", "payment");
            e.HasKey(p => p.Id);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.OwnsOne(p => p.Amount);
        });
    }
}
