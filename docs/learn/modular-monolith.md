# Modular monolith + CQRS — what it *looks* like (Day 7)

ADR-022 is the **seam**. ADR-023 is **CQRS-lite**. We did **not** rename the repo into textbook folders. This page is the **tree at work** vs **what Tadka actually has**.

## 1. Modular monolith — textbook

Each module owns **its** domain, data, and application code. Modules talk through **events** (a contract neither side owns). No `using OtherModule`.

```
src/Tadka.Api/
  Modules/
    Ordering/
      Domain/          # Order, OrderItem, state machine
      Application/     # commands, handlers
      Infrastructure/  # Ordering DbContext
      Api/             # OrdersController
    Payments/
      Domain/          # Payment entity
      Application/     # charge use-case, Channel
      Infrastructure/  # PaymentDbContext, fake gateway, Polly
      Api/             # none today — Payment is not on HTTP
    Restaurants/ ...   # not carved — no failure earned this
    Delivery/ ...
    Identity/ ...
  Domain/Orders/Events/          # OrderPlaced — Ordering owns it; Payment handles it
  Domain/Common/Events/          # PaymentCompleted / Failed — Payment owns them; Ordering handles them
```

Java: `com.tadka.ordering` / `com.tadka.payments`. Node: `src/modules/ordering`. Go: `internal/ordering`. Same tree.

## 2. Modular monolith — Tadka Day 7 (honest)

We carved **behavior**, not a pretty slice. Payment **runtime** is under `Modules/Payments/`. The **entity and DbContext** still sit in the old layered folders. Other domains stay layered. ADR-022: *only the boundary the brownout earned*.

```
src/Tadka.Api/
  Modules/Payments/          # gateway, Channel, processor, MediatR handler, options
  Domain/Payments/Payment.cs # entity — still here
  Data/PaymentDbContext.cs   # own schema + own __EFMigrationsHistory
  Data/Configurations/PaymentConfiguration.cs
  Domain/Orders/             # NOT a module folder
  Controllers/OrdersController.cs
  Domain/Orders/Events/OrderPlacedEvent.cs
  Domain/Common/Events/PaymentEvents.cs   # PaymentCompleted / Failed — not one shared folder
```

| Rule | Day 7 |
|---|---|
| Own data | `payment` schema, own migration history |
| Own process? | **No** — one `Tadka.Api` (Day 8 extracts) |
| Compile coupling | Ordering has **zero** `using` of Payment types |
| Proof | grep Orders for `Payment` → empty. Build-failing test is Day 8 |

**Not today:** moving `Payment.cs` into `Modules/Payments/`; empty `Modules/Ordering/`; extra assemblies.

## 3. CQRS — textbook

**Command** = change something (POST, handler, write model). **Query** = ask something (GET, read model). Full CQRS often **splits the project** (and sometimes the database):

```
src/
  Ordering.Contracts/     # commands, queries, events
  Ordering.Command/       # write model, aggregates, command handlers
  Ordering.Query/         # read models, query handlers, projections
  Ordering.Api/           # POST = commands, GET = queries
```

Two databases is optional (write store vs denormalised read store). Java: Axon / Spring command vs query packages. Node: `commands/` vs `queries/`. Same idea.

## 4. CQRS-lite — Tadka Day 7 (honest)

ADR-023: **placing an order does not wait for the bank.** That is a write-split, not two order databases and not `Commands/` vs `Queries/` folders.

```
POST /orders                 command: persist Order Created, publish OrderPlaced, return 201
PaymentProcessor             another write: charge → PaymentCompleted / Failed
GET  /orders/{id}            still the SAME Order row (no separate read model)
SSE  /orders/{id}/events     notification of an already-committed write
```

| Full CQRS | Tadka today |
|---|---|
| `Commands/` + `Queries/` projects | One `Tadka.Api` |
| Separate read store | Same `ordering.orders` row |
| Projection pipeline | SSE + GET the write model |
| First real local **read model** | **Day 12** menu replica, not today |

Spoken line: *Modular monolith is how seams look. CQRS is how writes split. We have a seam and a lite write-split. We do not have two order databases.*
