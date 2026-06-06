namespace Tadka.Api.Contracts.Restaurants;

// Shared response records still used by the ORDER contracts after Restaurant was extracted (ADR-036):
// - MoneyResponse: every money value in an API is structured ({amount, currency}), never a naked decimal.
// - RestaurantAddressResponse: reused by OrderResponse for the delivery address shape.
// The restaurant CRUD contracts themselves moved to the Restaurant service; these stay because Ordering
// depends on them. (Namespace kept as-is to avoid churning every `using` across the order code.)
public record MoneyResponse(decimal Amount, string Currency);

public record RestaurantAddressResponse(
    string Line1,
    string Line2,
    string City,
    string Pincode,
    double Latitude,
    double Longitude);
