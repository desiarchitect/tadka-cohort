using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Restaurants;

// Entity inside the Restaurant aggregate.
public class MenuItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Money Price { get; set; } = null!;
    public string Category { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public bool IsVeg { get; set; }
}
