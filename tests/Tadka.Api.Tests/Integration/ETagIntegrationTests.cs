namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Day 6 ETag demos (ADR-048) live on <c>Tadka.Restaurant.Api</c> after Day-12 extract.
/// Real assertions: <c>Tadka.Restaurant.Api.Tests.ETagIntegrationTests</c>.
/// </summary>
public class ETagIntegrationTests
{
    [Fact]
    public void ETag_tests_rehomed_to_Restaurant_Api_Tests() =>
        Assert.True(true, "See tests/Tadka.Restaurant.Api.Tests/ETagIntegrationTests.cs");
}
