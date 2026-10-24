# Visual Architectural Blueprint Catalog

> **Desi Architect Cohort, Architecture Diagrams Collection**  
> All diagrams are authored in GitHub-flavored Mermaid.js for interactive rendering and version control.

---

## 1. System Evolution: Monolith to 4 Microservices + Gateway

This diagram illustrates the architectural state of Tadka as completed on Day 16:

```mermaid
graph TD
    Client["Clients (Web / Mobile / Tests)"] -->|":8080"| Gateway["Tadka.Gateway (YARP :8080)<br/>- Edge rate limit<br/>- Path routing<br/>- Not a trust boundary"]

    Gateway -->|"/api/v1/orders/**"| Monolith["Ordering Service (:5224)<br/>Postgres :5432 (Primary)<br/>Postgres :5433 (Replica)<br/>Local Read Models"]
    Gateway -->|"/api/v1/payments/**"| Payment["Payment Service (:5240)<br/>Postgres :5434 (payment-db)<br/>Polly Resilience Pipeline"]
    Gateway -->|"/api/v1/deliveries/**"| Delivery["Delivery Service (:5250)<br/>Postgres :5435 (delivery-db)<br/>Redis-Geo Location Tracking"]
    Gateway -->|"/api/v1/restaurants/**"| Restaurant["Restaurant Service (:5260)<br/>Postgres :5436 (restaurant-db)<br/>Redis Cache-Aside"]

    Monolith -.->|Kafka: order-placed| Kafka[("Kafka Event Backbone<br/>(Dual-listener :9092 / :29092)")]
    Payment -.->|Kafka: payment-results| Kafka
    Delivery -.->|Kafka: delivery-assigned| Kafka
    Restaurant -.->|Kafka: menu-updated| Kafka

    Kafka -.->|Consume| Monolith
    Kafka -.->|Consume| Payment
    Kafka -.->|Consume| Delivery
```

---

## 2. The Choreographed Saga Flow (Order Placement & Payment)

Illustrates the asynchronous event choreography between `Ordering`, `Payment`, and `Delivery` without a centralized orchestrator:

```mermaid
sequenceDiagram
    autonumber
    actor Customer
    participant Gateway as YARP Gateway (:8080)
    participant Orders as Ordering Service
    participant OrderDB as ordering.orders + Outbox
    participant Kafka as Kafka Backbone
    participant Payment as Payment Service
    participant Delivery as Delivery Service

    Customer->>Gateway: POST /api/v1/orders
    Gateway->>Orders: Forward Request
    Note over Orders,OrderDB: Transactional Write
    Orders->>OrderDB: 1. Save Order (status Created)<br/>2. Write Outbox (order-placed)
    Orders-->>Gateway: HTTP 201 Created (status Created)
    Gateway-->>Customer: HTTP 201 Created

    Note over OrderDB,Kafka: OutboxRelay (SKIP LOCKED)
    OrderDB->>Kafka: Publish "order-placed" (with traceparent)

    Kafka->>Payment: Consume "order-placed"
    Note over Payment: Inbox Deduplication Check
    Payment->>Payment: Process via Polly Pipeline<br/>(Bulkhead -> Retry -> Breaker -> Timeout)
    Payment->>Kafka: Publish "payment-results" (Status=Completed)

    Kafka->>Orders: Consume "payment-results"
    Orders->>OrderDB: Update Order (Confirmed) + Outbox (OrderConfirmed)
    OrderDB->>Kafka: Publish "order-confirmed"

    Kafka->>Delivery: Consume "order-confirmed"
    Delivery->>Delivery: Assign the first Available rider<br/>(live location is kept in Redis geo)
    Delivery->>Kafka: Publish "delivery-assigned"
```

---

## 3. Transactional Outbox + Relay + Inbox Deduplication Flow

Guarantees at-least-once publishing and exactly-once processing effect across distributed databases:

