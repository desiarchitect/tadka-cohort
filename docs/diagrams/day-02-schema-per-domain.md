# Day 2 — Schema-per-domain (one database, five schemas)

One PostgreSQL **instance**. Five **schemas**. **Zero arrows between them** — that missing line is ADR-008, not an omission.

```mermaid
graph TB
    subgraph pg["PostgreSQL 16 — single instance"]
        ordering["ordering<br/>orders · order_items"]
        restaurant["restaurant<br/>restaurants · menu_items"]
        delivery["delivery<br/>delivery_agents · delivery_assignments"]
        identity["identity<br/>users · user_addresses"]
        payment["payment<br/>payments"]
    end

    api["Tadka.Api<br/>one TadkaDbContext"]
    api --> pg

    style pg fill:#0f172a,stroke:#334155,color:#e2e8f0
    style ordering fill:#1e3a5f,stroke:#3b82f6,color:#93c5fd
    style restaurant fill:#14532d,stroke:#22c55e,color:#86efac
    style delivery fill:#134e4a,stroke:#14b8a6,color:#5eead4
    style identity fill:#78350f,stroke:#f59e0b,color:#fcd34d
    style payment fill:#4c1d95,stroke:#a78bfa,color:#ddd6fe
    style api fill:#1e293b,stroke:#60a5fa,color:#93c5fd
```

There is no `users` schema and no `orders` schema. Names are **bounded-context** names (`identity`, `ordering`), not table names. The C# folder for identity is still `Domain/Users/` — aggregate name vs context name.

Inside a schema, FKs are fine (`order_items.order_id` → `orders.id`). Across schemas, only Guid columns (`orders.customer_id` has no FK to `identity.users`).
