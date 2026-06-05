using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Auth;

/// <summary>
/// Idempotent startup seed of known demo users with REAL password hashes + roles, so login works against a
/// fresh DB and the cohort demos have predictable accounts. (The Day-1 seed customer had a placeholder hash.)
/// Demo password for every seeded account: <see cref="DefaultPassword"/>.
/// </summary>
public static class AuthSeeder
{
    public const string DefaultPassword = "Password123!";

    private static readonly Guid Customer1 = new("c1b2c3d4-0001-4000-8000-000000000001"); // Priya (Day-1 seed)
    private static readonly Guid Customer2 = new("c1b2c3d4-0002-4000-8000-000000000002"); // Rahul (for the cross-customer 403 demo)
    private static readonly Guid Admin     = new("d0000000-0000-4000-8000-000000000001");
    private static readonly Guid Owner1     = new("e0000000-0000-4000-8000-000000000001");
    private static readonly Guid Owner2     = new("e0000000-0000-4000-8000-000000000002");
    private static readonly Guid Restaurant1 = new("a1b2c3d4-0001-4000-8000-000000000001"); // Meghana
    private static readonly Guid Restaurant2 = new("a1b2c3d4-0002-4000-8000-000000000002");

    public static async Task SeedAsync(TadkaDbContext db, IPasswordHasher<User> hasher)
    {
        await Upsert(db, hasher, Customer1, "Priya Sharma",  "priya@tadka.test",  "+919876500001", UserRole.Customer);
        await Upsert(db, hasher, Customer2, "Rahul Verma",   "rahul@tadka.test",  "+919876500002", UserRole.Customer);
        await Upsert(db, hasher, Admin,     "Tadka Admin",   "admin@tadka.test",  "+919876500009", UserRole.Admin);
        await Upsert(db, hasher, Owner1,    "Meghana Owner", "owner1@tadka.test", "+919876500011", UserRole.RestaurantOwner, Restaurant1);
        await Upsert(db, hasher, Owner2,    "Owner Two",     "owner2@tadka.test", "+919876500012", UserRole.RestaurantOwner, Restaurant2);
        await db.SaveChangesAsync();
    }

    private static async Task Upsert(TadkaDbContext db, IPasswordHasher<User> hasher,
        Guid id, string name, string email, string phone, UserRole role, Guid? ownedRestaurantId = null)
    {
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            user = new User { Id = id, Name = name, Email = email, Phone = phone, Role = role, OwnedRestaurantId = ownedRestaurantId, CreatedAt = DateTime.UtcNow };
            user.PasswordHash = hasher.HashPassword(user, DefaultPassword);
            db.Add(user);
            return;
        }

        // Existing (e.g. the Day-1 seed customer with a placeholder hash): set role/ownership + a real hash.
        user.Role = role;
        user.OwnedRestaurantId = ownedRestaurantId;
        user.Email = email;
        if (!user.PasswordHash.StartsWith("AQAAAA")) // not an ASP.NET Identity hash yet → set one
            user.PasswordHash = hasher.HashPassword(user, DefaultPassword);
    }
}
