namespace Tadka.Api.Domain.Users;

public class User
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    /// <summary>For a RestaurantOwner: the restaurant they own → a JWT claim used for resource-ownership authz (ADR-031). Null otherwise.</summary>
    public Guid? OwnedRestaurantId { get; set; }
    public List<UserAddress> SavedAddresses { get; set; } = [];
    public DateTime CreatedAt { get; set; }

    // Brute-force lockout (ADR-047). Consecutive failed logins; reset to 0 on a successful login.
    public int FailedLoginAttempts { get; set; }
    // Set once FailedLoginAttempts crosses the threshold; the account can't log in (even with the
    // correct password) until this passes. Null when not locked.
    public DateTime? LockedUntil { get; set; }
}

public enum UserRole
{
    Customer,
    RestaurantOwner,
    DeliveryAgent,
    Admin
}
