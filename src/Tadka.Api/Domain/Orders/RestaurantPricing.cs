namespace Tadka.Api.Domain.Orders;

/// <summary>
/// The slice of restaurant data Ordering needs to price an order — a projection, NOT the Restaurant
/// aggregate (which now lives in its own service, ADR-036). Sourced from the local read model (ADR-037)
/// or, for teaching contrast, a synchronous HTTP read (the `SyncHttp` lever).
/// </summary>
public sealed record RestaurantPricing(Guid Id, bool IsActive, IReadOnlyList<MenuItemPricing> Menu);

public sealed record MenuItemPricing(Guid MenuItemId, string Name, decimal Amount, string Currency, bool IsAvailable);

/// <summary>
/// Where order pricing gets its menu data. Two implementations: the default local replica (available even
/// when Restaurant is down) and a synchronous HTTP read (couples the order path to Restaurant — the wound
/// we demonstrate, ADR-037). Selected by <c>Ordering:RestaurantReadMode</c>.
/// </summary>
public interface IRestaurantPricingSource
{
    Task<RestaurantPricing?> GetAsync(Guid restaurantId, CancellationToken ct = default);
}
