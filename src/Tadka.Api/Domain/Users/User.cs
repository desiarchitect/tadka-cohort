namespace Tadka.Api.Domain.Users;

public enum UserRole
{
    Customer,
    RestaurantOwner,
    DeliveryAgent,
    Admin
}

// Aggregate Root in the identity domain. Manages its own profile and saved
// addresses. Authentication (password hashing, JWT) is deliberately deferred —
// it arrives when we have a reason to add it.
public class User
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public UserRole Role { get; set; }
    public List<UserAddress> SavedAddresses { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}