```mermaid
flowchart TD
    subgraph ServiceA ["Producer Service Boundary"]
        API["API Request Handler"] -->|Begin Txn| DBTxn[("Database Transaction")]
        DBTxn -->|1. Insert Entity| EntityTable[("Domain Table<br/>(e.g., ordering.orders)")]
        DBTxn -->|2. Insert Event| OutboxTable[("Transactional Outbox<br/>(Payload + TraceParent + CreatedAt)")]
        DBTxn -->|Commit Txn| AtomicCommit["Atomic DB Commit"]
        
        RelayWorker["Outbox Relay Worker"] -->|Poll with SELECT ... FOR UPDATE SKIP LOCKED| OutboxTable
        RelayWorker -->|Publish Event| Broker["Message Broker (Kafka Topic)"]
        Broker -->|Ack| RelayWorker
        RelayWorker -->|Update Status = Published| OutboxTable
    end

    subgraph ServiceB ["Consumer Service Boundary"]
        Broker -->|Deliver Message| Consumer["Message Consumer"]
        Consumer -->|Check MessageId| InboxTable[("Inbox Table<br/>(Unique MessageId)")]
        InboxTable -->|If Duplicate| Skip["Ack & Discard (Idempotent)"]
        InboxTable -->|If New| Process["Execute Domain Business Logic"]
        Process -->|Record MessageId| InboxTable
    end
```

---

## 4. Polly Resilience Pipeline (ADR-021, ADR-043)

The layered resilience pipeline protecting external synchronous calls (e.g. Payment Gateway):

```mermaid
flowchart LR
    Request["Incoming External Call"] --> Bulkhead["1. Bulkhead<br/>(Max 10 Concurrent)"]
    Bulkhead --> Retry["2. Jittered Retry<br/>(Transport Errors ONLY, Max 2 Retries)"]
    Retry --> Breaker["3. Circuit Breaker<br/>(Ratio 0.5, Min 5 calls, 30s window, 60s open)"]
    Breaker --> Timeout["4. Timeout<br/>(2.0s per attempt)"]
    Timeout --> Gateway["External Gateway / HTTP Endpoint"]

    classDef normal fill:#e1f5fe,stroke:#0288d1,stroke-width:2px;
    classDef warning fill:#fff3e0,stroke:#f57c00,stroke-width:2px;
    classDef danger fill:#ffebee,stroke:#d32f2f,stroke-width:2px;

    class Request,Timeout normal;
    class Bulkhead,Retry warning;
    class Breaker danger;
```

---

## 5. Event-Carried State Transfer & Local Read Models (ADR-037)

Eliminating temporal coupling on the order placement critical path:

```mermaid
flowchart TD
    subgraph RestaurantDomain ["Restaurant Domain (Owner of Truth)"]
        Admin["Restaurant Owner"] -->|Update Menu Price ₹299 -> ₹349| RestAPI["Restaurant Service"]
        RestAPI -->|Commit New Price| RestDB[("restaurant.menu_items")]
        RestAPI -->|Publish Snapshot| KafkaMenu["Kafka Topic: menu-updated"]
    end

    subgraph OrderingDomain ["Ordering Domain (Autonomous Read Model)"]
        KafkaMenu -->|Consume Event| MenuConsumer["MenuUpdatedConsumer"]
        MenuConsumer -->|Upsert Replica| LocalReplica[("ordering.menu_replica<br/>(Local Read Model)")]
        
        Customer["Customer Checkout"] -->|POST /api/v1/orders| OrderAPI["Orders Controller"]
        OrderAPI -->|Calculate Pricing| LocalReplica
        OrderAPI -->|Persist Order with Price ₹349| OrderDB[("ordering.orders")]
    end

    classDef domain1 fill:#e8f5e9,stroke:#388e3c,stroke-width:2px;
    classDef domain2 fill:#ede7f6,stroke:#512da8,stroke-width:2px;

    class RestaurantDomain domain1;
    class OrderingDomain domain2;
```

---

## 6. Distributed Trace Context Propagation (ADR-041)

Preserving end-to-end W3C trace identity across HTTP, Outbox, and Kafka:

