namespace Tadka.Api.Domain.ValueObjects;

// Value Object: a point on the map. No identity, immutable.
public record GeoLocation(double Latitude, double Longitude);
