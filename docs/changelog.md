# Day 8 changelog (since Day 7)

`git checkout day-08`. Previous branch: `day-07`.

## We learned

- Extract Payment because of **fault / PCI / data isolation**, not because it was slow (slow was fixed on Day 7 in one process).
- **Database per service:** Payment gets its own Postgres (`payment-db` :5434) and its own migration history.
- Sync **HTTP** between services is the first bridge (why HTTP before Kafka). If Payment is down, the order stays pending — **temporal coupling**. That wound is Day 9.

## Architecture

- Two processes: `Tadka.Api` + **`Tadka.Payment.Api`** (:5240).
- Monolith calls Payment via typed `IPaymentClient` + Day-7 Polly pipeline.
- Gateway/service/resilience **moved**, not rewritten.

## Code vs Day 7

| Area | What changed |
|---|---|
| `src/Tadka.Payment.Api/` | New host; Payment module **moved** here |
| `docker-compose.yml` | `payment-db` :5434 |
| Monolith | `IPaymentClient` HTTP; in-process Payment types gone from the order path |
| Tests | Monolith + Payment suites |

ADR **024, 025, 026**.
