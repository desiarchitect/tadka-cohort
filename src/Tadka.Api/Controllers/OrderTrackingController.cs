using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using Tadka.Api.Data.Repositories;
using Tadka.Api.Infrastructure.Realtime;

namespace Tadka.Api.Controllers;

[ApiController]
[Route("api/v1/orders")]
public class OrderTrackingController(IOrderTrackingBus bus, IOrderRepository orders) : ControllerBase
{
    private readonly IOrderTrackingBus _bus = bus;
    private readonly IOrderRepository _orders = orders;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Live order tracking via Server-Sent Events (ADR-020). Streams status changes pushed over the
    /// Redis pub/sub backplane until the client disconnects. One-way (server→client), plain HTTP.
    /// </summary>
    [HttpGet("{id:guid}/events")]
    public async Task GetEvents(Guid id, CancellationToken ct)
    {
        if (!_bus.IsEnabled)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await Response.WriteAsync("Live tracking requires Redis (ADR-020).", ct);
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        // Bridge the pub/sub callback to the SSE write loop via an in-memory channel.
        var queue = Channel.CreateUnbounded<OrderTrackingEvent>();
        await using var subscription = await _bus.SubscribeAsync(
            id, e => { queue.Writer.TryWrite(e); return Task.CompletedTask; }, ct);

        // Send the current status immediately, so a subscriber that joins between changes isn't blank.
        var order = await _orders.GetByIdAsync(id);
        if (order is not null)
            await WriteEventAsync(new OrderTrackingEvent(id, order.Status.ToString(), $"Current status: {order.Status}.", DateTime.UtcNow), ct);

        try
        {
            await foreach (var trackingEvent in queue.Reader.ReadAllAsync(ct))
                await WriteEventAsync(trackingEvent, ct);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal end of an SSE stream.
        }
    }

    private async Task WriteEventAsync(OrderTrackingEvent trackingEvent, CancellationToken ct)
    {
        await Response.WriteAsync($"event: {trackingEvent.Status}\ndata: {JsonSerializer.Serialize(trackingEvent, Json)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
