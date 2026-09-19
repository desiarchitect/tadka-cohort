# Day 8 — Strangler Fig (extract without a big-bang rewrite)

You peel **one** capability at a time. Everything else stays on the monolith. Day 8 peels **Payment**.

**Today you have no API gateway.** You run two `dotnet run`s (`:5224` and `:5240`). The picture below is the **pattern**; YARP `:8080` is **Day 11**. Until then, the “router” is: clients hit the monolith; the monolith calls Payment over HTTP.

```mermaid
flowchart LR
  client[Clients] --> mono[Monolith :5224]
  mono -->|"HTTP IPaymentClient"| pay[Payment Service :5240<br/>own DB 5434]
```

Later (Day 11+), a gateway sits in front and peels more vines the same way:

```mermaid
flowchart LR
  client[Clients] --> gw[YARP Gateway :8080]
  gw -->|/api/v1/payments/**| pay[Payment]
  gw -->|everything else /api/v1/**| mono[Monolith]
```

```mermaid
flowchart TB
  subgraph bigbang [Big-bang rewrite - do not]
    a[Stop the world] --> b[Rebuild everything] --> c[Cut over once]
  end
  subgraph strangler [Strangler Fig - this]
    e[Route 1 capability out] --> f[Verify] --> g[Peel the next] --> h[Monolith shrinks]
  end
```

Because Day 7 gave Payment its own DbContext/schema/contract, extraction is a **move**, not a rewrite.
