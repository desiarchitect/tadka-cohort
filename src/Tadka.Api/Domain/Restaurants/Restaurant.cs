using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Restaurants;

// Aggregate Root. The menu items belong to the restaurant — they're
// meaningless without it, and menu availability is the restaurant's job.
public class Restaurant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Address Address { get; set; } = null!;
    public bool IsActive { get; set; }
    public List<MenuItem> Menu { get; set; } = [];
    public int AvgPrepTimeMinutes { get; set; }
    public DateTime CreatedAt { get; set; }
}
