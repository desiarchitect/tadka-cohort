using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Users;

// Entity inside the User aggregate: a saved address has identity (you can edit
// "Home" vs "Office") and wraps an Address value object.
public class UserAddress
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Label { get; set; }
    public Address Address { get; set; } = null!;
    public bool IsDefault { get; set; }
}
