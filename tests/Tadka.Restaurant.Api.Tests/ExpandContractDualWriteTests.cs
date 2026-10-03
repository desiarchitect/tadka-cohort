using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Restaurant.Api.Data;

namespace Tadka.Restaurant.Api.Tests;

/// <summary>ADR-038: dual-write Name to DisplayName while both columns exist (on by default).</summary>
public class ExpandContractDualWriteTests
{
    private static readonly Guid Meghana = new("a1b2c3d4-0001-4000-8000-000000000001");
    private static readonly Guid Biryani = new("b1b2c3d4-0001-4000-8000-000000000001");

    [Fact]
    public async Task Patch_name_fills_display_name_by_default()
    {
        await using var factory = new RestaurantApiFactory();
        await factory.InitializeAsync();
        var (name, displayName) = await PatchNameAsync(factory, "Chicken Biryani (Dual)");

        Assert.Equal("Chicken Biryani (Dual)", name);
        Assert.Equal("Chicken Biryani (Dual)", displayName);
    }

    [Fact]
    public async Task With_dual_write_switched_off_display_name_is_left_behind_which_is_why_it_is_on()
    {
        await using var factory = new DualWriteOffRestaurantApiFactory();
        await factory.InitializeAsync();
        var (name, displayName) = await PatchNameAsync(factory, "Chicken Biryani (Off)");

        Assert.Equal("Chicken Biryani (Off)", name);
        Assert.NotEqual("Chicken Biryani (Off)", displayName); // stale or null: the new column fell behind
    }

    private static async Task<(string Name, string? DisplayName)> PatchNameAsync(RestaurantApiFactory factory, string newName)
    {
        var client = factory.CreateClient();
        var resp = await client.PatchAsJsonAsync($"/api/v1/restaurants/{Meghana}/menu/{Biryani}", new { name = newName });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
        var item = await db.MenuItems.AsNoTracking().FirstAsync(m => m.Id == Biryani);
        return (item.Name, item.DisplayName);
    }
}

/// <summary>Same as RestaurantApiFactory but with the dual-write lever switched off.</summary>
file sealed class DualWriteOffRestaurantApiFactory : RestaurantApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Demo:DualWriteDisplayName", "false");
    }
}
