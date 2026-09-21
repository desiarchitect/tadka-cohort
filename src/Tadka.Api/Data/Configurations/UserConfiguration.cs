using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Users;
using Tadka.Api.Infrastructure.Security;

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

        // Field-level PII encryption at rest (ADR-045). FieldCipher is configured once at startup from
        // Demo:EncryptPiiAtRest (default true — the correct behaviour ships by default; the break demo
        // flips it off). 250 chars accommodates AES-GCM's nonce+tag+ciphertext, base64-encoded, comfortably
        // wider than any plaintext phone number ever needs, and stays fixed regardless of the flag so a
        // toggle never requires a fresh migration.
        var phone = builder.Property(u => u.Phone).HasMaxLength(250);
        if (FieldCipher.Enabled)
            phone.HasConversion(v => FieldCipher.Encrypt(v), v => FieldCipher.Decrypt(v));

        builder.Property(u => u.PasswordHash).IsRequired().HasMaxLength(500);

        builder.Property(u => u.Role)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(UserRole.Customer);

        builder.Property(u => u.CreatedAt).HasDefaultValueSql("NOW()");

        builder.HasIndex(u => u.Email).IsUnique();
        builder.Property(u => u.OwnedRestaurantId); // RestaurantOwner → their restaurant (ADR-031)

        // Brute-force lockout counters (ADR-047).
        builder.Property(u => u.FailedLoginAttempts).HasDefaultValue(0);
        builder.Property(u => u.LockedUntil);

        builder.HasMany(u => u.SavedAddresses).WithOne().HasForeignKey(ua => ua.UserId).OnDelete(DeleteBehavior.Cascade);

        // The Day-1 seed customer (Priya, GUID matches docs + cohort-prep/day-03 sample payloads) used to
        // be seeded here via HasData. Moved to AuthSeeder.SeedAsync (runtime, idempotent upsert) because
        // HasData is incompatible with a non-deterministic value converter (ADR-045's AES-GCM encryption
        // uses a fresh random nonce per write, so the encrypted seed value can never match a value frozen
        // into a migration snapshot — EF detects "model changes every time it's built" and refuses to
        // start). AuthSeeder already creates every other demo user this way; Priya is no longer a special case.
    }
}
