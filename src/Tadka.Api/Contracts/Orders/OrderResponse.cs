using Tadka.Api.Contracts.Restaurants;

namespace Tadka.Api.Contracts.Orders;

public record OrderResponse(
    Guid Id,
    Guid CustomerId,
    Guid RestaurantId,
    string Status,
    List<OrderItemResponse> Items,
    MoneyResponse TotalAmount,
    RestaurantAddressResponse DeliveryAddress,
    DateTime CreatedAt,
    DateTime? ConfirmedAt,
    DateTime? DeliveredAt,
    DateTime? CancelledAt,
    string? CancellationReason);

public record OrderItemResponse(
    Guid Id,
    Guid MenuItemId,
    string Name,
    int Quantity,
    MoneyResponse UnitPrice,
    string? SpecialInstructions);
