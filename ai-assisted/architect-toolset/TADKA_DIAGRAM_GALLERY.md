# Tadka Diagram Gallery: one diagram of every kind

Nineteen diagrams of the **real Tadka system**, one for each type in [The Architect's Toolset](THE_ARCHITECTS_TOOLSET.md#3-the-diagrams-an-architect-draws). Open this file on GitHub or in VS Code with a Mermaid preview and every diagram renders.

> **How accurate is this?** Every name, port, topic, table, state and number was taken from the code on branch `day-16`, from `deploy/azure/*.tf`, or from a real trace captured from the running system. Where a diagram is a judgement and not a fact (the Wardley-style map), it says so. Where Tadka does **not** have something (for example a separate analytics store), the diagram does not draw it.

| # | Diagram | Question it answers |
|---|---|---|
| 1 | [System context](#1-system-context) | Who uses Tadka, and what outside systems does it touch? |
| 2 | [Container](#2-container-diagram) | What are the deployable pieces and how do they talk? |
| 3 | [Component](#3-component-diagram-inside-the-orders-service) | What is inside the Orders service? |
| 4 | [Code (class)](#4-code-diagram-the-order-aggregate) | How are the Order classes related? |
| 5 | [Deployment](#5-deployment-diagram-azure-container-apps) | Where does each piece run in the cloud? |
| 6 | [Sequence](#6-sequence-diagram-placing-an-order) | In what order do the parties talk when an order is placed? |
| 7 | [Flowchart](#7-flowchart-how-a-payment-is-processed) | How does a payment decide and branch? |
| 8 | [State machine](#8-state-machine-the-order) | Which states can an order be in, and which moves are legal? |
| 9 | [ER diagram](#9-er-diagram-the-orders-database) | Which tables and relations exist in the Orders database? |
| 10 | [Data flow](#10-data-flow-diagram-where-personal-and-money-data-goes) | Where does sensitive data come from, rest and go? |
| 11 | [Context map](#11-domain-or-context-map) | How do the bounded contexts relate? |
| 12 | [Event storming](#12-event-storming-board) | What happens in the business, in time order? |
| 13 | [Network topology](#13-network-topology-azure-session) | What is public, private, and behind which boundary? |
| 14 | [Dependency graph](#14-dependency-graph) | Who calls whom, and where is the cycle? |
| 15 | [Trace waterfall](#15-trace-waterfall-one-real-order) | Where did one real order spend its time? |
| 16 | [Latency budget](#16-latency-budget) | Where do the milliseconds go? |
| 17 | [Roadmap](#17-roadmap-the-16-day-evolution) | In what order did the system change? |
| 18 | [Decision tree](#18-decision-tree-should-this-be-its-own-service) | When does something become its own service? |
| 19 | [Wardley-style map](#19-wardley-style-map-what-is-commodity-and-what-is-ours) | What is commodity and what is our edge? |

---

## 1. System context

**C4 level 1.** The whole of Tadka is one box. Only people and outside systems appear.

```mermaid
flowchart TB
    customer["Customer<br/>orders food, tracks the order"]
    owner["Restaurant owner<br/>manages the menu and prices"]
    rider["Delivery rider<br/>sends location, updates status"]
    admin["Admin and on-call engineer<br/>runs, watches and fixes the system"]

    tadka["TADKA<br/>food delivery platform<br/>4 services behind 1 gateway"]

    pay["Payment gateway<br/>a fake gateway in the course,<br/>a real provider in production"]
    obs["Observability tools<br/>Jaeger, Prometheus, Grafana"]

    customer -->|"HTTPS, JWT"| tadka
    owner -->|"HTTPS, JWT"| tadka
    rider -->|"HTTPS, JWT"| tadka
    admin -->|"HTTPS, JWT"| tadka
    tadka -->|"charge and refund, HTTP"| pay
    tadka -->|"traces and metrics, OTLP"| obs
    admin -->|"dashboards"| obs

    classDef person fill:#eceff1,stroke:#455a64;
    classDef system fill:#bbdefb,stroke:#1565c0,stroke-width:3px;
    classDef ext fill:#e1bee7,stroke:#6a1b9a;
    class customer,owner,rider,admin person;
    class tadka system;
    class pay,obs ext;
```

**What to notice:** only one external business dependency (payments). There is no SMS, maps or search provider in the code.

---

## 2. Container diagram

**C4 level 2.** Every deployable piece, with its port. These are the ports used in the Day 16 runbook.

```mermaid
flowchart TB
    client["Client<br/>web or mobile"]

    subgraph edge ["Edge"]
        gw["API Gateway (YARP)<br/>:8080<br/>routing and rate limit"]
    end

    subgraph services ["Services (.NET 10)"]
        api["Tadka.Api<br/>Orders and Identity<br/>:5224"]
        rest["Tadka.Restaurant.Api<br/>menus<br/>:5260"]
        pay["Tadka.Payment.Api<br/>charges, refunds<br/>:5240"]
        del["Tadka.Delivery.Api<br/>riders, tracking<br/>:5250"]
    end

    subgraph data ["Data stores"]
        pgb["PgBouncer :6432"]
        pg[("Orders Postgres :5432<br/>schemas ordering, identity")]
        pgr[("Orders read replica :5433")]
        pgrest[("Restaurant Postgres :5436")]
        pgpay[("Payment Postgres :5434")]
        pgdel[("Delivery Postgres :5435")]
        redis[("Redis :6379<br/>cache, live tracking, rider location")]
    end

    kafka{{"Kafka :9092<br/>events between services"}}

    subgraph obs ["Observability"]
        otel["OTel Collector :4317"]
        jaeger["Jaeger :16686"]
        prom["Prometheus :9090"]
        graf["Grafana :3000"]
    end

    client --> gw
    gw --> api
    gw --> rest
    gw --> pay
    gw --> del

    api --> pgb --> pg
    pg -. "streaming replication" .-> pgr
    api -. "reads" .-> pgr
    rest --> pgrest
    pay --> pgpay
    del --> pgdel
    api --> redis
    rest --> redis
    del --> redis

    api <--> kafka
    rest <--> kafka
    pay <--> kafka
    del <--> kafka

    api -. OTLP .-> otel
    rest -. OTLP .-> otel
    pay -. OTLP .-> otel
    del -. OTLP .-> otel
    gw -. OTLP .-> otel
    otel --> jaeger
    otel --> prom --> graf

    classDef svc fill:#bbdefb,stroke:#1565c0;
    classDef db fill:#c8e6c9,stroke:#2e7d32;
    classDef cache fill:#ffcdd2,stroke:#c62828;
    classDef bus fill:#ffe0b2,stroke:#ef6c00;
    class api,rest,pay,del,gw svc;
    class pg,pgr,pgrest,pgpay,pgdel db;
    class redis cache;
    class kafka bus;
```

**What to notice:**
- Each service owns its own database. Nothing reaches into another service's database.
- Only Orders uses PgBouncer and a read replica.
- Kafka is the only way services talk to each other for business events.

---

## 3. Component diagram: inside the Orders service

**C4 level 3.** The parts inside `Tadka.Api`. Names are real classes and folders.

```mermaid
flowchart TB
    gw["API Gateway"] --> ctrl

    subgraph api ["Tadka.Api (Orders and Identity)"]
        ctrl["Controllers<br/>OrdersController, OrderTrackingController,<br/>UsersController, CouponsController,<br/>OrderInvoiceController, FeatureFlagsController"]
        auth["Auth<br/>JWT login, refresh tokens (rotating),<br/>account lockout, JWKS keys"]

        subgraph domain ["Domain"]
            factory["OrderFactory<br/>validates items, calculates the total"]
            order["Order (aggregate root)"]
            sm["OrderStateMachine<br/>legal state moves"]
            pricing["IRestaurantPricingSource<br/>LocalReplicaPricingSource (default)<br/>HttpRestaurantPricingSource (lever)"]
            events["Domain events and handlers<br/>OrderPaymentReactionHandlers,<br/>OrderStatusChangedTrackingHandler"]
        end

        subgraph infra ["Infrastructure"]
            db["TadkaDbContext (primary)<br/>TadkaReadDbContext (replica)"]
            relay["OutboxRelay<br/>FOR UPDATE SKIP LOCKED"]
            cons["Kafka consumers<br/>PaymentResults, PaymentRefunded,<br/>MenuUpdated, RestaurantResponse"]
            saga["RefundSagaOrchestrator"]
            track["RedisOrderTrackingBus<br/>SSE stream"]
            cache["RedisCacheService"]
            sec["FieldCipher (phone, AES-GCM)<br/>PiiMasker (log masking)"]
            rl["Redis rate limiter"]
        end
    end

    ctrl --> auth
    ctrl --> factory --> order --> sm
    factory --> pricing
    ctrl --> db
    order --> events --> track
    cons --> db
    cons --> saga
    relay --> db
    db --> pg[("Postgres")]
    track --> redis[("Redis")]
    cache --> redis
    rl --> redis
    pricing --> db
    relay --> kafka{{"Kafka"}}
    kafka --> cons

    classDef core fill:#bbdefb,stroke:#1565c0;
    classDef infraC fill:#fff9c4,stroke:#f9a825;
    class ctrl,auth,factory,order,sm,pricing,events core;
    class db,relay,cons,saga,track,cache,sec,rl infraC;
```

**What to notice:** the order and its outbox event are saved through the **same** `TadkaDbContext` in one transaction. The relay, not the controller, talks to Kafka.

---

## 4. Code diagram: the Order aggregate

**UML class diagram.** One module, real members from `src/Tadka.Api/Domain/Orders/`.

```mermaid
classDiagram
    class Order {
        +Guid Id
        +Guid CustomerId
        +Guid RestaurantId
        +OrderStatus Status
        +Money TotalAmount
        +Address DeliveryAddress
        +DateTime CreatedAt
        +DateTime? ConfirmedAt
        +DateTime? DeliveredAt
        +DateTime? CancelledAt
        +string? CancellationReason
        +IReadOnlyList~OrderItem~ Items
        +IReadOnlyList~IDomainEvent~ DomainEvents
        +Transition(OrderStatus) Result
        +Cancel(string reason) Result
    }
    class OrderItem {
        +Guid Id
        +Guid MenuItemId
        +string Name
        +int Quantity
        +Money UnitPrice
        +string? SpecialInstructions
    }
    class OrderStatus {
        <<enumeration>>
        Created
        Confirmed
        Preparing
        ReadyForPickup
        PickedUp
        Delivered
        Cancelled
        Refunded
    }
    class OrderStateMachine {
        <<static>>
        +CanTransition(current, next) bool
        +GetAllowedTransitions(current)
    }
    class OrderFactory {
        +Create(customerId, RestaurantPricing, items, Address) Result~Order~
    }
    class Money {
        <<record>>
        +decimal Amount
        +string Currency
    }
    class Address {
        <<record>>
        +string Line1
        +string Line2
        +string City
        +string Pincode
        +double Latitude
        +double Longitude
    }
    class RestaurantPricing {
        <<record>>
        +Guid Id
        +bool IsActive
        +IReadOnlyList~MenuItemPricing~ Menu
    }
    class IRestaurantPricingSource {
        <<interface>>
        +GetAsync(restaurantId) RestaurantPricing
    }
    class IDomainEvent {
        <<interface>>
    }

    Order "1" *-- "1..*" OrderItem : owns
    Order --> OrderStatus
    Order ..> OrderStateMachine : uses
    Order o-- Money
    Order o-- Address
    OrderItem o-- Money
    Order ..> IDomainEvent : raises
    OrderFactory ..> Order : creates
    OrderFactory ..> RestaurantPricing : prices from
    IRestaurantPricingSource ..> RestaurantPricing : returns
```

**What to notice:** every property on `Order` has a private setter. State changes only through `Transition` and `Cancel`, so the state machine cannot be bypassed. `Refunded` exists in the enum, but no transition leads into it yet.

---

## 5. Deployment diagram: Azure Container Apps

**Where each piece runs** in a live class session, created by `scripts/cloud-up.ps1` from `deploy/azure/*.tf`. Front Door is optional (a free trial cannot create it). `basic` and `ha` modes differ where noted.

```mermaid
flowchart TB
    user["Users"]

    subgraph azure ["Azure subscription: one resource group per session (rg-tadka-session)"]
        fd["Azure Front Door + WAF<br/>(optional, needs a paid subscription)"]

        subgraph vnet ["Virtual network vnet-tadka 10.40.0.0/16"]
            subgraph aca ["Container Apps environment, subnet 10.40.0.0/23"]
                gw["gateway app<br/>EXTERNAL ingress (HTTPS)"]
                subgraph internal ["Internal-only apps (http://name inside the environment)"]
                    api["tadka-api"]
                    rest["restaurant"]
                    pay["payment<br/>optional KEDA scaling on Kafka lag"]
                    del["delivery"]
                    kafka["kafka<br/>apache/kafka 3.8, 3 partitions, TCP 9092"]
                    redis["redis container<br/>(ha: primary + replica + 3 sentinels)"]
                    otel["otel collector"]
                end
            end
            subgraph pgnet ["Subnet 10.40.2.0/24, delegated, private DNS"]
                pgm[("Postgres Flexible Server v16<br/>4 databases, no public access")]
                pgh[("ha mode: zone-redundant standby<br/>+ 1 async read replica")]
            end
        end

        logs["Log Analytics workspace"]
        appi["Application Insights"]
        budget["Budget alert on the resource group"]
    end

    user --> fd --> gw
    user -. "without Front Door" .-> gw
    gw --> api
    gw --> rest
    gw --> pay
    gw --> del
    api --> pgm
    rest --> pgm
    pay --> pgm
    del --> pgm
    pgm -.-> pgh
    api --> kafka
    pay --> kafka
    api --> redis
    api -. OTLP .-> otel
    otel --> appi
    appi --> logs

    classDef pub fill:#ffe0b2,stroke:#ef6c00;
    classDef priv fill:#c8e6c9,stroke:#2e7d32;
    class fd,gw pub;
    class pgm,pgh priv;
```

**What to notice:** the gateway is the **only** app with a public address. Postgres has no public access at all. The session is deleted with `scripts/cloud-down.ps1`, and a budget alert guards the cost.

---

## 6. Sequence diagram: placing an order

**One scenario, end to end.** Topic names are the real Kafka topics. The numbers are the order of events.

```mermaid
sequenceDiagram
    autonumber
    actor C as Customer
    participant G as Gateway 8080
    participant O as Orders (Tadka.Api)
    participant DB as Orders DB
    participant K as Kafka
    participant P as Payment
    participant D as Delivery
    participant R as Restaurant

    C->>G: POST /api/v1/orders (JWT, Idempotency-Key)
    G->>O: forward (traceparent)
    O->>O: check token and ownership, price from menu_replica
    O->>DB: one transaction: order (Created) + outbox row + idempotency key
    O-->>G: 201 Created
    G-->>C: 201 Created

    Note over DB,K: the OutboxRelay publishes the row, carrying traceparent
    DB->>K: order-placed
    K->>P: order-placed
    P->>P: inbox check, tokenise card, bulkhead, retry, breaker, 2 s timeout
    P->>K: payment-results (Completed)
    K->>O: payment-results
    O->>DB: order Confirmed + outbox row (same transaction)
    DB->>K: order-confirmed
    K->>D: order-confirmed
    K->>R: order-confirmed
    D->>D: assign the first available rider
    R->>K: restaurant-response (Accepted)
    K->>O: restaurant-response
    O->>O: record in inbox (the order stays Confirmed)
```

**What to notice:** the customer gets the 201 **before** payment happens (step 4). The charge, confirmation and rider assignment run afterwards through Kafka. That is why the Day 16 load test found the queue, not the API, saturating first.

---

## 7. Flowchart: how a payment is processed

**One process, with its branches.** Real order of the layers in `PaymentResiliencePipeline.cs`: bulkhead, retry, circuit breaker, timeout.

```mermaid
flowchart TD
    start(["order-placed message arrives"]) --> inbox{"Already in the inbox?"}
    inbox -- "yes" --> skip["Skip (duplicate delivery)"]
    inbox -- "no" --> token["Tokenise the card (HMAC token, card number never stored)"]
    token --> bulk{"Bulkhead: fewer than 10<br/>charges in flight?"}
    bulk -- "no" --> rej["Rejected fast"]
    bulk -- "yes" --> attempt["Attempt (up to 2 retries, jittered)"]
    attempt --> brk{"Circuit breaker<br/>open?"}
    brk -- "open" --> fast["BrokenCircuitException<br/>fail fast, no network call"]
    brk -- "closed or half-open" --> gcall["Call the payment gateway<br/>2 second timeout"]
    gcall --> res{"Result"}
    res -- "approved" --> ok["Payment Completed"]
    res -- "declined" --> dec["Payment Failed<br/>a decline is final: no retry,<br/>not counted by the breaker"]
    res -- "timeout or unavailable" --> retry{"Retries left?"}
    retry -- "yes" --> attempt
    retry -- "no" --> unav
    fast --> unav{"Payment:OnGatewayUnavailable"}
    unav -- "Compensate (default)" --> comp["Save Failed"]
    unav -- "Buffer" --> buf["Keep the message, pause the consumer,<br/>charge after the gateway recovers"]
    ok --> pub["Publish payment-results"]
    dec --> pub
    comp --> pub
    rej --> unav

    classDef good fill:#c8e6c9,stroke:#2e7d32;
    classDef bad fill:#ffcdd2,stroke:#c62828;
    classDef warn fill:#fff9c4,stroke:#f9a825;
    class ok good;
    class dec,comp,fast,rej bad;
    class buf warn;
```

**What to notice:** a **decline** and an **outage** take different paths. Mixing them up (retrying a decline) is the bug the Day 14 predicate prevents.

---

## 8. State machine: the order

**One entity.** Straight from `OrderStateMachine.cs`.

```mermaid
stateDiagram-v2
    [*] --> Created : POST /orders
    Created --> Confirmed : payment Completed (saga)
    Created --> Cancelled : payment Failed or customer cancels
    Confirmed --> Preparing : status update by the restaurant
    Confirmed --> Cancelled : restaurant rejects (refund saga) or cancel
    Preparing --> ReadyForPickup
    ReadyForPickup --> PickedUp
    PickedUp --> Delivered
    Delivered --> [*]
    Cancelled --> [*]

    note right of Delivered
        Terminal. Refunded exists in the enum
        but no transition leads into it yet.
    end note
```

**What to notice:** cancel is legal only from `Created` or `Confirmed`. After the food is `Preparing`, the API returns **422** if you try to cancel.

---

## 9. ER diagram: the Orders database

**One database.** Tables from `src/Tadka.Api/Data/`. Dotted lines are links by **id only**: there is no foreign key across schemas (ADR-008).

```mermaid
erDiagram
    USERS ||--o{ USER_ADDRESSES : has
    USERS ||--o{ REFRESH_TOKENS : has
    ORDERS ||--|{ ORDER_ITEMS : contains
    ORDERS ||--o| IDEMPOTENCY_KEYS : "created by"
    COUPONS ||--o{ COUPON_REDEMPTIONS : "redeemed as"
    RESTAURANT_REPLICA ||--o{ MENU_REPLICA : lists
    USERS ||..o{ ORDERS : "customer_id, id only"
    RESTAURANT_REPLICA ||..o{ ORDERS : "restaurant_id, id only"
    ORDERS ||..o{ SAGA_INSTANCES : "order_id, id only"

    USERS {
        uuid Id PK
        string Email
        string PasswordHash
        string Role
        string Phone "encrypted at rest"
        int FailedLoginAttempts
        datetime LockedUntil
    }
    ORDERS {
        uuid Id PK
        uuid CustomerId
        uuid RestaurantId
        string Status
        decimal TotalAmount
        string Currency
        datetime CreatedAt
        datetime ConfirmedAt
        datetime CancelledAt
        string CancellationReason
        xid xmin "concurrency token"
    }
    ORDER_ITEMS {
        uuid Id PK
        uuid OrderId FK
        uuid MenuItemId
        string Name
        int Quantity
        decimal UnitPrice
    }
    IDEMPOTENCY_KEYS {
        string Key PK
        uuid OrderId
        datetime CreatedAt
    }
    OUTBOX_MESSAGES {
        uuid Id PK
        string Topic
        string Key
        string Payload
        string TraceParent
        datetime CreatedAt
        datetime ProcessedAt
    }
    INBOX_MESSAGES {
        uuid MessageId PK
        datetime ConsumedAt
    }
    RESTAURANT_REPLICA {
        uuid Id PK
        string Name
        bool IsActive
        datetime UpdatedAt
    }
    MENU_REPLICA {
        uuid MenuItemId PK
        uuid RestaurantId
        string Name
        decimal PriceAmount
        bool IsAvailable
    }
    SAGA_INSTANCES {
        uuid Id PK
        uuid OrderId
        string SagaType
        string CurrentStep
        string Status
    }
    COUPONS {
        uuid Id PK
    }
    COUPON_REDEMPTIONS {
        uuid Id PK
    }
    USER_ADDRESSES {
        uuid Id PK
    }
    REFRESH_TOKENS {
        uuid Id PK
    }
```

**What to notice:** `outbox_messages` and `inbox_messages` have no relation to `orders`. They are a separate channel written in the **same transaction**. The other services have their own databases: Payment (`payments`), Delivery (`agents`, `assignments`, `pending_assignments`, `cancelled_orders`), Restaurant (`restaurants`, `menu_items`, `order_decisions`). Each also has its own `outbox_messages` and `inbox_messages` where it publishes or consumes events.

---

## 10. Data flow diagram: where personal and money data goes

**For security and privacy reviews.** Circles are processes, cylinders are stores, rectangles are outside parties, dashed boxes are trust boundaries.

```mermaid
flowchart LR
    cust["Customer<br/>(external)"]
    pp["Payment provider<br/>(external)"]

    subgraph tb1 ["Trust boundary: Tadka services (each service validates the JWT itself)"]
        auth(("Login<br/>password hashed (PBKDF2)"))
        place(("Place order"))
        charge(("Charge<br/>card number to HMAC token at once"))
        logs(("Logging<br/>PiiMasker masks email and phone"))
        users[("users<br/>phone encrypted (AES-GCM)")]
        orders[("orders, order_items")]
        pays[("payments<br/>token and last 4 only")]
        bus[("Kafka events<br/>ids and amounts, no card data")]
    end

    cust -- "email, password" --> auth --> users
    cust -- "address, items, JWT" --> place --> orders
    place -- "order id, amount" --> bus
    bus -- "order id, amount" --> charge
    cust -. "card number (at the charge call)" .-> charge
    charge -- "token, last 4" --> pays
    charge -- "charge request" --> pp
    place --> logs
    auth --> logs

    classDef ext fill:#e1bee7,stroke:#6a1b9a;
    classDef store fill:#c8e6c9,stroke:#2e7d32;
    class cust,pp ext;
    class users,orders,pays,bus store;
```

**What to notice:** the card number exists only in memory during the charge call. It is turned into a keyed token immediately (ADR-053) and never stored or logged. Order ids go on traces and logs, never on metric labels (ADR-042). The right to be forgotten anonymises the user and keeps the money records (ADR-032).

---

## 11. Domain or context map

**DDD view.** Four bounded contexts, and how each relates to the others. Identity lives inside the Orders deployable as its own schema.

```mermaid
flowchart LR
    subgraph ordering ["Ordering context (Tadka.Api)"]
        o1["Order, OrderItem, Coupon"]
        i1["Identity (own schema)<br/>User, tokens"]
    end
    rest["Restaurant context<br/>Restaurant, MenuItem"]
    pay["Payment context<br/>Payment, refund"]
    del["Delivery context<br/>DeliveryAgent, Assignment"]

    rest == "menu-updated<br/>Customer/Supplier, snapshot event<br/>(Ordering keeps a read model)" ==> ordering
    ordering == "order-placed, refund-requested<br/>Partnership by events" ==> pay
    pay == "payment-results, payment-refunded" ==> ordering
    ordering == "order-confirmed" ==> del
    ordering == "order-confirmed" ==> rest
    rest == "restaurant-response<br/>(accept or reject)" ==> ordering
    pay == "payment-refunded" ==> del
    o1 --- i1

    classDef ctx fill:#e3f2fd,stroke:#1565c0;
    class rest,pay,del ctx;
```

**What to notice:** the arrows carry **events**, not function calls. Ordering never calls Restaurant for a price: it owns a copy (`menu_replica`), the integration pattern DDD calls a local read model fed by a published language. Orders and Identity are one deployable but two schemas (a shared kernel kept small on purpose).

---

## 12. Event storming board

**The business in time order.** Orange are events (past tense), blue are commands, yellow are actors, purple are reactions ("policies").

```mermaid
flowchart LR
    a1["Customer"]:::actor --> c1["Place order"]:::cmd --> e1["OrderPlaced<br/>(order-placed)"]:::event
    e1 --> p1["When an order is placed,<br/>charge the customer"]:::policy --> c2["Charge"]:::cmd
    c2 --> e2["PaymentCompleted"]:::event
    c2 --> e3["PaymentFailed"]:::event
    e2 --> p2["When payment completes,<br/>confirm the order"]:::policy --> e4["OrderConfirmed<br/>(order-confirmed)"]:::event
    e3 --> p3["When payment fails,<br/>cancel the order"]:::policy --> e5["OrderCancelled"]:::event
    e4 --> p4["When confirmed,<br/>find a rider"]:::policy --> e6["RiderAssigned<br/>(delivery-assigned)"]:::event
    e4 --> p5["When confirmed,<br/>ask the restaurant"]:::policy --> a2["Restaurant"]:::actor
    a2 --> e7["RestaurantAccepted"]:::event
    a2 --> e8["RestaurantRejected"]:::event
    e8 --> p6["When rejected after payment,<br/>refund"]:::policy --> e9["RefundRequested"]:::event --> e10["PaymentRefunded"]:::event
    a3["Restaurant owner"]:::actor --> c3["Change menu price"]:::cmd --> e11["MenuUpdated<br/>(menu-updated)"]:::event
    e11 --> p7["When a menu changes,<br/>update the local prices"]:::policy

    classDef event fill:#ffcc80,stroke:#e65100;
    classDef cmd fill:#90caf9,stroke:#1565c0;
    classDef actor fill:#fff59d,stroke:#f9a825;
    classDef policy fill:#ce93d8,stroke:#6a1b9a;
```

**What to notice:** the event names match real Kafka topics, so the board and the code tell the same story. A real event storming session is done with sticky notes and the business people, and the result looks like this before anyone draws a service boundary.

---

## 13. Network topology: Azure session

**What is public, private, and behind which boundary.** From `deploy/azure/main.tf`, `apps.tf` and `postgres.tf`.

```mermaid
flowchart TB
    internet(("Internet"))

    subgraph pub ["PUBLIC"]
        fd["Front Door + WAF<br/>(optional)<br/>origin header lock: X-Azure-FDID"]
        gwx["gateway app<br/>public HTTPS on *.azurecontainerapps.io"]
    end

    subgraph vnet ["PRIVATE: vnet-tadka 10.40.0.0/16"]
        subgraph s1 ["snet-container-apps 10.40.0.0/23"]
            inner["tadka-api, restaurant, payment, delivery,<br/>kafka (TCP 9092), redis, otel collector<br/>internal ingress only"]
        end
        subgraph s2 ["snet-postgres 10.40.2.0/24 (delegated)"]
            pgs[("Postgres Flexible Server<br/>public network access disabled<br/>private DNS zone")]
        end
    end

    internet --> fd --> gwx
    internet -. "if Front Door is off" .-> gwx
    gwx --> inner
    inner --> pgs

    classDef pubc fill:#ffe0b2,stroke:#ef6c00;
    classDef privc fill:#c8e6c9,stroke:#2e7d32;
    class fd,gwx pubc;
    class inner,pgs privc;
```

**What to notice:** exactly one thing is reachable from the internet. With Front Door on, the gateway answers **403** to any request that does not carry the right `X-Azure-FDID` header, so nobody can skip the WAF by calling the gateway address directly. Locally, the equivalent is simply `localhost` with ports.

---

## 14. Dependency graph

**Who calls whom.** Solid arrows are synchronous HTTP. Dashed arrows go through Kafka topics. The only **cycle** is Orders and Payment, and it is asynchronous.

```mermaid
flowchart LR
    client["Client"] --> gw["Gateway"]
    gw --> api["Orders"]
    gw --> rest["Restaurant"]
    gw --> pay["Payment"]
    gw --> del["Delivery"]
    pay --> ext["Payment gateway (HTTP)"]

    api -. "order-placed" .-> pay
    pay -. "payment-results" .-> api
    api -. "refund-requested" .-> pay
    pay -. "payment-refunded" .-> api
    pay -. "payment-refunded" .-> del
    api -. "order-confirmed" .-> del
    api -. "order-confirmed" .-> rest
    rest -. "menu-updated" .-> api
    rest -. "restaurant-response" .-> api
    del -. "delivery-assigned (no consumer yet)" .-> nobody["(nobody)"]

    api -.- redis[("Redis")]
    rest -.- redis
    del -.- redis

    linkStyle 6,7,8,9,10,11,12,13,14,15 stroke:#ef6c00,stroke-width:2px;
```

**What to notice:** there is **no** synchronous call between two business services. The `HttpRestaurantPricingSource` exists only as a lever (`Ordering:RestaurantReadMode=SyncHttp`) to show the coupling it would create. The `delivery-assigned` topic is published but nothing consumes it yet.

---

## 15. Trace waterfall: one real order

**One real trace**, captured from the running system (Jaeger) during the Day 16 load runs on a laptop. One order produced **13 spans across all 5 services**, in a single trace.

```mermaid
gantt
    title One real order: spans over time (milliseconds from the first request)
    dateFormat x
    axisFormat %L
    section Gateway
    POST /api/v1 (gateway)            :g1, 0, 391ms
    section Orders
    POST api/v1/orders                :o1, 5, 386ms
    outbox publish order-placed       :o2, 416, 21ms
    consume payment-results           :o3, 855, 50ms
    outbox publish order-confirmed    :o4, 956, 18ms
    consume restaurant-response       :o5, 1421, 9ms
    section Payment
    consume order-placed              :p1, 432, 222ms
    ProcessPayment                    :p2, 435, 211ms
    outbox publish payment-results    :p3, 847, 8ms
    section Delivery
    consume order-confirmed           :d1, 974, 14ms
    section Restaurant
    consume order-confirmed           :r1, 974, 11ms
    outbox publish restaurant-response :r2, 1360, 60ms
```

| Service | Span | Start (ms) | Duration (ms) |
|---|---|---:|---:|
| Gateway | POST /api/v1/{**remainder} | 0 | 391 |
| Orders | POST api/v1/orders | 5 | 386 |
| Orders | outbox publish order-placed | 416 | 21 |
| Payment | consume order-placed | 432 | 222 |
| Payment | ProcessPayment | 435 | 211 |
| Payment | outbox publish payment-results | 847 | 8 |
| Orders | consume payment-results | 855 | 50 |
| Orders | outbox publish order-confirmed | 956 | 18 |
| Delivery | consume order-confirmed | 974 | 14 |
| Restaurant | consume order-confirmed | 974 | 11 |
| Restaurant | outbox publish restaurant-response | 1,360 | 60 |
| Orders | consume restaurant-response | 1,421 | 9 |

**What to notice:** the customer's request ends at about **391 ms**. The charge, confirmation and restaurant response happen in the **next 1,000 ms** through Kafka. The trace was taken under load, so the 391 ms is higher than a quiet run would show. The gaps between spans are time spent in the queue.

---

## 16. Latency budget

**Where the milliseconds go**, using the same real trace. The promise to the customer is the **synchronous** part: order placement under **500 ms at p99**.

```mermaid
xychart-beta
    title "Duration of each step in one real order (ms)"
    x-axis ["Gateway extra", "Orders API", "Outbox publish", "Payment charge", "Publish result", "Confirm order", "Publish confirmed", "Delivery", "Restaurant", "Publish reply", "Orders reply"]
    y-axis "milliseconds" 0 --> 420
    bar [5, 386, 21, 211, 8, 50, 18, 14, 11, 60, 9]
```

| Part | Budget | Measured in this trace | Where |
|---|---|---|---|
| **Synchronous: customer waits** (gateway + Orders API) | **500 ms p99** | **391 ms** | One request through the gateway |
| Of which gateway overhead | small | about 5 ms | Gateway span minus Orders span |
| Asynchronous: charge, confirm, restaurant reply | no customer wait | about 1,000 ms after the 201 | Kafka hops |
| Largest async step | n/a | Payment charge, about 211 ms | `ProcessPayment` (fake gateway) |

**What to notice:** the synchronous budget is almost all spent inside the Orders API (386 of 391 ms). Anything added to that path (a new call, a new query) must fit in the remaining gap, so it is a budget decision. On a quiet machine the same call is a small fraction of this, because this trace was captured under k6 load.

---

## 17. Roadmap: the 16-day evolution

**In what order the system changed.** Each day answers one failure with one decision.

```mermaid
timeline
    title Tadka: from one monolith to four services in 16 days
    section Foundations
        Day 1 : Monolith first, requirements, ADR-002
        Day 2 : Domain model, schema per domain
        Day 3 : REST API v1, the order state machine
        Day 4 : Idempotency, optimistic concurrency, domain events
    section Scale one process
        Day 5 : Indexes, connection pool, read replica
        Day 6 : Redis cache, single-flight, live tracking with SSE
        Day 7 : Payment brownout, timeout and bulkhead, async payment
    section Split it
        Day 8 : Extract Payment, database per service
        Day 9 : Kafka, outbox and inbox, saga
        Day 10 : JWT, per-service authorization, PII
        Day 11 : Extract Delivery, YARP gateway, PgBouncer
        Day 12 : Extract Restaurant, read model, zero-downtime backfill
    section Operate it
        Day 13 : OpenTelemetry, one trace across services
        Day 14 : Circuit breaker, retries, graceful degradation
        Day 15 : System design masterclass, interview practice
        Day 16 : Load tests, cost model, cloud, portfolio
```

**What to notice:** nothing was split before a real force appeared. Payment left first because of **fault and PCI isolation**, Delivery for its **write profile**, and Restaurant because of **team ownership**.

---

## 18. Decision tree: should this be its own service?

**One choice.** The four valid triggers, with the Tadka extraction that used each one.

```mermaid
flowchart TD
    q0{"Is there a real force today,<br/>with a number?"}
    q0 -- "no" --> stay["Stay in the monolith<br/>ADR-002 (monolith first)<br/>Orders and Identity stayed together"]
    q0 -- "yes" --> q1{"Which force?"}
    q1 --> f1["A failure here must not<br/>stop everything else"]
    q1 --> f2["Regulated data<br/>(card data, PCI)"]
    q1 --> f3["Very different scaling<br/>or durability profile"]
    q1 --> f4["A separate team<br/>owns it (Conway)"]
    f1 --> pay["Extract: Payment<br/>Day 8, ADR-024"]
    f2 --> pay
    f3 --> del["Extract: Delivery<br/>Day 11, ADR-033<br/>latest-value writes, Redis geo"]
    f4 --> rest["Extract: Restaurant<br/>Day 12, ADR-036<br/>needs a read model first (ADR-037)"]
    pay --> after["Before you split:<br/>1. own database (ADR-026)<br/>2. talk by events (ADR-027, 028)<br/>3. a boundary test (ADR-008)"]
    del --> after
    rest --> after

    classDef stay fill:#c8e6c9,stroke:#2e7d32;
    classDef go fill:#bbdefb,stroke:#1565c0;
    class stay stay;
    class pay,del,rest go;
```

**What to notice:** "it is faster" or "it is modern" is not on the tree. Splitting for performance was explicitly **rejected** for Payment, because the in-process fix on Day 7 already solved the latency.

---

## 19. Wardley-style map: what is commodity and what is ours

**An opinion, not a measurement.** Mermaid has no Wardley map, so this is a quadrant chart: left to right is how mature the thing is in the market (genesis to commodity), bottom to top is how visible it is to the customer. The placement is the author's judgement and is **meant to be argued with**.

```mermaid
quadrantChart
    title Tadka components, by maturity and customer visibility (judgement)
    x-axis Genesis --> Commodity
    y-axis Invisible to customer --> Visible to customer
    quadrant-1 Build and differentiate
    quadrant-2 Build with care
    quadrant-3 Buy or run as commodity
    quadrant-4 Standardise
    Order flow and saga: [0.30, 0.90]
    Rider assignment: [0.25, 0.70]
    Menu read model: [0.35, 0.55]
    Payment resilience: [0.45, 0.62]
    Menu catalogue: [0.58, 0.80]
    API gateway: [0.72, 0.45]
    Login and tokens: [0.78, 0.50]
    Payment provider: [0.85, 0.65]
    Kafka: [0.82, 0.25]
    Redis: [0.90, 0.30]
    PostgreSQL: [0.94, 0.38]
    Observability stack: [0.82, 0.15]
    Container platform: [0.92, 0.08]
```

**How to read it:** items on the left are where the company's own judgement and code matter (the order flow, how riders are assigned, how the read model stays fresh). Items on the right are commodities: buy or run them, and do not build your own. The lesson for an architect is to spend design effort **on the left** and to **standardise on the right**.

---

*Back to:* [The Architect's Toolset](THE_ARCHITECTS_TOOLSET.md) · [Tadka field manual](README.md) · [DIAGRAMS.md](DIAGRAMS.md) (the earlier, shorter set).
