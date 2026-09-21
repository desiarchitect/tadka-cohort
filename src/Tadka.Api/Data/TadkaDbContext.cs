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
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>(); // rotation + reuse detection (ADR-048)

    // NOTE: Payment is NOT here. As of Day 7 (ADR-022) it lives in its own module behind
    // PaymentDbContext (the `payment` schema, its own migration history). The core context has zero
    // knowledge of payments — that decoupling is what Day 8 extracts into a separate service.

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ordering");
        // The core context owns orders/restaurants/delivery/identity. Payment was extracted into its own
        // service on Day 8 (ADR-024) — there is no Payment configuration left in this assembly to exclude.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TadkaDbContext).Assembly);
    }
}
