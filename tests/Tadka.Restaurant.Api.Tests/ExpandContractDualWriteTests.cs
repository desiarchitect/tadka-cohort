using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Restaurant.Api.Data;

namespace Tadka.Restaurant.Api.Tests;

/// <summary>ADR-038: dual-write Name → DisplayName when Demo:DualWriteDisplayName=true.</summary>
public class ExpandContractDualWriteTests
{
    private static readonly Guid Meghana = new("a1b2c3d4-0001-4000-8000-000000000001");
    private static readonly Guid Biryani = new("b1b2c3d4-0001-4000-8000-000000000001");

    [Fact]
    public async Task Patch_name_with_dual_write_fills_display_name()
    {
        await using var factory = new DualWriteRestaurantApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();

        var resp = await client.PatchAsJsonAsync(
            $"/api/v1/restaurants/{Meghana}/menu/{Biryani}",
            new { name = "Chicken Biryani (Dual)" });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
        var item = await db.MenuItems.AsNoTracking().FirstAsync(m => m.Id == Biryani);
        Assert.Equal("Chicken Biryani (Dual)", item.Name);
        Assert.Equal("Chicken Biryani (Dual)", item.DisplayName);
    }
}

/// <summary>Same as RestaurantApiFactory but with dual-write lever on.</summary>
file sealed class DualWriteRestaurantApiFactory : RestaurantApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Demo:DualWriteDisplayName", "true");
    }
}
