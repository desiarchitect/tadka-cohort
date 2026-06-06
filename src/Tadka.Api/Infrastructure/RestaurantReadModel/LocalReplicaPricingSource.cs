using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data;
using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Infrastructure.RestaurantReadModel;

/// <summary>
/// The DEFAULT pricing source (ADR-037): prices an order from Ordering's OWN local replica
/// (<c>ordering.restaurant_replica</c> + <c>ordering.menu_replica</c>), fed by the Restaurant service's
/// <c>menu-updated</c> events. No cross-service call → the order path stays available even when Restaurant
/// is down. The trade is eventual consistency: a seconds-long stale-price window after a price change.
/// </summary>
public sealed class LocalReplicaPricingSource(TadkaReadDbContext read) : IRestaurantPricingSource
{
    public async Task<RestaurantPricing?> GetAsync(Guid restaurantId, CancellationToken ct = default)
    {
        var restaurant = await read.Set<Data.ReadModel.RestaurantReplica>()
            .FirstOrDefaultAsync(r => r.Id == restaurantId, ct);
        if (restaurant is null) return null;

        var menu = await read.Set<Data.ReadModel.MenuItemReplica>()
            .Where(m => m.RestaurantId == restaurantId)
            .Select(m => new MenuItemPricing(m.MenuItemId, m.Name, m.PriceAmount, m.PriceCurrency, m.IsAvailable))
            .ToListAsync(ct);

        return new RestaurantPricing(restaurant.Id, restaurant.IsActive, menu);
    }
}
