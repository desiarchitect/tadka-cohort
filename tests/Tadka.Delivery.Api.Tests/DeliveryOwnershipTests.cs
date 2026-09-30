using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Delivery.Api.Data;

namespace Tadka.Delivery.Api.Tests;

/// <summary>
/// Resource ownership on Delivery's own endpoints (ADR-031). Before this fix both endpoints only required
/// "any valid token": any logged-in customer could read any order's rider and live GPS, or move a rider's
/// dot on someone else's map. The SSE stream in the monolith got this check in d2bb1c5; these endpoints,
/// in a different service, did not inherit it.
/// </summary>
public class DeliveryOwnershipTests(DeliveryApiFactory factory) : IClassFixture<DeliveryApiFactory>
{
    private readonly DeliveryApiFactory _factory = factory;

    private async Task<(Guid OrderId, Guid Customer, Guid RiderUser)> AssignForNewCustomerAsync()
    {
        var orderId = Guid.NewGuid();
        var customer = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DeliveryService>().AssignAsync(orderId, customerId: customer);
        Assert.NotNull(result);
        var db = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == result!.AgentId);
        Assert.NotNull(agent.UserId); // seeded riders are linked to their login ids
        return (orderId, customer, agent.UserId!.Value);
    }

    private static HttpRequestMessage As(HttpMethod method, string url, string testAuth, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Auth", testAuth);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    [Fact]
    public async Task Track_is_readable_by_the_owner_the_assigned_rider_and_admin_but_not_another_customer()
    {
        var (orderId, customer, riderUser) = await AssignForNewCustomerAsync();
        var client = _factory.CreateClient();
        var url = $"/api/v1/deliveries/{orderId}/track";

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Get, url, $"Customer:{customer}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Get, url, $"DeliveryAgent:{riderUser}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Get, url, "Admin"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Get, url, $"Customer:{Guid.NewGuid()}"))).StatusCode);
    }

    [Fact]
    public async Task Only_the_assigned_rider_or_admin_can_move_the_riders_location()
    {
        var (orderId, customer, riderUser) = await AssignForNewCustomerAsync();
        var client = _factory.CreateClient();
        var url = $"/api/v1/deliveries/{orderId}/location";
        var sea = new { Latitude = 12.0, Longitude = 75.0 };

        // The customer who owns the order still can't move the rider, nor can a different rider.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Put, url, $"Customer:{customer}", sea))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Put, url, $"DeliveryAgent:{Guid.NewGuid()}", sea))).StatusCode);
        Assert.DoesNotContain(_factory.TrackingPublisher.Published, p => p.OrderId == orderId);

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(As(HttpMethod.Put, url, $"DeliveryAgent:{riderUser}", new { Latitude = 12.97, Longitude = 77.59 }))).StatusCode);
    }

    [Fact]
    public async Task An_assignment_with_no_customer_on_record_is_not_readable_by_any_customer()
    {
        var orderId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DeliveryService>().AssignAsync(orderId); // no customerId

        var client = _factory.CreateClient();
        var res = await client.SendAsync(As(HttpMethod.Get, $"/api/v1/deliveries/{orderId}/track", $"Customer:{Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
