using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tadka.Api.Domain.Users;

namespace Tadka.Api.Data.Configurations;

public class UserAddressConfiguration : IEntityTypeConfiguration<UserAddress>
{
    public void Configure(EntityTypeBuilder<UserAddress> builder)
    {
        builder.ToTable("user_addresses", "identity");

        builder.HasKey(ua => ua.Id);
        builder.Property(ua => ua.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(ua => ua.UserId).IsRequired();
        builder.Property(ua => ua.Label).HasMaxLength(50);
        builder.Property(ua => ua.IsDefault).HasDefaultValue(false);

        builder.OwnsOne(ua => ua.Address, addr =>
        {
            addr.Property(a => a.Line1).HasColumnName("line1").HasMaxLength(200);
            addr.Property(a => a.Line2).HasColumnName("line2").HasMaxLength(200);
            addr.Property(a => a.City).HasColumnName("city").HasMaxLength(50);
            addr.Property(a => a.Pincode).HasColumnName("pincode").HasMaxLength(10);
            addr.Property(a => a.Latitude).HasColumnName("latitude");
            addr.Property(a => a.Longitude).HasColumnName("longitude");
        });
    }
}
