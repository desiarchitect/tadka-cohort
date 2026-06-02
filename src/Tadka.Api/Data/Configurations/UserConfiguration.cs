using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", "identity");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(u => u.Name).IsRequired().HasMaxLength(100);
        builder.Property(u => u.Email).IsRequired().HasMaxLength(200);
        builder.Property(u => u.Phone).HasMaxLength(15);
        builder.Property(u => u.PasswordHash).IsRequired().HasMaxLength(500);

        builder.Property(u => u.Role)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(UserRole.Customer);

        builder.Property(u => u.CreatedAt).HasDefaultValueSql("NOW()");

        builder.HasIndex(u => u.Email).IsUnique();

        builder.HasMany(u => u.SavedAddresses).WithOne().HasForeignKey(ua => ua.UserId).OnDelete(DeleteBehavior.Cascade);

        // Seed one customer so POST /orders works against a fresh DB out of the box.
        // GUID matches the docs + cohort-prep/day-03 sample payloads.
        builder.HasData(new User
        {
            Id = new Guid("c1b2c3d4-0001-4000-8000-000000000001"),
            Name = "Priya Sharma",
            Email = "priya@tadka.test",
            Phone = "+919876500001",
            PasswordHash = "seed-not-a-real-hash",
            Role = UserRole.Customer,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
    }
}
