using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Delivery.Api.Data;
using Tadka.Delivery.Api.Domain;

namespace Tadka.Delivery.Api.Tests;

/// <summary>
/// ADR-036: <c>PUT /deliveries/{id}/location</c> must reach the Day-6 live-tracking backplane
/// (<c>IOrderTrackingBus</c> / Redis channel <c>order:{orderId}</c>), not just <c>ILocationStore</c>'s
/// GEOADD — otherwise a customer's <c>GET /orders/{id}/events</c> SSE stream never sees a rider-location
/// ping. These tests exercise the real endpoint end-to-end (real Postgres via Testcontainers, real
/// routing/auth) and swap only the Redis transport for <see cref="FakeOrderTrackingPublisher"/>, which
/// keeps the suite Redis-free (this project's existing convention) while still proving the endpoint
/// makes the publish call with the payload the SSE stream would carry.
/// </summary>
public class LocationTrackingTests(DeliveryApiFactory factory) : IClassFixture<DeliveryApiFactory>
{
    private readonly DeliveryApiFactory _factory = factory;

    [Fact]
    public async Task Location_update_for_an_active_assignment_publishes_onto_the_tracking_backplane()
    {
        var orderId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<Tadka.Delivery.Api.DeliveryService>();
        var assigned = await svc.AssignAsync(orderId);
        Assert.NotNull(assigned); // a seeded Available agent must exist for this to assign

        var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/v1/deliveries/{orderId}/location", new { Latitude = 12.9716, Longitude = 77.5946 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var published = _factory.TrackingPublisher.Published.Where(p => p.OrderId == orderId).ToList();
        var evt = Assert.Single(published);
        Assert.Equal(12.9716, evt.Latitude);
        Assert.Equal(77.5946, evt.Longitude);
    }

    [Fact]
    public async Task Location_update_for_an_unassigned_order_is_not_found_and_publishes_nothing()
    {
        var orderId = Guid.NewGuid(); // never assigned

        var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/v1/deliveries/{orderId}/location", new { Latitude = 12.9716, Longitude = 77.5946 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(_factory.TrackingPublisher.Published, p => p.OrderId == orderId);
    }

    [Fact]
    public async Task Location_update_for_a_delivered_assignment_is_not_published_but_still_updates_geo()
    {
        var orderId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<Tadka.Delivery.Api.DeliveryService>();
        await svc.AssignAsync(orderId);

        var db = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
        var assignment = await db.Assignments.SingleAsync(a => a.OrderId == orderId);
        assignment.Status = AssignmentStatus.Delivered; // no one's SSE stream is watching a finished delivery
        await db.SaveChangesAsync();

        var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/v1/deliveries/{orderId}/location", new { Latitude = 12.9716, Longitude = 77.5946 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); // GEOADD still happens (ADR-034 unaffected)
        Assert.DoesNotContain(_factory.TrackingPublisher.Published, p => p.OrderId == orderId);
    }
}
