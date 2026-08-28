using Microsoft.EntityFrameworkCore;
using Tadka.Api.Domain.Delivery;
using Tadka.Api.Domain.Orders;
using Tadka.Api.Domain.Payments;
using Tadka.Api.Domain.Restaurants;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Data;

public class TadkaDbContext : DbContext
{
    public TadkaDbContext(DbContextOptions<TadkaDbContext> options) : base(options)
    {
    }

    // Ordering domain
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<CouponRedemption> CouponRedemptions => Set<CouponRedemption>();

    // Restaurant domain
    public DbSet<Restaurant> Restaurants => Set<Restaurant>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();

    // Delivery domain
    public DbSet<DeliveryAgent> DeliveryAgents => Set<DeliveryAgent>();
    public DbSet<DeliveryAssignment> DeliveryAssignments => Set<DeliveryAssignment>();

    // Identity domain
    public DbSet<User> Users => Set<User>();
    public DbSet<UserAddress> UserAddresses => Set<UserAddress>();

    // Payment domain
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ordering");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TadkaDbContext).Assembly);
    }
}
