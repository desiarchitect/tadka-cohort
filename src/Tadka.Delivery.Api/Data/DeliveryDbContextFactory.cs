using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tadka.Delivery.Api.Data;

/// <summary>Design-time factory so `dotnet ef migrations` builds the context without running the app.</summary>
public sealed class DeliveryDbContextFactory : IDesignTimeDbContextFactory<DeliveryDbContext>
{
    public DeliveryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DeliveryDbContext>()
            .UseNpgsql("Host=localhost;Port=5435;Database=tadka_delivery;Username=tadka;Password=tadka_local")
            .Options;
        return new DeliveryDbContext(options);
    }
}
