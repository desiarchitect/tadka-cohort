using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tadka.Restaurant.Api.Data;

/// <summary>Design-time factory so `dotnet ef migrations` builds the context without running the app.</summary>
public sealed class RestaurantDbContextFactory : IDesignTimeDbContextFactory<RestaurantDbContext>
{
    public RestaurantDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RestaurantDbContext>()
            .UseNpgsql("Host=localhost;Port=5436;Database=tadka_restaurant;Username=tadka;Password=tadka_local")
            .Options;
        return new RestaurantDbContext(options);
    }
}
