# Sharding Demo — see why "just hash % N" is a trap

A **standalone** teaching sample. It is **not** part of `Tadka.slnx`, references nothing in `src/`, and is never touched by `dotnet test` — so it can't affect the app's build or test count. It's a pure simulation of shard-key routing.

> **Why this exists.** The cohort *defers* sharding (ADR-017) because 1 lakh orders/day fits one Postgres — that's the honest call. But "earn it, don't assume it" cuts both ways: the **skill** of shard-key design, consistent hashing, virtual nodes, and rebalancing cost is an architect must-have, and you don't feel it from a slide. This demo lets you *see* it, failure-first. It pairs with the interview-pack [`sharding-deep-dive.md`](../../../desiarchitect-website/cohort-prep/interview-pack/sharding-deep-dive.md).

## Run it

```bash
dotnet run --project samples/sharding-demo -c Release
```

No dependencies, deterministic (fixed inputs → same numbers every run). Routes 1,00,000 order keys across shards four ways.

## What it shows (captured output)

| Demo | Setup | Result | The lesson |
|------|-------|--------|------------|
| **1. Naive `hash % N`** | 100k keys, 4 shards | even (1.01× spread) — but growing **4→5 shards moves 80.0% of keys** | modulo rebalances almost *everything* when N changes → never reshard naively |
| **2. Consistent hashing, NO vnodes** | 4 shards, 1 ring point each | **12.86× spread** (one shard 2.8%, another 36.3%) | too few ring points = lumpy slices; one shard carries far more than its share |
| **3. Consistent hashing + vnodes** | 4 shards, **150 vnodes** each | **1.08× spread** (even); dropping a shard moves **only 24.5%** | vnodes smooth the load *and* make a node add/remove move only ~1/N keys — the other shards are untouched |
| **4. Skewed shard key** | shard by `city`, Bangalore = 80% of orders | one shard holds **88%** (44× spread) | hashing balances *keys*, not *traffic* — a low-cardinality / skewed shard key makes a **hot shard** no hash can fix |

## The four takeaways

1. **`hash % N` is a resharding bomb** — changing the shard count reshuffles ~80% of your data. That's why production uses consistent hashing, not modulo.
2. **Consistent hashing alone is lumpy** — with only a few ring points per shard, load is uneven (12.86× here).
3. **Virtual nodes fix both problems** — ~150 points per shard gives near-even load (1.08×) *and* a cheap rebalance: add/remove a shard and only ~1/N keys move (24.5%), not 80%.
4. **The shard *key* matters more than the hash** — shard by a **high-cardinality, evenly-distributed** key (e.g. `order_id` / `customer_id`), never something skewed like `city`, or one shard becomes a hot spot. Mitigate genuine hot keys separately (replication, an L1 cache — the Day-6 single-flight lock).

## How this maps to Tadka

Tadka stays single-Postgres at 1 lakh/day — correctly. When Day-16's load test finds the single-writer ceiling (~2,500 writes/s ≈ ~1 crore orders/day), *this* is the next move: shard the orders table by a high-cardinality key, consistent-hashing + vnodes so the first reshard doesn't move the whole dataset. Postgres analogs: Citus, Vitess, or app-level routing. Full reasoning — shard-key choice, cross-shard query pain, online resharding — in the [sharding deep-dive](../../../desiarchitect-website/cohort-prep/interview-pack/sharding-deep-dive.md).

## Related failure-first demos

Same pedagogy as [`toydemo/`](../../toydemo/README.md) (12 breadth/realtime toys) and the Tadka day break-kits — indexed in [cohort-prep/DEMOS.md](../../../desiarchitect-website/cohort-prep/DEMOS.md). Hot-key mitigation referenced in takeaway #4: [`hot-key-stampede-toy`](../../toydemo/day-06-cache-realtime/hot-key-stampede-toy/RUN-AND-TEST.md).
