# Day 7 — Payment resilience (diagrams)

Payment enters the order flow for the first time — and with it, Tadka's first hard external dependency it doesn't own. See ADR-021 (Polly timeout + bulkhead), ADR-022 (modular monolith, MediatR, Payment module), ADR-023 (async payment, CQRS-lite), and `cohort-prep/day-07/break-kit-day-07.md` (the brownout lab this maps to).

Grounded in the actual code on this branch: `src/Tadka.Api/Modules/Payments/` (`PaymentProcessor.cs`, `PaymentWorkChannel.cs`, `PayForOrderOnOrderPlaced.cs`, `PaymentOptions.cs`, `IPaymentGateway.cs` / `FakePaymentGateway.cs`), `src/Tadka.Api/Infrastructure/Resilience/PaymentResiliencePipeline.cs`, `src/Tadka.Api/Domain/Payments/Payment.cs`. Still **one deployable** — `src/Tadka.Api` is the only project in `src/` on this branch; Payment is carved as a module, not yet a service (that's Day 8).

---

## 1. High-level runtime architecture (end of Day 7, shipped default: Async)

```mermaid
%%{init: {'theme': 'dark', 'themeVariables': {
  'primaryColor': '#3B82F6',
  'primaryTextColor': '#F8FAFC',
  'primaryBorderColor': '#60A5FA',
  'secondaryColor': '#1E293B',
  'tertiaryColor': '#334155',
  'lineColor': '#94A3B8',
  'textColor': '#E2E8F0',
  'fontSize': '14px'
}}}%%
graph TB
    Client([Client App])

    subgraph Mono["Tadka.Api — single process"]
        Ordering([Ordering<br/>Controllers + MediatR])
        PayModule([Payment module<br/>PayForOrderOnOrderPlaced])
        Processor([PaymentProcessor<br/>BackgroundService])
    end

    Client -->|POST /orders| Ordering
    Ordering ==>|write + read-your-writes| Primary[(Postgres primary<br/>ordering + payment schemas)]
    Ordering -->|read-heavy GETs| Replica[(Postgres read replica)]
    Ordering --> Cache([Redis<br/>cache-aside + SSE backplane])

    Ordering -.->|"MediatR: OrderPlaced<br/>(in-process, after commit)"| PayModule
    PayModule -.->|enqueue| WorkQ[/PaymentWorkChannel<br/>Channel&lt;T&gt;/]
    WorkQ -.-> Processor
    Processor -->|charge, via Polly:<br/>bulkhead 10 + timeout 2s| Gateway[[Payment Gateway<br/>external, not owned by Tadka]]
    Processor ==>|"Completed / Failed<br/>(payment schema)"| Primary
    Processor -.->|"MediatR: PaymentCompleted /<br/>PaymentFailed"| Ordering

    Cache -.->|order status change,<br/>pub/sub backplane| Client

    style Client fill:#F59E0B,stroke:#F59E0B,color:#0F172A
    style Ordering fill:#3B82F6,stroke:#60A5FA,color:#F8FAFC
    style PayModule fill:#22C55E,stroke:#22C55E,color:#0F172A
    style Processor fill:#22C55E,stroke:#22C55E,color:#0F172A
    style Primary fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style Replica fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style Cache fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style WorkQ fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style Gateway fill:#F59E0B,stroke:#F59E0B,color:#0F172A
```

**Legend:** amber = outside Tadka's control (client, payment gateway), blue = the existing request path, green = what Day 7 adds, slate = infrastructure. Dashed arrows are in-process events/queueing, not HTTP calls.

**What the diagram is actually proving:** `Ordering` and `PayModule` sit in the same OS process and the same physical Postgres — but `Ordering` has **zero references** to anything under `Modules/Payments`, and the only contract between them is the `OrderPlaced`/`PaymentCompleted`/`PaymentFailed` events (ADR-022). That boundary is what Day 8 later extracts into a real service without touching Ordering's code — the diagram already shows the cut line, a day early.

Also note what's *not* in the request path: `Ordering -->|POST /orders|` never reaches `Gateway`. The synchronous edge stops at `Primary`. Payment is entirely someone else's problem by the time the client gets its `201`.

---

## 2. Before / after: the brownout (ADR-021 + ADR-023)

Same code, one config flip — `Payment:Mode = Synchronous` vs `Async` (`PaymentOptions.cs`) with the gateway set to `Behavior: Slow` (~8s, `GatewayOptions.SlowDelaySeconds`). This is the actual teaching lever in the code, not a hypothetical.

```mermaid
%%{init: {'theme': 'dark', 'themeVariables': {
  'primaryColor': '#3B82F6',
  'primaryTextColor': '#F8FAFC',
  'primaryBorderColor': '#60A5FA',
  'secondaryColor': '#1E293B',
  'tertiaryColor': '#334155',
  'lineColor': '#94A3B8',
  'textColor': '#E2E8F0',
  'fontSize': '14px'
}}}%%
flowchart LR
    subgraph Before["BEFORE — Payment:Mode = Synchronous (the brownout)"]
        direction LR
        B1["POST /orders<br/>calls gateway inline"] --> B2["Gateway is Slow<br/>~8s"]
        B2 --> B3["request thread +<br/>Postgres connection<br/>held 8s"]
        B3 --> B4["pool drains<br/>(ADR-015)"]
        B4 --> B5["p99 collapses for<br/>EVERY endpoint,<br/>even pure reads"]
    end

    subgraph After["AFTER — Payment:Mode = Async (shipped default)"]
        direction LR
        A1["POST /orders persists<br/>+ publishes OrderPlaced"] --> A2["returns 201<br/>in ms — no gateway call<br/>on this path"]
        A2 --> A3["PaymentProcessor charges,<br/>via Polly: bulkhead 10 +<br/>timeout 2s"]
        A3 --> A4["Gateway is Slow<br/>~8s"]
        A4 --> A5["fails fast at 2s,<br/>contained to 1 of 10<br/>processor slots"]
        A5 --> A6["order settles<br/>Paid / Cancelled,<br/>customer sees it via SSE"]
    end

    style B1 fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style B2 fill:#EF4444,stroke:#EF4444,color:#0F172A
    style B3 fill:#EF4444,stroke:#EF4444,color:#0F172A
    style B4 fill:#EF4444,stroke:#EF4444,color:#0F172A
    style B5 fill:#EF4444,stroke:#EF4444,color:#0F172A
    style A1 fill:#22C55E,stroke:#22C55E,color:#0F172A
    style A2 fill:#22C55E,stroke:#22C55E,color:#0F172A
    style A3 fill:#3B82F6,stroke:#60A5FA,color:#F8FAFC
    style A4 fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style A5 fill:#22C55E,stroke:#22C55E,color:#0F172A
    style A6 fill:#22C55E,stroke:#22C55E,color:#0F172A
```

**The point that's easy to miss:** the gateway is *equally slow* in both halves (`~8s`, unchanged). Nothing about the payment provider got better. What changed is **where the wait happens and who it's allowed to hurt** — before, an 8s wait sits on the shared connection pool that every other endpoint depends on; after, the same 8s wait is a `TimeoutRejectedException` at 2s, contained to one of ten processor slots, on a background path the order-creation request never touches. Same failure, bounded blast radius (ADR-021's own phrase).

One real ordering detail worth teaching from the code, not just the prose: in `PaymentResiliencePipeline.cs` the **bulkhead (concurrency limiter) is the outermost strategy and timeout is innermost** — a call is admitted against the 10-permit cap first, *then* subjected to the 2s clock. Rejected-for-capacity and timed-out-in-flight are two different failure modes with two different causes, and the pipeline is built to tell them apart.
