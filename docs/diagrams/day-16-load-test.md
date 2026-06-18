# Day 16 — Load Testing (diagrams)

Measure the breaking point before you need it. k6 suite targets gateway `:8080` — see `k6/lib.js`, `k6/smoke.js`, `average-load.js`, `stress.js`, `spike.js`.

---

## 1. Load-test types (VUs vs time)

```
VUs
 │
 │  SMOKE — "is it even working?"
 │  ───────────────────────────              1 VU, 30s. Sanity, not load.
 │
 │  AVERAGE — "does the SLO hold?"
 │        ┌───────────────┐                  ramp → ~50 VUs → sustain.
 │       /                 \                  Assert: menu/list p99 < 300ms.
 │  ____/                   \____
 │
 │  STRESS — "where's the breaking point?"          ← THE DEMO
 │                        ╱▓▓▓▓  ← knee: p99 explodes,
 │                  ╱─────             5xx, ONE resource saturates
 │            ╱─────
 │      ╱─────
 │  ____
 │
 │  SPIKE — "survive AND recover?"
 │         █                                  instant flood → hold → drop.
 │         █                                  breaker OPEN → recover.
 │  ___────█───___________
 │
 │  SOAK — "any leaks?" (overnight/CI, not in class)
 │   ┌──────────────────────────────┐         moderate load for hours.
 │  _│                              │_
 │
 └────────────────────────────────────────▶ time
```

| Type | You learn… |
|------|------------|
| Smoke | functional correctness — is there a *bug*? |
| Average | does the SLO hold on a normal day? |
| Stress | the **knee** — capacity ceiling + first resource to fall |
| Spike | survival (fail fast) + recovery (no leak) |
| Soak | slow leaks invisible in short runs |

Peak at 1 lakh orders/day ≈ **~25 orders/s** — far below the stress knee. We measure so "we don't need to scale yet" is evidence, not optimism.

---

## 2. The breaking point (the knee)

Read this on the Day-13 Grafana RED dashboard during **stress**.

```
 p99 latency                                        throughput (req/s)
 (ms)                                               served
   │                              ╱│                   │        ┌─────────────
   │                            ╱  │ ← p99 explodes     │       ╱   ← plateau
   │                          ╱    │                    │      ╱
   │  SLO ─ ─ ─ ─ ─ ─ ─ ─ ─ ╱─ ─ ─ │                    │     ╱
   │  300ms              ╱         │                    │    ╱
   │  ___________──────╱           │                    │___╱
   └──────────────────┼────────────┼──────▶ load        └───┼──────────▶ load
                      │         THE KNEE                  THE KNEE
```

1. **Flat zone** — p99 under SLO, throughput rises 1:1 with load.
2. **The knee** — queue forms; p99 shoots up; throughput plateaus. **This load value is the breaking point.**
3. **Past the knee** — errors appear; one resource pinned at 100%.

| First to saturate | Symptom | Fix |
|-------------------|---------|-----|
| Postgres pool | wait time climbs, DB CPU fine | pool size / replica / shorter holds |
| Kafka lag | 201 but orders stay pending | scale consumers / partitions |
| Container memory | OOM / GC thrash | right-size / scale out |
| CPU on one box | one service at 100% | scale up, then out |

After stress knee → **sharding pivot** (ADR-017 revisit) is the honest "what's next at 10×" beat.