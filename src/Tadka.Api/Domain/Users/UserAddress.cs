using Tadka.Api.Domain.ValueObjects;

namespace Tadka.Api.Domain.Users;

public class UserAddress
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Label { get; set; }
    public Address Address { get; set; } = null!;
    public bool IsDefault { get; set; }
}
