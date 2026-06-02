namespace Tadka.Api.Domain.ValueObjects;

public record Money(decimal Amount, string Currency = "INR");
