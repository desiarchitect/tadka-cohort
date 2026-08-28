using FluentValidation;

namespace Tadka.Api.Contracts.Orders;

public class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderRequestValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty().WithMessage("Customer ID is required.");
        RuleFor(x => x.RestaurantId).NotEmpty().WithMessage("Restaurant ID is required.");

        RuleFor(x => x.Items)
            .NotEmpty().WithMessage("Order must have at least one item.");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.MenuItemId).NotEmpty().WithMessage("Menu item ID is required.");
            item.RuleFor(i => i.Quantity).GreaterThan(0).WithMessage("Quantity must be at least 1.");
        });

        RuleFor(x => x.DeliveryAddress).NotNull().WithMessage("Delivery address is required.");

        When(x => x.DeliveryAddress is not null, () =>
        {
            RuleFor(x => x.DeliveryAddress.Line1).NotEmpty().WithMessage("Address line 1 is required.");
            RuleFor(x => x.DeliveryAddress.City).NotEmpty().WithMessage("City is required.");
            RuleFor(x => x.DeliveryAddress.Pincode)
                .NotEmpty().WithMessage("Pincode is required.")
                .Matches(@"^\d{6}$").WithMessage("Pincode must be 6 digits.");
        });
    }
}
