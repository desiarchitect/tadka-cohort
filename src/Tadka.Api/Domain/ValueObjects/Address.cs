namespace Tadka.Api.Domain.ValueObjects;

// Value Object: a postal address has no identity of its own — it's just its values.
public record Address(
    string Line1,
    string? Line2,
    string City,
    string Pincode,
    double Latitude,
    double Longitude);
