# Sharded Orders Demo — real Postgres-backed sharding, with simplified resharding mechanics

A **standalone** teaching sample. Not part of `Tadka.slnx`, references nothing in `src/`,
never touched by `dotnet test`. Unlike this repo's earlier `sharding-demo` (a pure
in-memory hash simulation this project replaces), everything here runs against **four
real, separate Postgres databases** — real `INSERT` routing, a real cross-shard
scatter-gather query, and a real "add a shard live, watch a lookup break, then migrate
the data to fix it" sequence.

> **Read this label carefully: "real Postgres-backed sharding, with simplified
> resharding mechanics" — not "this is exactly how production sharding works."**
> See [Framing and honest limits](#framing-and-honest-limits-read-this) below before you
> teach this as more than it is.

## Why this exists

`ADR-017` (partitioning/sharding deferred) is the right call at Tadka's scale — but the
*skill* (shard-key design, routing, cross-shard reads, what actually happens when you
add capacity) is an architect must-have you don't feel from a slide. This demo lets you
see it, failure-first, against real infrastructure. Pairs with the interview-pack
[`sharding-deep-dive.md`](../../../desiarchitect-website/cohort-prep/interview-pack/sharding-deep-dive.md).

## Run it

```bash
docker compose -f samples/sharded-orders-demo/docker-compose.yml up -d
dotnet run --project samples/sharded-orders-demo -c Release -- init
dotnet run --project samples/sharded-orders-demo -c Release -- seed --count 20000 --shard-key order_id
dotnet run --project samples/sharded-orders-demo -c Release -- topology
```

## Commands

| Command | Does | Idempotent? |
|---|---|---|
| `topology` | Routing mode, shard key, live `SELECT count(*)` per shard | read-only |
| `init` | Creates the `orders` schema on every registered shard | yes (`IF NOT EXISTS`) |
| `seed --count 20000 --shard-key order_id\|customer_id\|restaurant_id\|city [--mode naive\|consistent-vnodes] [--vnodes 150]` | Generates + really inserts N synthetic orders; prints per-shard distribution + a skew warning | yes — reruns insert 0 new rows |
| `insert --customer-id X --city Y --amount Z [--restaurant-id R]` | Inserts one live order, prints the routing decision | new row each call, by design |
| `get --order-id X [--verify]` | Routes by `order_id` and fetches from that one shard; `--verify` scans every shard to show where the row actually lives | read-only |
| `report [--top N]` | Scatter-gather: fans out to every shard concurrently, merges client-side | read-only |
| `add-shard --id 5` | Verifies the shard is reachable, creates its schema, registers it — **routing is not changed** | yes — no-ops if already registered |
| `reshard plan --to consistent-vnodes [--vnodes 150]` | Dry run: per-row ring-owner vs. actual shard, prints a move/stay table, writes nothing | read-only, safe anytime |
| `reshard apply --to consistent-vnodes [--vnodes 150] [--resume]` | Executes the plan for real, then switches routing mode | resumable, self-healing |
| `reset` | Truncates `orders` on every shard, clears local state back to defaults | yes |

`docker compose -f samples/sharded-orders-demo/docker-compose.yml --profile shard5 up -d shard-db-5`
brings up the 5th shard — it's down by default so "adding a shard" is a real, live event.

## Shard-key selection, four ways (captured, real row counts)

Same 20,000 synthetic orders, reseeded with a different `--shard-key` each time (`reset` between runs):

| Shard key | Max shard share | What it shows |
|---|---:|---|
| `order_id` | 25.3% | High-cardinality, evenly distributed — the right choice |
| `customer_id` | 25.4% | Also high-cardinality, also even |
| `restaurant_id` | 33.1% | ~200 synthetic restaurants, Zipf-skewed (a few popular ones take more) — a real, moderate hot spot |
| `city` | 80.2% | 5 cities, Bangalore = 80% of orders — hashing balances *keys*, not *traffic*; one shard becomes a hot spot no hash can fix |

`restaurant_id` is the interesting middle case: worse than a uniform key, nowhere near as
bad as `city`. Real shard-key choices usually land somewhere on this spectrum, not at
either extreme.

## The centerpiece: two real scenarios, not one

Growing from 4 shards to 5 tells two genuinely different stories depending on what
you're growing *from*. Both are demonstrated for real, against real data.

### Scenario 1 — naive `hash % N`: the break

```bash
dotnet run --project samples/sharded-orders-demo -c Release -- reset
dotnet run --project samples/sharded-orders-demo -c Release -- seed --count 20000 --shard-key order_id
dotnet run --project samples/sharded-orders-demo -c Release -- get --order-id order-0012345
#   hash/route(order-0012345) via naive hash % N (N = 4) -> shard 1
#   SELECT ... FROM shard 1 -> HIT (1 row)

docker compose -f samples/sharded-orders-demo/docker-compose.yml --profile shard5 up -d shard-db-5
dotnet run --project samples/sharded-orders-demo -c Release -- add-shard --id 5
#   Measured impact of hash % 4 -> hash % 5 across all 20,000 seeded keys
#   (recomputed live, not assumed): 15,987 keys now hash to a DIFFERENT shard than
#   the one they're actually stored on = 79.9% reshuffled by just changing the modulus.

dotnet run --project samples/sharded-orders-demo -c Release -- get --order-id order-0012345
#   hash/route(order-0012345) via naive hash % N (N = 5) -> shard 5
#   SELECT ... FROM shard 5 -> MISS (0 rows)

dotnet run --project samples/sharded-orders-demo -c Release -- get --order-id order-0012345 --verify
#   Scanning all 5 shards... shard 1: FOUND. Everywhere else: not found.
#   This is a REAL wrong-shard miss, not a bug in the demo.
```

**Captured, live:** 79.9% of keys hash to a different shard purely from changing the
modulus — nothing physically moved, so most lookups now point at the wrong place. A
`report` right after this still shows the correct total (20,000) — a genuinely useful
nuance: **aggregate scatter-gather queries can mask a per-key routing break.**

### Scenario 2 — consistent hashing + vnodes: grown correctly from the start

**Important, and only obvious once you measure it: you cannot cheaply *convert* an
existing naive-hash dataset onto a consistent-hash ring.** Doing so changes two things
at once (the algorithm AND the shard count) and moves about as much data as a full
rehash — empirically, ~80%, not the ~20% consistent hashing promises. The cheap-resize
property is a benefit of *growing* a cluster that's **already** on consistent hashing,
not a way to retroactively fix a naively-sharded one. This demo proves that distinction
rather than asserting it:

```bash
dotnet run --project samples/sharded-orders-demo -c Release -- reset
dotnet run --project samples/sharded-orders-demo -c Release -- seed --count 20000 --shard-key order_id --mode consistent-vnodes --vnodes 150
dotnet run --project samples/sharded-orders-demo -c Release -- add-shard --id 5
#   (shard5 container already up from Scenario 1, or bring it up again)

dotnet run --project samples/sharded-orders-demo -c Release -- reshard plan --to consistent-vnodes --vnodes 150
#   Reshard plan: consistent-hash + 150 vnodes/shard, 4 shards (physical placement)
#     -> consistent-hash + 150 vnodes/shard, 5 shards
#   Rows to migrate: 3,453 of 20,000 (17.3% -- roughly one shard's share of the keyspace)
#   Contrast: growing FROM naive hashing (Scenario 1) moved 79.9% of the same-sized dataset.

dotnet run --project samples/sharded-orders-demo -c Release -- reshard apply --to consistent-vnodes --vnodes 150
#   Migrating 3,453 rows...
#     shard 1 -> shard 5: 1,148 rows migrated
#     shard 2 -> shard 5: 736 rows migrated
#     shard 3 -> shard 5: 685 rows migrated
#     shard 4 -> shard 5: 884 rows migrated
#   Migration complete: 3,453 rows moved.
#   Total: 20,000 -> 20,000 (no rows lost, no duplicates)

dotnet run --project samples/sharded-orders-demo -c Release -- get --order-id order-0000001
#   Ring route: shard 3 -> HIT (1 row). Correct, whether or not this key moved.
```

**Captured, live: 17.3% of rows moved** — close to the "one shard's fifth" a well-balanced
5-shard ring predicts, and nowhere near Scenario 1's 79.9%. This is the number worth
teaching: not "consistent hashing always moves ~20%" (it doesn't — it depends on ring
topology, vnode count, and key distribution, which is why the demo measures it live
every time, never asserts it), but "growing an already-consistent-hash cluster costs
roughly its share of the new capacity; converting an existing naive one costs almost a
full rehash."

## Migration safety (crash-safe, no double-write or data loss)

A local JSONL ledger (`.state/migration-log.jsonl`) records `InsertedOnTarget` then
`Completed` per row; **the row is only deleted from its source shard after the target
insert is confirmed.** Combined with `ON CONFLICT DO NOTHING` on every insert, this
means a `reshard apply` you interrupt mid-flight is safe to just run again — because
`reshard plan`/`apply` always recomputes the move list from the CURRENT physical state
of the data, not from history, an already-fully-migrated row simply won't appear in the
next plan at all. `--resume` reads the ledger to skip re-verifying rows already known
complete; it isn't the only thing making a retry safe, but it's what keeps a retry fast
and gives you an audit trail.

Verified live by killing an in-flight migration and rerunning it: the `report` total
briefly read one row high immediately after the kill (a transient duplicate — the row
existed on both its old and new shard for a moment) and self-corrected to the true total
once the resume finished deleting it from the source. No row was ever lost. This is the
demo's honest at-least-once-in-the-middle, exactly-once-at-the-end story, not hidden.

## Framing and honest limits (read this)

Three places this demo is deliberately simpler than a real production sharding platform:

**1. Consistent hashing is *one* strategy, not *the* answer.**

| Approach | Rebalance cost on resize | Notes |
|---|---|---|
| Naive `hash % N` (rejected) | ~80% of all keys move | Never do this in production |
| Consistent hashing + vnodes (this demo's pick) | ~1/N of keys, decentralized | No directory to maintain; the pick here |
| Range-based partitioning (e.g. Vitess-style keyspace ranges) | Split/merge a range, easy range scans | A range-owner directory tracks who owns what |
| Directory-based | Most flexible, arbitrary key→shard mapping | The directory itself becomes a dependency + bottleneck |

Real systems commonly use range-based partitioning with explicit split/merge workflows
instead of a hash ring — Vitess is the well-documented example. Neither is universally
"more real" than the other; they're different trade-offs for the same problem.

**2. The migrated-row percentage is measured, never a fixed law.** This demo's own
output always reports what it actually measured on that run (see the two scenarios
above) — never a hardcoded "~20%."

**3. The biggest simplification: this migration assumes no concurrent writes during the
copy window.** `copy → verify → delete` is a single pass; there's no continuous
replication keeping the target caught up while the source keeps serving live traffic.
Vitess's documented resharding workflow is closer to: create target shards → start copy
→ **keep the target caught up via continuous change replication** → validate/diff →
**cutover traffic** → cleanup the source — the source keeps serving live writes the
entire time.

> **Failure mode (this demo's real limit):** if an order on the source shard is updated
> between this demo's copy step and its delete step, that update is silently lost — the
> source row (with the update) gets deleted, and the target only ever has the value from
> the moment it was copied.
>
> **Revisit when:** any real deployment. Production resharding needs continuous
> replication + a controlled cutover window instead of a single copy-verify-delete pass —
> this demo deliberately stops short of building that, so the gap becomes a genuine
> discussion question instead of a hidden one: *"what happens to an order that changes
> while it's mid-migration?"*

## Explicitly out of scope

No shard-count auto-discovery/service registry, no production-grade 2PC, no UI, no
concurrent-writer-during-migration simulation, no cross-shard JOINs/secondary
indexes/read replicas, no arbitrary-N support beyond the 4→5 narrative (the ring math is
generic; seeding/reporting are tuned for this story).

## How this maps to Tadka

Tadka stays single-Postgres at 1 lakh orders/day — correctly (ADR-017). When a real
single-writer ceiling is reached (Day 16's load test finds it — roughly a write ceiling
around 2,500 writes/s), *this* is the next move: shard the orders table by a
high-cardinality key, and prefer growing an already-consistent-hash cluster over ever
converting a naive one after the fact — this demo is why. Postgres analogs to a
hand-rolled router like this one: Citus, Vitess (for MySQL, cited above), or
application-level routing exactly like `ShardCatalog`/`Router` here, just hardened.

## Related demos

Same failure-first pedagogy as the tadka `toydemo/` suite and the day break-kits —
indexed in `cohort-prep/DEMOS.md`. Hot-key mitigation (a different problem — one *key*
getting too much traffic, not one *shard*) is a separate concern, covered in the Day-6
cache-aside material and the `hot-key-stampede-toy`.
