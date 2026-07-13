using System.Net;
using System.Net.Http.Headers;

namespace Tadka.Restaurant.Api.Tests;

/// <summary>
/// ADR-048: conditional GET (ETag / 304) lives on Restaurant.Api after the Day-12 extract.
/// Re-homed from Tadka.Api.Tests placeholder.
/// </summary>
public class ETagIntegrationTests(RestaurantApiFactory factory) : IClassFixture<RestaurantApiFactory>
{
    private readonly RestaurantApiFactory _factory = factory;
    private static readonly Guid Meghana = new("a1b2c3d4-0001-4000-8000-000000000001");

    [Fact]
    public async Task Menu_get_returns_etag_and_if_none_match_yields_304()
    {
        var client = _factory.CreateClient();

        var first = await client.GetAsync($"/api/v1/restaurants/{Meghana}/menu");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.ETag is not null, "Expected ETag on menu GET");
        var etag = first.Headers.ETag!.Tag;
        Assert.False(string.IsNullOrWhiteSpace(etag));

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/restaurants/{Meghana}/menu");
        req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var second = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Restaurant_list_returns_etag()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/restaurants");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.ETag is not null);
    }
}
