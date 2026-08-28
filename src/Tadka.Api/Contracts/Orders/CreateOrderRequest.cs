namespace Tadka.Api.Contracts.Orders;

public record CreateOrderRequest(
    Guid CustomerId,
    Guid RestaurantId,
    List<CreateOrderItemRequest> Items,
    OrderAddressRequest? DeliveryAddress);

public record CreateOrderItemRequest(
    Guid MenuItemId,
    int Quantity,
    string? SpecialInstructions);

public record OrderAddressRequest(
    string Line1,
    string Line2,
    string City,
    string Pincode,
    double Latitude,
    double Longitude);

public record UpdateOrderStatusRequest(string Status);

public record CancelOrderRequest(string? Reason);
