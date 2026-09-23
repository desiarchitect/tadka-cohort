# Tadka — Cost Model (₹)

> **Modeled, not a real invoice.** The AWS tables below are teaching
> approximations: AWS Mumbai (`ap-south-1`) list prices, ₹ at ~₹83/USD, rounded.
> The *ratios and breaking points* are realistic; the exact rupee is not a quote.
> The point is the **shape of the bill and what moves it**, not three decimal
> places. The AWS stack in `terraform/` is plan-only and never applied. Since
> ADR-064 the cohort *does* deploy live, per session, on Azure: the real bills
> for those sessions go in [Real Azure bills](#real-azure-bills-per-session-adr-064)
> at the end of this page.

Cost is a **Day-1 architecture driver**, not a Day-16 afterthought. Every box on
the architecture diagram is a line on this bill. An architect reads the two
together: "this decision buys me fault isolation — and costs me a second managed
database at ₹2,300/month, forever."

---

## The honest baseline: what 1 lakh orders/day *actually* needs

From [`tadka-growth-story.md`](tadka-growth-story.md): 1 lakh/day ≈ **1.2 orders/s
average, ~25/s peak**, a few hundred to ~1,000 reads/s. A single modest Postgres
box handles this without breaking a sweat. The cheapest architecture that meets
the SLO:

| Component | Spec | ₹/month |
|-----------|------|--------:|
| App server (the monolith) | 1× EC2 `t3.medium` (2 vCPU / 4 GB) | ~₹2,800 |
| Database | 1× RDS Postgres `db.t3.small` + 20 GB | ~₹2,300 |
| Cache | 1× ElastiCache Redis `t3.micro` | ~₹1,000 |
| **Honest minimal total** | | **~₹6,100** |

**This is all 1 lakh/day needs.** No read replica, no Kafka, no 4 services, no
gateway. Remember this number.

---

## What we actually built — "4 services + gateway" at 1 lakh/day

| Component | Spec | ₹/month | Why we have it (earned) |
|-----------|------|--------:|-------------------------|
| Compute — 5 containers (monolith + Payment + Delivery + Restaurant + gateway) | ECS Fargate, ~0.5 vCPU / 1 GB each | ~₹7,500 | Independent deploy/scale per service (Wk 4–6) |
| Postgres ×4 (database-per-service: ordering / payment / delivery / restaurant) | `db.t3.small` each | ~₹9,200 | Fault + PCI + data isolation (ADR-026) |
| Read replica (ordering) | `db.t3.small` | ~₹2,300 | Read scaling + read/write split (ADR-016) |
| Redis | ElastiCache `t3.small` | ~₹1,500 | Cache-aside + locks + geo (ADR-018/019/034) |
| Kafka | MSK, 2× `kafka.t3.small` brokers (min) | ~₹5,500 | Async backbone + outbox/saga (ADR-027–029) |
| Gateway / ALB | ALB + LCU | ~₹1,700 | One entry, edge rate-limit (ADR-035) |
| Observability | self-hosted OTEL+Jaeger+Prom+Grafana on 1 box | ~₹2,800 | See the saga (ADR-040; ₹0 software vs Datadog) |
| CDN | CloudFront (images/static) | ~₹800 | Offload static |
| **Built total** | | **~₹31,300** | |

### The capstone number

**~₹31,300 vs ~₹6,100 → the distributed system costs ~5× the honest minimum at
1 lakh/day.** You did *not* buy it for performance — a single box was already
fast enough. You bought **fault isolation, PCI scope, and independent team
velocity**, and you earned each piece week by week when a real failure justified
it. The two biggest line items — **4 databases (₹11,500)** and **Kafka idle
(₹5,500)** — are pure isolation/decoupling overhead, invisible to a customer.
That is the honest trade: *operational independence is not free; it shows up here
every month.*

---

## The same system at 10 lakh orders/day

10×/day ≈ ~250 orders/s peak. Now the architecture genuinely earns its keep — and
the bill grows **sub-linearly**, because the fixed distributed overhead was
already paid:

| Component | What changes | ₹/month |
|-----------|--------------|--------:|
| Compute | Scale out 2–3 instances/service + autoscale | ~₹22,000 |
| Postgres ×4 + replicas | Larger (`r6g.large`) + a replica per hot service | ~₹35,000 |
| Redis | Larger node | ~₹5,000 |
| Kafka | More/larger brokers | ~₹12,000 |
| ALB | More LCUs | ~₹3,000 |
| Observability | More ingest, bigger box | ~₹8,000 |
| CDN | More traffic | ~₹3,000 |
| **Built total @ 10 lakh/day** | | **~₹88,000** |

10× the orders for **~2.8× the cost** (₹31k → ₹88k). The lesson: the distributed
architecture scales *cost-efficiently* once you're in it — but the honest
monolith, which was 5× cheaper at 1 lakh/day, would have to make its **first big
architectural jump right here**. You front-loaded the complexity; at 10 lakh/day
that bet starts paying off.

---

## Optimization levers (where the 30–40% hides)

| Lever | Applies to | Saving | Catch |
|-------|-----------|-------:|-------|
| Right-size | everything | varies | `t3.medium` when `t3.small` is enough is the #1 leak — check CPU/mem first |
| Reserved Instances / Savings Plans (1-yr) | steady compute + RDS | ~30–40% | commitment; only for the baseline you *know* you'll run |
| Fargate Spot | non-critical (CI, load-gen, batch) | ~60–70% | can be reclaimed; never the payment path |
| Autoscale to baseline at night | compute | ~40–60% off-peak | needs a warm-up plan for the morning ramp |
| Shut down dev/staging after hours | non-prod | ~60% | automate it or nobody remembers |
| MSK Serverless / self-host Kafka | messaging | varies | removes the ~₹5,500 idle-broker floor at small scale |
| Budget alerts at ₹25k / ₹30k / ₹35k | the bill itself | catches surprises | not a saving — a smoke alarm |

> At Flipkart's scale, cloud-cost optimization is a full-time team. At your
> startup's scale, the five rows above cut the bill ~40% in an afternoon.

---

## When-to-switch triggers (read with the growth story)

| Spend pressure | Trigger | Move |
|----------------|---------|------|
| Single box CPU pinned at rush | reads saturate the primary | add a read replica (already in, ADR-016) |
| 4 idle DBs feel wasteful at low scale | you're pre-product-market-fit | *don't extract yet* — a modular monolith is one DB and one bill |
| Kafka's ₹5,500 floor hurts at MVP | <1 lakh/day, one team | defer Kafka; in-process events are free (the Day 4–7 arc) |
| Compute bill grows linearly with traffic | sustained national load | reserved/savings plans on the known baseline |
| Postgres box can't hold the working set in RAM | billions of rows (Wk 8) | partition → shard (deferred, ADR-017) |

**The thread:** every line on this bill maps to an ADR and a week where a failure
made it worth paying. That mapping — *"here's the rupee, here's the failure that
justified it, here's the load where it flips"* — is the difference between a
senior engineer and an architect.

---

## How this maps to the AWS reference in `terraform/`

The ~₹31,300 "built" table above is the one AWS figure to quote (it replaces the
older "~₹20–60k/month" range in `deploy/README.md` and `terraform/README.md`).
The plan-only stack in `terraform/environments/demo` differs from that table in
two ways, both unpriced here on purpose (check the AWS pricing page before you
quote a number):

- It adds **one NAT gateway** (hourly + per-GB), which the table above does not
  include. Private ECS tasks need it to pull images and call out.
- **MSK is off by default** (`enable_msk = false`): Kafka runs as one Fargate
  task instead, which removes the ~₹5,500 idle-broker line and adds one more
  small Fargate task.

## Real Azure bills per session (ADR-064)

The live class environment runs on Azure for one session at a time and is
destroyed afterwards (`scripts/cloud-up.ps1` / `cloud-down.ps1`). These rows hold
**real Cost Management figures** for one session's resource group, not models.

| Session | Mode | Hours up | Real bill (Cost Management) | Planning estimate (not a bill) |
|---------|------|---------:|----------------------------:|-------------------------------|
| Real Azure bill, basic session | `basic` | TO BE FILLED after first dry run | **TO BE FILLED after first dry run** | ~₹50–150 per ~4 h |
| Real Azure bill, ha session | `ha` | TO BE FILLED after first dry run | **TO BE FILLED after first dry run** | ~₹250–350 per 4 h |

Read the real number next to the modeled monthly AWS bill on Day 16: one is a
month of a system that runs forever, the other is an afternoon of one that
exists only while you look at it. Both are architecture decisions.
