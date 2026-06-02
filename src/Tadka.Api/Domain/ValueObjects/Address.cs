namespace Tadka.Api.Domain.ValueObjects;

public record Address(
    string Line1,
    string Line2,
    string City,
    string Pincode,
    double Latitude,
    double Longitude);
