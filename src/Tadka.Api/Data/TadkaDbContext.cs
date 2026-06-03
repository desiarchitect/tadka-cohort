using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data.Configurations;
using Tadka.Api.Domain.Delivery;
using Tadka.Api.Domain.Orders;
using Tadka.Api.Domain.Restaurants;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Data;

public class TadkaDbContext : DbContext
{
    public TadkaDbContext(DbContextOptions<TadkaDbContext> options) : base(options)
    {
    }

    // Lets TadkaReadDbContext (the read-replica context, ADR-016) reuse this exact model
    // by passing its own DbContextOptions<TadkaReadDbContext> through to the base.
    protected TadkaDbContext(DbContextOptions options) : base(options)
    {
    }

    // Ordering domain
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    // Restaurant domain
    public DbSet<Restaurant> Restaurants => Set<Restaurant>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();

    // Delivery domain
    public DbSet<DeliveryAgent> DeliveryAgents => Set<DeliveryAgent>();
    public DbSet<DeliveryAssignment> DeliveryAssignments => Set<DeliveryAssignment>();

    // Identity domain
    public DbSet<User> Users => Set<User>();
    public DbSet<UserAddress> UserAddresses => Set<UserAddress>();

    // NOTE: Payment is NOT here. As of Day 7 (ADR-022) it lives in its own module behind
    // PaymentDbContext (the `payment` schema, its own migration history). The core context has zero
    // knowledge of payments — that decoupling is what Day 8 extracts into a separate service.

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ordering");
        // Apply every configuration in the assembly EXCEPT the Payment module's — that one belongs to
        // PaymentDbContext alone, so the core model never re-acquires the Payment entity (ADR-022).
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(TadkaDbContext).Assembly,
            t => t != typeof(PaymentConfiguration));
    }
}
