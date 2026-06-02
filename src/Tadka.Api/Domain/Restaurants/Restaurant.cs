using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Restaurants;

public class Restaurant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Address Address { get; set; } = null!;
    public bool IsActive { get; set; }
    public List<MenuItem> Menu { get; set; } = [];
    public int AvgPrepTimeMinutes { get; set; } = 30;
    public DateTime CreatedAt { get; set; }
}
