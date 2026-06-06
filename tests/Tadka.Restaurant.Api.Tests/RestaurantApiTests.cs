using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Restaurant.Api.Contracts;
using Tadka.Restaurant.Api.Data;
using Tadka.Restaurant.Api.Domain;
using Tadka.Restaurant.Api.Messaging;

namespace Tadka.Restaurant.Api.Tests;

public class RestaurantApiTests(RestaurantApiFactory factory) : IClassFixture<RestaurantApiFactory>
{
    private readonly RestaurantApiFactory _factory = factory;

    private static readonly Guid Meghana = new("a1b2c3d4-0001-4000-8000-000000000001");
    private static readonly Guid OtherRestaurant = new("a1b2c3d4-0002-4000-8000-000000000002");
    private static readonly Guid Biryani = new("b1b2c3d4-0001-4000-8000-000000000001");
    private static readonly Guid Owner1 = new("e0000000-0000-4000-8000-000000000001");

    [Fact]
    public async Task Get_menu_returns_seeded_items()
    {
        var client = _factory.CreateClient();
        var items = await client.GetFromJsonAsync<List<MenuItemResponse>>($"/api/v1/restaurants/{Meghana}/menu");
        Assert.NotNull(items);
        Assert.Contains(items!, i => i.Name == "Chicken Biryani");
    }

    [Fact]
    public async Task Updating_a_price_stages_a_menu_updated_outbox_event()  // ADR-037 event-carried state transfer
    {
        var client = _factory.CreateClient(); // default Admin
        var resp = await client.PatchAsJsonAsync(
            $"/api/v1/restaurants/{Meghana}/menu/{Biryani}",
            new { price = new { amount = 349m, currency = "INR" } });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();

        // The new price is persisted...
        var item = await db.MenuItems.AsNoTracking().FirstAsync(m => m.Id == Biryani);
        Assert.Equal(349m, item.Price.Amount);

        // ...and a menu-updated snapshot was staged on the Outbox in the same txn (ADR-028/037).
        var outbox = await db.OutboxMessages.AsNoTracking()
            .Where(o => o.Topic == Topics.MenuUpdated && o.Key == Meghana.ToString())
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync();
        Assert.NotNull(outbox);
        Assert.Contains("349", outbox!.Payload);
    }

    [Fact]
    public async Task Owner_cannot_edit_another_restaurants_menu_403()  // RBAC role passes, ownership fails (ADR-031)
    {
        var client = _factory.CreateClient();
        // Owner1 owns Meghana but targets a DIFFERENT restaurant's menu → 403.
        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/restaurants/{OtherRestaurant}/menu/{Guid.NewGuid()}")
        { Content = JsonContent.Create(new { isVeg = true }) };
        req.Headers.Add("X-Test-Auth", $"RestaurantOwner:{Owner1}:{Meghana}");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(req)).StatusCode);
    }
}
