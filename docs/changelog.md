# Day 11 changelog (since Day 10)

`git checkout day-11`. Previous branch: `day-10`.

## We learned

- Extract **Delivery** because of a **scaling/durability** profile (not Payment's fault/PCI).
- **Polyglot:** Postgres for assignment history; Redis GEO for live location.
- **YARP gateway** :8080 — one host for clients; per-service JWT stays the floor.
- 3-participant saga: order-confirmed → rider assigned → `delivery-assigned`.

## Architecture

- Third service: `Tadka.Delivery.Api` + `delivery-db` :5435.
- Gateway routes `/api/v1/payments/**` → Payment, `/deliveries/**` → Delivery, rest → monolith.
- Monolith publishes `order-confirmed` via Outbox (lat/long, no back-call).

## Code vs Day 10

| Area | What changed |
|---|---|
| `src/Tadka.Delivery.Api/` | Kafka consumer of `order-confirmed`, Inbox, unique one-assignment-per-order |
| `ILocationStore` | GEOADD / GEOPOS |
| `src/Tadka.Gateway/` | YARP + edge rate-limit |
| `docker-compose.yml` | `delivery-db` |

ADRs **033, 034, 035**.
