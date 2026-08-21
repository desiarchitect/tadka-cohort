namespace Tadka.Api.Domain.ValueObjects;

// Value Object: no identity, immutable, equal by value.
// Money(299, "INR") == Money(299, "INR"). A C# record gives us that for free.
public record Money(decimal Amount, string Currency = "INR");
