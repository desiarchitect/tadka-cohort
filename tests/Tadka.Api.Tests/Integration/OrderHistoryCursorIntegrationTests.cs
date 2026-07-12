using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tadka.Api.Contracts;
using Tadka.Api.Contracts.Orders;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Day 5, Beat 4 (ADR-046): keyset ("cursor") pagination on order history — the fix for
/// OFFSET's O(depth) cost. Verifies pagination correctness (no gaps, no duplicates, no
/// overlap across pages, terminates), not the millisecond numbers the break kit captures live.
/// </summary>
public class OrderHistoryCursorIntegrationTests(TadkaApiFactory factory) : IClassFixture<TadkaApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Cursor_pages_cover_every_order_exactly_once_no_gaps_no_duplicates()
    {
        var customerId = Guid.NewGuid();
        const int totalOrders = 23; // deliberately not a multiple of pageSize
        var placedIds = new List<Guid>();
        for (var i = 0; i < totalOrders; i++)
            placedIds.Add(await PlaceOrderAsync(customerId));

        var seen = new List<Guid>();
        string? cursor = null;
        var safetyLimit = 20; // pages; guards against an infinite loop if NextCursor never goes null

        do
        {
            var url = $"/api/v1/orders/history?customerId={customerId}&pageSize=5"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var response = await _client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var page = await response.Content.ReadFromJsonAsync<CursorPageResponse<OrderResponse>>();
            Assert.NotNull(page);
            Assert.True(page!.Items.Count <= 5);

            seen.AddRange(page.Items.Select(o => o.Id));
            cursor = page.NextCursor;
        } while (cursor is not null && --safetyLimit > 0);

        Assert.True(safetyLimit > 0, "pagination did not terminate within the expected number of pages");
        Assert.Equal(totalOrders, seen.Count);
        Assert.Equal(placedIds.OrderBy(x => x).ToList(), seen.OrderBy(x => x).ToList()); // exact set match
        Assert.Equal(seen.Count, seen.Distinct().Count()); // no duplicates across page boundaries
    }

    [Fact]
    public async Task Cursor_page_is_ordered_newest_first_matching_offset_pagination()
    {
        var customerId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            await PlaceOrderAsync(customerId);

        var response = await _client.GetAsync($"/api/v1/orders/history?customerId={customerId}&pageSize=10");
        var page = await response.Content.ReadFromJsonAsync<CursorPageResponse<OrderResponse>>();

        Assert.NotNull(page);
        Assert.Equal(5, page!.Items.Count);
        Assert.Null(page.NextCursor); // fewer than pageSize -> no next page
        var timestamps = page.Items.Select(o => o.CreatedAt).ToList();
        Assert.Equal(timestamps.OrderByDescending(t => t).ToList(), timestamps); // newest first
    }

    [Fact]
    public async Task Invalid_cursor_returns_400_not_500()
    {
        var response = await _client.GetAsync($"/api/v1/orders/history?customerId={Guid.NewGuid()}&cursor=not-a-real-cursor");

        // A malformed cursor is a bad REQUEST, not a server fault - the exception middleware
        // maps ArgumentException the same way it maps FluentValidation failures.
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity,
            $"expected a 4xx client error for a malformed cursor, got {response.StatusCode}");
    }

    // --- helpers -------------------------------------------------------------

    private async Task<Guid> PlaceOrderAsync(Guid customerId)
    {
        var (restaurantId, menuItemId) = await DiscoverSeedAsync();
        var request = new CreateOrderRequest(
            CustomerId: customerId,
            RestaurantId: restaurantId,
            Items: [new CreateOrderItemRequest(menuItemId, Quantity: 1, SpecialInstructions: null)],
            DeliveryAddress: new OrderAddressRequest(
                "12 MG Road", "Indiranagar", "Bengaluru", "560038", 12.9716, 77.5946));

        var response = await _client.PostAsJsonAsync("/api/v1/orders", request);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>();
        return order!.Id;
    }

    private async Task<(Guid RestaurantId, Guid MenuItemId)> DiscoverSeedAsync()
    {
        using var listDoc = JsonDocument.Parse(await _client.GetStringAsync("/api/v1/restaurants"));
        var restaurantId = Unwrap(listDoc.RootElement).EnumerateArray().First().GetProperty("id").GetGuid();

        using var menuDoc = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/restaurants/{restaurantId}/menu"));
        foreach (var item in Unwrap(menuDoc.RootElement).EnumerateArray())
        {
            var available = !item.TryGetProperty("isAvailable", out var a) || a.GetBoolean();
            if (available)
                return (restaurantId, item.GetProperty("id").GetGuid());
        }
        throw new InvalidOperationException("No available menu item found.");
    }

    private static JsonElement Unwrap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root
        : root.TryGetProperty("items", out var items) ? items
        : root;
}
