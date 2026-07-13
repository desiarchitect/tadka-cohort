namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Day 6 ETag demos (ADR-048) now live on <c>Tadka.Restaurant.Api</c> (restaurants extracted Day 12).
/// Kept as a placeholder so the break-kit still documents the lesson; re-home under Restaurant.Api.Tests.
/// </summary>
public class ETagIntegrationTests
{
    [Fact(Skip = "Restaurants extracted to Restaurant.Api; ETag applied on Restaurant.Api RestaurantsController.")]
    public void Placeholder() => Assert.True(true);
}
