namespace Tadka.Api.Contracts.Restaurants;

public record RestaurantResponse(
    Guid Id,
    string Name,
    RestaurantAddressResponse Address,
    bool IsActive,
    int AvgPrepTimeMinutes,
    DateTime CreatedAt);

public record RestaurantAddressResponse(
    string Line1,
    string Line2,
    string City,
    string Pincode,
    double Latitude,
    double Longitude);