```mermaid
sequenceDiagram
    autonumber
    participant Client
    participant OrderAPI as Ordering API
    participant Outbox as Outbox DB Table
    participant Relay as Outbox Relay
    participant Kafka as Kafka Topic
    participant PaymentAPI as Payment Consumer

    Client->>OrderAPI: HTTP Request (traceparent: 00-trace123-span001-01)
    Note over OrderAPI: Ambient Activity Source creates Span001
    OrderAPI->>Outbox: Save row with TraceParent = "00-trace123-span001-01"
    
    Relay->>Outbox: Read row
    Note over Relay: Start Producer Span002 (Child of Span001)
    Relay->>Kafka: Publish with Kafka Header: traceparent = "00-trace123-span002-01"

    Kafka->>PaymentAPI: Receive Kafka Message
    Note over PaymentAPI: Extract Header -> Start Consumer Span003 (Child of Span002)
    PaymentAPI->>PaymentAPI: Process Payment Logic (All linked to trace123!)
    Note over Client,PaymentAPI: Result: Jaeger visualizes 1 unified trace across 4 services
```

---

## 7. C4 Container Diagram: Tadka Production Architecture

Level 2 Container Diagram showing technologies, ports, and protocols:

```mermaid
graph TB
    User["Web / Mobile User"]
    Admin["Ops / Admin"]

    subgraph Boundary ["Tadka Architecture Boundary"]
        YARP["API Gateway (YARP)<br/>Port :8080<br/>Reverse Proxy & Routing"]
        
        Monolith["Ordering Service<br/>.NET 10 / Controllers<br/>Port :5224"]
        PaymentSvc["Payment Service<br/>.NET 10 / Web API<br/>Port :5240"]
        DeliverySvc["Delivery Service<br/>.NET 10 / Web API<br/>Port :5250"]
        RestaurantSvc["Restaurant Service<br/>.NET 10 / Web API<br/>Port :5260"]

        KafkaBrokers["Kafka KRaft Cluster<br/>Ports :9092 (Local) / :29092 (Internal)<br/>Topics: order-placed, payment-results, etc."]
        RedisNode["Redis Node<br/>Port :6379<br/>Cache-aside, Geo, Pub/Sub"]

        DBPrimary[("Postgres Primary<br/>Port :5432<br/>Schemas: ordering, identity")]
        DBReplica[("Postgres Read Replica<br/>Port :5433<br/>Streaming Replication")]
        DBPayment[("Payment DB<br/>Port :5434<br/>Schema: payment")]
        DBDelivery[("Delivery DB<br/>Port :5435<br/>Schema: delivery")]
        DBRestaurant[("Restaurant DB<br/>Port :5436<br/>Schema: restaurant")]

        OTel["OpenTelemetry Collector<br/>Port :4317 (gRPC) / :4318 (HTTP)"]
        Jaeger["Jaeger Tracing<br/>Port :16686"]
        Prometheus["Prometheus TSDB<br/>Port :9090"]
        Grafana["Grafana Dashboards<br/>Port :3000"]
    end

    User -->|HTTPS| YARP
    Admin -->|HTTP| Grafana
    Admin -->|HTTP| Jaeger

    YARP -->|HTTP| Monolith
    YARP -->|HTTP| PaymentSvc
    YARP -->|HTTP| DeliverySvc
    YARP -->|HTTP| RestaurantSvc

    Monolith -->|Write| DBPrimary
    Monolith -->|Read| DBReplica
    PaymentSvc -->|Read/Write| DBPayment
    DeliverySvc -->|Read/Write| DBDelivery
    RestaurantSvc -->|Read/Write| DBRestaurant

    RestaurantSvc -->|Cache| RedisNode
    DeliverySvc -->|Geo Locations| RedisNode
    Monolith -->|Live tracking pub/sub| RedisNode

    Monolith <-->|Pub/Sub| KafkaBrokers
    PaymentSvc <-->|Pub/Sub| KafkaBrokers
    DeliverySvc <-->|Pub/Sub| KafkaBrokers
    RestaurantSvc <-->|Pub/Sub| KafkaBrokers

    Monolith -.->|OTLP| OTel
    PaymentSvc -.->|OTLP| OTel
    DeliverySvc -.->|OTLP| OTel
    RestaurantSvc -.->|OTLP| OTel

    OTel --> Jaeger
    OTel --> Prometheus
    Prometheus --> Grafana
```
