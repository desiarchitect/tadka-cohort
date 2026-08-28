namespace Tadka.Api.Contracts.Restaurants;

public record MenuItemResponse(
    Guid Id,
    string Name,
    string? Description,
    MoneyResponse Price,
    string Category,
    bool IsAvailable,
    bool IsVeg);

public record MoneyResponse(decimal Amount, string Currency);
