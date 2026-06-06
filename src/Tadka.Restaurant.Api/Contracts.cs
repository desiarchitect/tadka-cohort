using FluentValidation;

namespace Tadka.Restaurant.Api.Contracts;

// Request/response contracts — the Restaurant service owns its own copies (a service boundary is the
// API contract, ADR-036). Money is always structured ({amount, currency}), never a naked decimal.
public record MoneyRequest(decimal Amount, string Currency = "INR");
public record MoneyResponse(decimal Amount, string Currency);

public record AddressResponse(string Line1, string Line2, string City, string Pincode, double Latitude, double Longitude);

public record CreateRestaurantAddressRequest(string Line1, string Line2, string City, string Pincode, double Latitude, double Longitude);

public record CreateRestaurantRequest(string Name, CreateRestaurantAddressRequest Address, int AvgPrepTimeMinutes = 30);

public record UpdateRestaurantRequest(string? Name, int? AvgPrepTimeMinutes, bool? IsActive);

public record CreateMenuItemRequest(string Name, string? Description, MoneyRequest Price, string Category, bool IsVeg);

public record UpdateMenuItemRequest(string? Name, string? Description, MoneyRequest? Price, string? Category, bool? IsVeg, bool? IsAvailable);

public record UpdateAvailabilityRequest(bool IsAvailable);

public record RestaurantResponse(Guid Id, string Name, AddressResponse Address, bool IsActive, int AvgPrepTimeMinutes, DateTime CreatedAt);

public record MenuItemResponse(Guid Id, string Name, string? Description, MoneyResponse Price, string Category, bool IsAvailable, bool IsVeg);

public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed class CreateRestaurantRequestValidator : AbstractValidator<CreateRestaurantRequest>
{
    public CreateRestaurantRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Restaurant name is required.").MaximumLength(200);
        RuleFor(x => x.AvgPrepTimeMinutes).GreaterThan(0).WithMessage("Average prep time must be positive.");
        RuleFor(x => x.Address).NotNull().WithMessage("Address is required.");
        When(x => x.Address is not null, () =>
        {
            RuleFor(x => x.Address.Line1).NotEmpty().WithMessage("Address line 1 is required.");
            RuleFor(x => x.Address.City).NotEmpty().WithMessage("City is required.");
            RuleFor(x => x.Address.Pincode).NotEmpty().Matches(@"^\d{6}$").WithMessage("Pincode must be 6 digits.");
        });
    }
}

public sealed class CreateMenuItemRequestValidator : AbstractValidator<CreateMenuItemRequest>
{
    public CreateMenuItemRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Category).NotEmpty();
        RuleFor(x => x.Price).NotNull();
        RuleFor(x => x.Price.Amount).GreaterThan(0).WithMessage("Price must be greater than 0.");
    }
}
