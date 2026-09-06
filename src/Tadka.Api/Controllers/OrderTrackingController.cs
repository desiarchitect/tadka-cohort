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
    ///
    /// Day 6 reconnect-replay beat: if the client sends `Last-Event-ID` (standard SSE reconnect
    /// header — browsers set it automatically; our fetch-based client sets it explicitly), missed
    /// events still in the short recent-history buffer are replayed before live events resume.
    /// This is a BUFFER, not durability — events older than the buffer's capacity/TTL are gone for
    /// good. That limit is the point: true no-loss delivery is the transactional outbox (Week 5),
    /// not a bigger buffer.
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

        // Subscribe FIRST, so any event published between "read the replay buffer" and "start
        // draining the live queue" is captured (in the queue) rather than silently missed.
        var queue = Channel.CreateUnbounded<SequencedTrackingEvent>();
        IAsyncDisposable subscription;
        try
        {
            subscription = await _bus.SubscribeAsync(
                id, e => { queue.Writer.TryWrite(e); return Task.CompletedTask; }, ct);
        }
        catch (StackExchange.Redis.RedisException)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await Response.WriteAsync("Live tracking requires Redis (ADR-020).", ct);
            return;
        }

        await using (subscription)
        {
            var lastEventId = Request.Headers["Last-Event-ID"].FirstOrDefault();
            var replayedThrough = 0L;

            if (long.TryParse(lastEventId, out var sinceSeq))
            {
                var missed = await _bus.GetEventsSinceAsync(id, sinceSeq, ct);
                foreach (var sequenced in missed)
                {
                    await WriteEventAsync(sequenced, ct);
                    replayedThrough = sequenced.Seq;
                }
            }
            else
            {
                // No reconnect in progress — send the current status immediately so a fresh
                // subscriber isn't blank while waiting for the next transition (seq 0: not
                // replayable, always resent on a fresh connect).
                var order = await _orders.GetByIdAsync(id);
                if (order is not null)
                    await WriteEventAsync(new SequencedTrackingEvent(0,
                        new OrderTrackingEvent(id, order.Status.ToString(), $"Current status: {order.Status}.", DateTime.UtcNow)), ct);
            }

            try
            {
                await foreach (var sequenced in queue.Reader.ReadAllAsync(ct))
                {
                    // The live subscription started before the replay read, so an event already
                    // delivered by replay can also arrive here — skip anything not newer.
                    if (sequenced.Seq <= replayedThrough) continue;
                    await WriteEventAsync(sequenced, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — normal end of an SSE stream.
            }
        }
    }

    private async Task WriteEventAsync(SequencedTrackingEvent sequenced, CancellationToken ct)
    {
        var trackingEvent = sequenced.Event;
        var idLine = sequenced.Seq > 0 ? $"id: {sequenced.Seq}\n" : "";
        await Response.WriteAsync(
            $"{idLine}event: {trackingEvent.Status}\ndata: {JsonSerializer.Serialize(trackingEvent, Json)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
