using System.Text.Json;
using Tadka.Api.Domain.Orders;

namespace Tadka.Api.Infrastructure.RestaurantReadModel;

/// <summary>
/// The "wrong way", kept for teaching (ADR-037): price the order by calling the Restaurant service
/// SYNCHRONOUSLY over HTTP on the order hot path. Always fresh — but it re-introduces the Day-8 temporal
/// coupling: Restaurant down/slow → this throws → <c>POST /orders</c> fails. Enabled by
/// <c>Ordering:RestaurantReadMode = SyncHttp</c> so the break is reproducible next to the local-replica fix.
/// </summary>
public sealed class HttpRestaurantPricingSource(HttpClient http) : IRestaurantPricingSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RestaurantPricing?> GetAsync(Guid restaurantId, CancellationToken ct = default)
    {
        // If Restaurant is down, this throws (HttpRequestException) and the order fails — the coupling.
        var menuResp = await http.GetAsync($"/api/v1/restaurants/{restaurantId}/menu", ct);
        if (menuResp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        menuResp.EnsureSuccessStatusCode();

        using var menuDoc = JsonDocument.Parse(await menuResp.Content.ReadAsStringAsync(ct));
        var menu = menuDoc.RootElement.EnumerateArray().Select(i => new MenuItemPricing(
            i.GetProperty("id").GetGuid(),
            i.GetProperty("name").GetString() ?? "",
            i.GetProperty("price").GetProperty("amount").GetDecimal(),
            i.GetProperty("price").GetProperty("currency").GetString() ?? "INR",
            i.GetProperty("isAvailable").GetBoolean())).ToList();

        return new RestaurantPricing(restaurantId, IsActive: true, menu);
    }
}
