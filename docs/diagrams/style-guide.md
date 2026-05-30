# Mermaid Diagram Style Guide

All teaching diagrams in the cohort follow these conventions for consistency.

## Color Palette (Dark Theme)

Matches the Desi Architect brand colors.

```
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
```

| Role | Color | Hex | Usage |
|------|-------|-----|-------|
| Primary service | Blue | `#3B82F6` | Main service nodes |
| Infrastructure | Slate | `#334155` | Databases, caches, queues |
| External | Amber | `#F59E0B` | External APIs, clients |
| Highlight/New | Green | `#22C55E` | Newly added components this session |
| Danger/Problem | Red | `#EF4444` | Failure points, bottlenecks |
| Background | Dark Slate | `#0F172A` | Diagram background |

## Node Shapes

| Component Type | Shape | Mermaid Syntax |
|---------------|-------|----------------|
| Service / API | Rounded rectangle | `ServiceName([Service Name])` |
| Database | Cylinder | `DB[(Database)]` |
| Message Queue | Parallelogram | `Queue[/Queue Name/]` |
| Cache | Stadium | `Cache([Cache])` |
| Client / User | Person | Actor syntax or rounded rect |
| Load Balancer | Hexagon | `LB{{Load Balancer}}` |
| External API | Subroutine | `Ext[[External API]]` |

## Arrow Styles

| Meaning | Syntax | When to use |
|---------|--------|-------------|
| Sync request | `-->` | HTTP calls, direct function calls |
| Async message | `-.->` | Events, message queue publish/consume |
| Data flow | `==>` | Database reads/writes |

## Sample Diagrams

### Day 1: Monolith Architecture

```mermaid
%%{init: {'theme': 'dark'}}%%
graph TB
    Client([Client App]) --> API([Tadka API<br/>.NET 10 Monolith])
    API ==> DB[(PostgreSQL)]

    style Client fill:#F59E0B,stroke:#F59E0B,color:#0F172A
    style API fill:#3B82F6,stroke:#60A5FA,color:#F8FAFC
    style DB fill:#334155,stroke:#94A3B8,color:#E2E8F0
```

### Day 8: First Service Extraction

```mermaid
%%{init: {'theme': 'dark'}}%%
graph TB
    Client([Client App]) --> GW{{API Gateway<br/>YARP}}
    GW --> OrderSvc([Order Service])
    GW --> RestSvc([Restaurant Service])
    GW --> MonoAPI([Tadka API<br/>Remaining Domains])

    OrderSvc ==> OrderDB[(Orders DB)]
    RestSvc ==> RestDB[(Restaurants DB)]
    MonoAPI ==> MainDB[(Main DB)]

    OrderSvc -.-> Kafka[/Kafka/]
    Kafka -.-> RestSvc

    style Client fill:#F59E0B,stroke:#F59E0B,color:#0F172A
    style GW fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style OrderSvc fill:#22C55E,stroke:#22C55E,color:#0F172A
    style RestSvc fill:#22C55E,stroke:#22C55E,color:#0F172A
    style MonoAPI fill:#3B82F6,stroke:#60A5FA,color:#F8FAFC
    style OrderDB fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style RestDB fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style MainDB fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style Kafka fill:#334155,stroke:#94A3B8,color:#E2E8F0
```

### Week 8: Final Architecture

```mermaid
%%{init: {'theme': 'dark'}}%%
graph TB
    Client([Client App]) --> CDN([CDN / CloudFront])
    CDN --> GW{{API Gateway<br/>YARP}}

    GW --> OrderSvc([Order Service])
    GW --> RestSvc([Restaurant Service])
    GW --> DeliverySvc([Delivery Service])
    GW --> UserSvc([User Service])
    GW --> PaymentSvc([Payment Service])

    OrderSvc ==> OrderDB[(Orders DB)]
    RestSvc ==> RestDB[(Restaurants DB)]
    DeliverySvc ==> DeliveryDB[(Delivery DB)]
    UserSvc ==> UserDB[(Users DB)]
    PaymentSvc ==> PaymentDB[(Payments DB)]

    OrderSvc --> Cache([Redis Cache])
    RestSvc --> Cache

    OrderSvc -.-> Kafka[/Kafka/]
    Kafka -.-> DeliverySvc
    Kafka -.-> PaymentSvc

    Prometheus([Prometheus]) --> Grafana([Grafana])

    style Client fill:#F59E0B,stroke:#F59E0B,color:#0F172A
    style CDN fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style GW fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style OrderSvc fill:#3B82F6,stroke:#60A5FA,color:#F8FAFC
    style RestSvc fill:#3B82F6,stroke:#60A5FA,color:#F8FAFC
    style DeliverySvc fill:#3B82F6,stroke:#60A5FA,color:#F8FAFC
    style UserSvc fill:#3B82F6,stroke:#60A5FA,color:#F8FAFC
    style PaymentSvc fill:#3B82F6,stroke:#60A5FA,color:#F8FAFC
    style Cache fill:#334155,stroke:#94A3B8,color:#E2E8F0
    style Kafka fill:#334155,stroke:#94A3B8,color:#E2E8F0
```

## Rules

1. **Every session gets a "before" and "after" diagram.** Show what changed this session in green.
2. **Max 8-10 nodes per diagram.** If it gets bigger, split into subsystem views.
3. **Label arrows** when the meaning isn't obvious (e.g., "HTTP GET", "order.placed event").
4. **Include a legend** for complex diagrams with multiple colors.
