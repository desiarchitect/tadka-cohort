using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Data.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens", "identity");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasDefaultValueSql("gen_random_uuid()");

        // The hash IS how we look a presented token up — unique + indexed, never a table scan (ADR-048).
        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(64);
        builder.HasIndex(t => t.TokenHash).IsUnique();

        builder.HasIndex(t => t.UserId);
        // Reuse detection revokes an entire chain in one statement (WHERE FamilyId = x) — index it.
        builder.HasIndex(t => t.FamilyId);

        builder.Property(t => t.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(t => t.ExpiresAt).IsRequired();

        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
