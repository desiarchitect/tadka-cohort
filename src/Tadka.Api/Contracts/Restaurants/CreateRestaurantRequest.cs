namespace Tadka.Api.Contracts.Restaurants;

public record CreateRestaurantRequest(
    string Name,
    CreateRestaurantAddressRequest Address,
    int AvgPrepTimeMinutes = 30);

public record CreateRestaurantAddressRequest(
    string Line1,
    string Line2,
    string City,
    string Pincode,
    double Latitude,
    double Longitude);

public record UpdateAvailabilityRequest(bool IsAvailable);

// Partial update of a restaurant (PATCH). Any field left null is unchanged.
// Setting IsActive=false deactivates the restaurant — our "delete" (no hard DELETE).
public record UpdateRestaurantRequest(
    string? Name,
    int? AvgPrepTimeMinutes,
    bool? IsActive);

// Money is always structured ({amount, currency}), never a naked decimal
// (API design guide §9) — same shape as MoneyResponse.
public record MoneyRequest(decimal Amount, string Currency = "INR");

// Add a menu item to a restaurant (restaurant-partner flow).
public record CreateMenuItemRequest(
    string Name,
    string? Description,
    MoneyRequest Price,
    string Category,
    bool IsVeg);

// Partial update of a menu item (PATCH) — e.g. a restaurant raising a dish's price.
public record UpdateMenuItemRequest(
    string? Name,
    string? Description,
    MoneyRequest? Price,
    string? Category,
    bool? IsVeg,
    bool? IsAvailable);
