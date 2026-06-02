using FluentValidation;

namespace Tadka.Api.Contracts.Restaurants;

public class CreateRestaurantRequestValidator : AbstractValidator<CreateRestaurantRequest>
{
    public CreateRestaurantRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Restaurant name is required.")
            .MaximumLength(200);

        RuleFor(x => x.AvgPrepTimeMinutes)
            .GreaterThan(0).WithMessage("Average prep time must be positive.");

        RuleFor(x => x.Address).NotNull().WithMessage("Address is required.");

        When(x => x.Address is not null, () =>
        {
            RuleFor(x => x.Address.Line1).NotEmpty().WithMessage("Address line 1 is required.");
            RuleFor(x => x.Address.City).NotEmpty().WithMessage("City is required.");
            RuleFor(x => x.Address.Pincode)
                .NotEmpty().WithMessage("Pincode is required.")
                .Matches(@"^\d{6}$").WithMessage("Pincode must be 6 digits.");
        });
    }
}
