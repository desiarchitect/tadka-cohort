# Evolution runbook: proving what system design bought, from 1 user to 1 million

**Branch:** run it from any branch from `day-05` to `day-16`; each carries the same harness. It checks out the days it measures by itself (into `../tadka-evo/`); your own checkout is never touched.
**Tools:** `evolution/` (runner, manifest, seed, report) + `k6/evolution.js` (the workload).

The capstone question is "did the 16 days actually make Tadka scale?". This runbook answers it with numbers you
measure yourself: the **same** load test, on the **same** data, on the **same** machine, against six checkpoints of
the codebase. The only thing that changes between two runs is the architecture.

> **Why not just run day-03 against day-16?** Because that comparison can lie in both directions. Day 16 does *more*
> work per request than day 3 (a gateway hop, JWT validation), so on a tiny data set with no failures day 3
> can look *faster*. The course's wins show up under specific pressures: a big table, a hot key, a slow or dead
> dependency, more traffic than one box can take. So we apply each pressure, at each step, and watch which day fixes it.

---

## 1. The ladder: what each checkpoint should prove

| Checkpoint | What changed (ADRs) | Pressure that proves it | What you should see |
|---|---|---|---|
| `day-03` | Baseline monolith: REST API, no indexes, default pool (005-010) | All scenarios: this is the "before" | History seq-scans 200k orders; the knee comes early |
| `day-05` | Indexes, pool sizing, read replica (014-016) | **history** on 200k orders | History capacity jumps several-fold; p99 flat for longer |
| `day-06` | Redis cache-aside + single-flight lock (018-020) | **hotkey** (80% of reads on one menu) | Menu capacity jumps; history unchanged (the control: a cache doesn't fix a query) |
| `day-07` | Bulkhead + timeout, async payment (021-023) | **orders** with a slow gateway: `sync-unprotected` → `sync-slow` → `async-slow` | Unprotected: every order waits the full 8 s. With timeout + bulkhead: ~2 s. Async: milliseconds. (The Day 7 brownout, 9.2 s → 2.1 s → ms, measured.) |
| `day-09` | Payment service + Kafka + outbox + saga (024-029) | **orders** with Payment slow, then **down** | Orders keep returning 201 while Payment is slow or not running at all |
| `day-16` | 4 services + gateway, JWT, breaker, scale-out (030-047) | **orders** with gateway `Outage`; **history** with 1 vs 3 replicas | Orders unaffected by the outage (breaker opens in Payment's log); 3 replicas lift capacity until the shared database caps it |

Every checkpoint runs on the **same seeded data**: ~200,000 orders, 20,000 of them belonging to the seeded customer
Priya (`evolution/seed/orders-large.sql`, which handles the Day 3 vs Day 5 column naming by itself).

### The four scenarios (`k6/evolution.js`)

| Scenario | One request = | Stresses |
|---|---|---|
| `history` | `GET /api/v1/orders?customerId=<Priya>&page=1..20` | The big table: indexes, pool, replica, scale-out |
| `hotkey` | `GET /api/v1/restaurants/{id}/menu`, 80% to Meghana Foods (cold cache at start) | One hot key: cache + stampede lock |
| `browse` | `GET` restaurant list or a menu, 50/50 | The everyday read path, including what the gateway hop costs |
| `orders` | `POST /api/v1/orders` with a fresh `Idempotency-Key` | The write path and its dependency on Payment |

### How capacity is measured

The workload is an **open model** (k6 `ramping-arrival-rate`): it sends a fixed number of requests per second whatever
the server does, in steps of 5, 10, 25, 50, 100, 200 and 400 req/s, 40 seconds each, after a 20-second warm-up.
(A closed "N virtual users" model slows down when the server slows down, which hides the very knee we are looking for.)

For every step it records p50/p95/p99, the error rate and how many requests were actually served. **Capacity** is the
highest step, counted up from the bottom without a gap, where:

1. p99 < **300 ms** (the Week 1 SLO),
2. errors < **1%**, and
3. the server served at least **95%** of the target rate (it kept up).

That single number per run is what the report compares. If a checkpoint passes every step, the report says
"none (raise RATES)": rerun it with `-Rates 5,10,25,50,100,200,400,800,1600`. For a class-length run, see section 4 (`-Quick`).

---

## 2. Before you start

- **Tools:**
  - PowerShell 7 (`pwsh`, not Windows PowerShell 5.1), or bash 4+ with `jq` and `curl`.
  - git, Docker Desktop with **at least 8 GB** of memory for the Day 16 stack, the **.NET 10 SDK**, k6 (`winget install GrafanaLabs.k6` / `brew install k6`), and Node 18+ for the report.
- **Stop your own Tadka stack** (`docker compose down` in your repo) and any `dotnet run` you have open. The compose
  files use fixed container names, so two stacks cannot run at once; the runner refuses to start if it finds one.
- **Use one machine, on mains power, with other heavy apps closed.** Numbers from different machines must never be
  mixed in one comparison; the report prints the machine of every run and warns when they differ.
- **Disk:** six worktrees (one per checkpoint) next to your repo in `../tadka-evo/`, plus their build output: a few GB.
- **Time:** about 5 minutes per run plus 2-4 minutes of setup per checkpoint. The full ladder (21 runs) takes about
  2.5 hours; with `-Repeat 3` (recommended for anything you will present) about 7 hours. Run it overnight.

---

## 3. Run it

### The whole ladder in one command (recommended)

**PowerShell:**
```powershell
pwsh evolution/run-ladder.ps1                 # every row of section 1, then builds the report
pwsh evolution/run-ladder.ps1 -Repeat 3       # three runs per row; the report uses the median
pwsh evolution/run-ladder.ps1 -Only history   # just one scenario's rows
pwsh evolution/run-ladder.ps1 -Quick          # every row at class length (~1 hour): a rehearsal, not the numbers to quote
```

**Bash:**
```bash
evolution/run-ladder.sh
evolution/run-ladder.sh --repeat 3
evolution/run-ladder.sh --only history
evolution/run-ladder.sh --quick
```

Rows whose result file already exists are skipped, so an interrupted ladder resumes where it stopped (`-Force` /
`--force` redoes them). Rows of the same day share one infrastructure start and one seed.

### One run at a time

```powershell
pwsh evolution/run-checkpoint.ps1 -Day day-03 -Scenario history
pwsh evolution/run-checkpoint.ps1 -Day day-07 -Scenario orders -Variant sync-unprotected
pwsh evolution/run-checkpoint.ps1 -Day day-16 -Scenario history -Variant replicas3 -Repeat 3
```
```bash
evolution/run-checkpoint.sh --day day-03 --scenario history
evolution/run-checkpoint.sh --day day-07 --scenario orders --variant sync-unprotected
```

**What one run does:**
1. Checks out `origin/<day>` into `../tadka-evo/<day>`.
2. Resets the infrastructure (`docker compose -p tadka-evo down -v` then `up -d`) and waits for the containers to be healthy.
3. Builds the solution and starts that day's services, each on its usual port. The rate limiters are raised, and admission control and load shedding are pinned off, so you measure capacity, not a limiter.
4. Seeds the 200k orders and waits for the read replica to catch up.
5. Logs in as Priya where the day has auth (Day 10+).
6. Runs k6 and writes `evolution/results/<label>.json` plus `<label>.meta.json` (commit, machine, variant).
7. Stops the services.

Service logs go to `evolution/logs/<label>/`.

**Variants** (from `evolution/checkpoints.json`):

| Checkpoint | Variant | What it changes |
|---|---|---|
| `day-07` | `sync-unprotected` / `sync-slow` / `async-slow` | All with the fake gateway on `Slow` (8 s). `Payment:Mode=Synchronous` with no timeout and no bulkhead (`TimeoutSeconds=30`, `MaxConcurrentCharges=1000`, the Day 7 baseline); `Synchronous` with the shipped 2 s timeout and bulkhead of 10; `Async` |
| `day-09` | `payment-slow` / `payment-down` | Payment's gateway on `Slow`; or the Payment service not started at all |
| `day-16` | `payment-outage` / `payment-down` | Payment's gateway on `Outage` (transport failures, the breaker opens); or Payment not started |
| `day-16` | `replicas1` / `replicas3` | The `scale-out` compose profile (monolith in containers behind nginx on **:8091**) with one or three replicas running |

### Build the report

```bash
node evolution/report.mjs
```

It writes `evolution/report.md` (one table per scenario, the per-step numbers, and the users-supported model) and
two charts per scenario in `evolution/report/`:
- **capacity per checkpoint:** one bar each;
- **p99 at each load step:** one line per checkpoint, with the 300 ms SLO marked; a hollow dot marks a failed step.

Repeats are folded into their median.

---

## 4. Demo it day by day in class

The full ladder is an overnight job. In class you show **one before/after pair**, live, with `-Quick` (`--quick` in
bash): load steps of 10, 25, 50, 100 and 200 req/s, 20 seconds each, after a 10-second warm-up. That is about **2 minutes
per run** plus a minute or two of setup. Quick results are labelled `...-quick`, so they never overwrite or mix with
the overnight results; the report warns if a table ever holds both kinds.

Run these from that day's branch (`git checkout day-05` on Day 5, and so on). Each branch carries the same harness,
and it can measure any checkpoint, including earlier ones.

| Day | Show | Runs | Then |
|---|---|---|---|
| **5** | Indexes + pool + replica | `day-03 history`, `day-05 history` | `node evolution/report.mjs --days day-03,day-05 --scenarios history` |
| **6** | Cache fixes the hot key, not the query | `day-05 hotkey`, `day-06 hotkey`, `day-06 history` (the control) | `node evolution/report.mjs --days day-05,day-06` |
| **7** | Brownout → contained → off the request path | `day-07 orders` with `sync-unprotected`, `sync-slow`, `async-slow` | `node evolution/report.mjs --days day-07 --scenarios orders` |
| **9** | Orders survive a dead Payment | `day-07 orders sync-slow`, `day-09 orders payment-down` | `node evolution/report.mjs --days day-07,day-09 --scenarios orders` |
| **16** | Scale-out and its database ceiling | `day-16 history` with `replicas1`, `replicas3` | `node evolution/report.mjs --days day-16 --scenarios history` |

Day 16 also presents the full overnight ladder report (section 3).

**Day 5, for example (PowerShell):**
```powershell
pwsh evolution/run-checkpoint.ps1 -Day day-03 -Scenario history -Quick
pwsh evolution/run-checkpoint.ps1 -Day day-05 -Scenario history -Quick
node evolution/report.mjs --days day-03,day-05 --scenarios history
```

**Bash:**
```bash
evolution/run-checkpoint.sh --day day-03 --scenario history --quick
evolution/run-checkpoint.sh --day day-05 --scenario history --quick
node evolution/report.mjs --days day-03,day-05 --scenarios history
```

Open `evolution/report.md`: the table shows both capacities and the "vs first row" multiple, and the p99 chart shows
the day-03 curve crossing the 300 ms line long before day-05's.

**Same pattern for the other days:** the run lines for Day 7, for example, are
`-Day day-07 -Scenario orders -Variant sync-unprotected -Quick`, then `-Variant sync-slow`, then `-Variant async-slow`.

**Rehearse once the evening before.** The first run of each checkpoint is slow: it fetches the branch, builds it, and
pulls the Docker images. Later runs reuse all of that, so in class you only wait for the run itself.

**One machine, one run at a time, nothing else heavy open.** Quick runs have fewer and shorter steps, so they are
noisier than the overnight ladder. Use them to show the shape (where the knee moves), and quote the overnight numbers
when you need exact figures.

## 5. Reading the results

Read each scenario as "which checkpoint moved the knee, and why":

- **history:**
  - `day-03` should knee early: Postgres reads all 200k rows for every request, and the default pool queues behind those scans.
  - `day-05` should move the knee far right: the `(CustomerId, CreatedAt)` index (ADR-014) plus history served from the replica (ADR-016).
  - `day-06` should look like `day-05`. That is the point: a cache does not fix a query, and this is your **control**.
  - `day-16` behind the gateway may lose a little against `day-05` (gateway hop and JWT on every call). That is the cost of the boundaries, and it is worth naming in a presentation.
- **hotkey:** the `day-03` and `day-05` menu reads hit Postgres every time; from `day-06` almost every read is a Redis hit, and the single-flight lock means the cold start sends one query, not hundreds, to the database (compare the toy `toydemo/day-06-cache-realtime/hot-key-stampede-toy`).
- **orders:**
  - `day-07 sync-unprotected` fails at the very first step: every order waits the full 8 s, and under load the requests pile up behind each other (the brownout).
  - `sync-slow` still fails the 300 ms SLO, because each order now waits for the 2 s timeout. But the bulkhead stops the pile-up, and the rest of the API stays healthy. That is containment, not a fix.
  - `async-slow` should return in milliseconds: same slow gateway, but the charge happens off the request path (ADR-023).
  - `day-09 payment-down` and `day-16 payment-outage` show that a sick or dead Payment no longer touches the order path at all.
  - On `day-16`, open `evolution/logs/day-16-orders-payment-outage/payment.log` and find the circuit breaker opening (ADR-043).
- **scale-out (`day-16 replicas1` vs `replicas3`):**
  - Three replicas should lift capacity, but not 3×. They share one primary Postgres and one replica, so the database becomes the next bottleneck.
  - That is the honest lesson of horizontal scaling: you move the knee to the next shared resource. That is where sharding (ADR-017, deferred) would come in.
- **browse:** the everyday path is already fast on every checkpoint with this data size. Use it to show that the architecture did not make the common case worse, not as a win.

**Signs that a run is wrong, not the architecture:**
- **Errors at the very first step:** check the service logs. Common causes are a login failure, a seed failure, or a port already taken.
- **A run with `rate_limited`-style 429s:** a limiter was not raised. The manifest raises every limiter it knows; tell me if one slips through.
- **Wildly different numbers between repeats:** something else was using the machine. Rerun with `-Repeat 3` and use the median.

---

## 6. From capacity to "1 user → 1 million users"

A laptop cannot generate a million users, and this runbook does not pretend to. What it gives you is a **measured
capacity per box** at each checkpoint. Turning that into users is a model, and you should present it as one.

**Users supported ≈ capacity (req/s) ÷ requests per second per active user.**

The report assumes **0.1 req/s per active user**: during the dinner rush an active user makes about one request every
10 seconds (browse, open a menu, check an order). Change it with `node evolution/report.mjs --rps-per-user 0.05`.

| Rung | Active users | Load (at 0.1 req/s each) | What it takes | Where in Tadka |
|---|---|---|---|---|
| 1 | 1 | 0.1 req/s | Anything works | `day-01`-`day-03` |
| 2 | ~1,000 | ~100 req/s | Indexes, a sized pool, a read replica | `day-05`: the history rung you measured |
| 3 | ~10,000 | ~1,000 req/s | A cache in front of hot reads, several app replicas behind a load balancer | `day-06` + `replicas3` |
| 4 | ~100,000 | ~10,000 req/s | Async work off the request path, a broker, fault isolation per service, an edge cache/CDN | `day-07` → `day-16` |
| 5 | ~1,000,000 | ~100,000 req/s | Many replicas per service, partitioned/sharded data, multi-AZ, a CDN that serves most reads | Beyond Tadka: ADR-017 (sharding, deferred), ADR-039 (cloud), ADR-064 (Azure) |

**How many app replicas a rung needs:** `replicas = ceil(target req/s × 1.5 headroom ÷ capacity per replica)`. Take
"capacity per replica" from your `day-16 replicas1` run, then check it against `replicas3`. If three replicas gave
less than 3× one, the shared database is already the limit, and the next rung needs a data-layer change, not more app
boxes.

**Rupees:** feed the replica count into [`docs/cost-model.md`](../cost-model.md) for ₹ per month and per 1,000 orders.
The cost model already shows the shape: about 5× the honest-minimal cost at 1 lakh orders/day, then sub-linear growth
(10× the orders for about 2.8× the cost).

**What the extrapolation assumes (say these out loud):**
- Per-box capacity measured on a laptop is lower than on a dedicated server, so the model is conservative per box.
- At higher rungs the bottleneck moves: database, then Kafka partitions, then network, then hot keys. Each move needs its own measurement; you cannot multiply your way past it.

---

## 7. Share the results

Commit `evolution/results/*.json`, `evolution/report.md` and `evolution/report/*.svg` to a branch, or send them over.
They are small JSON files, and the report rebuilds from them on any machine with `node evolution/report.mjs`.

## 8. Troubleshooting

| Symptom | Fix |
|---|---|
| `container tadka-postgres belongs to compose project '...'` | Run `docker compose down` in that project's folder (your own repo), then rerun |
| `port 5224 is already in use` | A `dotnet run` is still open somewhere; stop it |
| `read replica did not catch up` | Docker is short on memory or disk; give Docker Desktop more memory and rerun |
| `login returned no accessToken` | The Day 10+ login rate limit (5 per 10 s) or lockout tripped from manual tries; wait 60 s |
| k6 warns about `dropped_iterations` / "insufficient VUs" | The server is so slow that 1,500 VUs are all waiting: that step has already failed; it is part of the result |
| Windows: `Kill` / process tree errors | You are on Windows PowerShell 5.1; use `pwsh` (PowerShell 7) |
| `bash 4+ needed` on macOS | `brew install bash`, then run the script with that bash |

**Cleanup:**
- Remove the Docker resources with `docker compose -p tadka-evo --profile scale-out down -v` from any worktree.
- Remove the worktrees with `git worktree remove ../tadka-evo/<day>` for each day.
- The results stay in `evolution/results/` until you delete them.
