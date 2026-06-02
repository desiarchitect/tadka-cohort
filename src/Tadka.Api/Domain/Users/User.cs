namespace Tadka.Api.Domain.Users;

public class User
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public List<UserAddress> SavedAddresses { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public enum UserRole
{
    Customer,
    RestaurantOwner,
    DeliveryAgent,
    Admin
}
