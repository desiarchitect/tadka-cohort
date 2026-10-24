---
paths:
  - "src/Tadka.Api/**/*.cs"
  - "tests/Tadka.Api.Tests/**/*.cs"
---

# Orders service rules (loads when you touch `Tadka.Api` files)

This is a **path-scoped rule**: Claude Code loads it only when it reads or edits a matching file, so the main `CLAUDE.md` stays short.

## Order lifecycle

`Created` then `Confirmed` then `Preparing` then `ReadyForPickup` then `PickedUp` then `Delivered`. `Cancelled` is allowed only from `Created` or `Confirmed`. `Delivered` and `Cancelled` are final. `Refunded` exists in the enum but nothing moves into it yet. The single source of truth is `src/Tadka.Api/Domain/Orders/OrderStateMachine.cs`. An illegal transition is a **422**.

## Pricing (ADR-037)

The price is always calculated on the server, from the local read model `ordering.menu_replica` through `IRestaurantPricingSource`. The client never sends a price. Never call the Restaurant service on the order-placement path.

## Events (ADR-028)

Stage the event on the same `DbContext` as the order, then save once. The order and its event commit together or not at all:

```csharp
_orderRepository.Add(order);
_db.Set<OutboxMessage>().Add(new OutboxMessage
{
    Topic = Topics.OrderPlaced,
    Key = order.Id.ToString(),
    Payload = JsonSerializer.Serialize(placed),
    TraceParent = Tadka.Telemetry.TadkaTrace.CurrentTraceParent()   // ADR-041
});
await _orderRepository.SaveChangesAsync();
```

The relay claims rows with `FOR UPDATE SKIP LOCKED`, so several pods never publish the same row twice. Consumers record a message id in the inbox table so a redelivery is skipped.

## Idempotency and concurrency (ADR-011, ADR-012)

- `POST /api/v1/orders` honours the `Idempotency-Key` header. The key is stored in the same transaction as the order. A replay returns **200** with the original order. A race on the unique key returns the winner, not a 500.
- Concurrent changes to an order are detected with the `xmin` column and answered with **409**.

## Status codes

400 malformed input, 401 or 403 auth, 404 missing, 422 a business rule said no, 409 you lost a race. Errors are Problem Details (`Problem(...)` in controllers).
