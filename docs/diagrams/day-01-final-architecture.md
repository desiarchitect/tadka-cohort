# D1-4b: Tadka Final Architecture (Week 8)

> "THIS is where we'll be in 8 weeks. But we'll earn every single box on this diagram."

## Diagram

```mermaid
graph TB
    subgraph clients["Client Apps"]
        mobile["📱 Mobile App"]
        web["🌐 Web App"]
    end

    cdn["🌍 CDN<br/>CloudFront<br/>(images, static assets)"]

    lb["⚖️ ALB<br/>(Load Balancer)"]

    subgraph gateway["API Gateway"]
        yarp["🔀 YARP<br/>(.NET 10)<br/>Routing · Rate Limiting · Auth"]
    end

    subgraph services["4 Services"]
        direction LR
        order["📦 Ordering Service<br/>(orders, pricing, identity)"]
        restaurant["🍽️ Restaurant<br/>Service"]
        delivery["🚴 Delivery<br/>Service"]
        payment["💳 Payment<br/>Service"]
    end

    subgraph datastores["Data Stores — database per service"]
        direction LR
        ordering_db[("🐘 Ordering DB<br/>Primary")]
        ordering_replica[("📖 Ordering DB<br/>Read Replica")]
        restaurant_db[("🐘 Restaurant DB")]
        delivery_db[("🐘 Delivery DB")]
        payment_db[("🐘 Payment DB")]
        redis[("⚡ Redis 7<br/>Cache · Locks · Geo")]
    end

    subgraph messaging["Event Streaming"]
        kafka["📨 Apache Kafka<br/>order-placed · payment-result<br/>delivery-assigned · menu-updated"]
    end

    subgraph observability["Observability"]
        direction LR
        otel["🔭 OpenTelemetry<br/>Collector"]
        prometheus["📊 Prometheus<br/>Metrics"]
        grafana["📈 Grafana<br/>Dashboards"]
        jaeger["🔍 Jaeger<br/>Traces"]
    end

    subgraph deployment["Deployment"]
        ecs["☁️ AWS ECS Fargate<br/>Containers"]
        terraform["🏗️ Terraform<br/>IaC"]
        ghactions["⚙️ GitHub Actions<br/>CI/CD"]
    end

    mobile --> cdn
    web --> cdn
    mobile --> lb
    web --> lb
    lb --> yarp
    yarp --> order
    yarp --> restaurant
    yarp --> delivery
    yarp --> payment

    order --> ordering_db
    restaurant --> restaurant_db
    delivery --> delivery_db
    payment --> payment_db

    order -.->|reads| ordering_replica
    ordering_db -.->|replication| ordering_replica

    order --> redis
    restaurant --> redis
    delivery -.->|geo| redis

    order --> kafka
    payment --> kafka
    delivery --> kafka
    restaurant --> kafka
    kafka --> order
    kafka --> delivery
    kafka --> payment

    services -.-> otel
    otel --> prometheus
    otel --> jaeger
    prometheus --> grafana

    services --> ecs
    ecs --> terraform
    terraform --> ghactions

    style clients fill:#0f172a,stroke:#334155,color:#f8fafc
    style gateway fill:#1e293b,stroke:#8b5cf6,stroke-width:2px,color:#f8fafc
    style services fill:#1e293b,stroke:#3b82f6,stroke-width:2px,color:#f8fafc
    style datastores fill:#1e293b,stroke:#f59e0b,stroke-width:2px,color:#f8fafc
    style messaging fill:#1e293b,stroke:#10b981,stroke-width:2px,color:#f8fafc
    style observability fill:#1e293b,stroke:#ec4899,stroke-width:2px,color:#f8fafc
    style deployment fill:#1e293b,stroke:#6366f1,stroke-width:2px,color:#f8fafc

    style order fill:#1e3a5f,stroke:#3b82f6,color:#93c5fd
    style restaurant fill:#1e3a5f,stroke:#3b82f6,color:#93c5fd
    style delivery fill:#1e3a5f,stroke:#3b82f6,color:#93c5fd
    style payment fill:#1e3a5f,stroke:#3b82f6,color:#93c5fd

    style ordering_db fill:#2d1f0e,stroke:#f59e0b,color:#fbbf24
    style ordering_replica fill:#2d1f0e,stroke:#f59e0b,color:#fbbf24
    style restaurant_db fill:#2d1f0e,stroke:#f59e0b,color:#fbbf24
    style delivery_db fill:#2d1f0e,stroke:#f59e0b,color:#fbbf24
    style payment_db fill:#2d1f0e,stroke:#f59e0b,color:#fbbf24
    style redis fill:#2d1f0e,stroke:#ef4444,color:#fca5a5
    style kafka fill:#0f2918,stroke:#10b981,color:#6ee7b7
    style yarp fill:#1e1b4b,stroke:#8b5cf6,color:#c4b5fd
    style cdn fill:#1e293b,stroke:#06b6d4,color:#67e8f9
    style lb fill:#1e293b,stroke:#f97316,color:#fdba74

    style otel fill:#2d1a2e,stroke:#ec4899,color:#f9a8d4
    style prometheus fill:#2d1a2e,stroke:#ec4899,color:#f9a8d4
    style grafana fill:#2d1a2e,stroke:#ec4899,color:#f9a8d4
    style jaeger fill:#2d1a2e,stroke:#ec4899,color:#f9a8d4

    style ecs fill:#1e1b4b,stroke:#6366f1,color:#a5b4fc
    style terraform fill:#1e1b4b,stroke:#6366f1,color:#a5b4fc
    style ghactions fill:#1e1b4b,stroke:#6366f1,color:#a5b4fc

    style mobile fill:#1e293b,stroke:#6366f1,color:#a5b4fc
    style web fill:#1e293b,stroke:#6366f1,color:#a5b4fc
```

## What to Tell Students

"Count the boxes. CDN, load balancer, API gateway, **4 services** (Ordering, Restaurant, Delivery, Payment), a database *per service*, a read replica, Redis, Kafka, 3 observability tools, containerized deployment with Infrastructure as Code and CI/CD.

You won't understand every box right now. That's the point.

By Week 8, you'll have built every single one of these. Not because someone told you to add them, but because you felt the pain that made each one necessary.

Week 2: Your menu listing is slow → you add Redis.
Week 4: Your monolith deploys are blocking each other → you extract Payment as a service.
Week 5: Your order placement is coupled to delivery assignment → you add Kafka.
Week 7: Something breaks in production and you can't figure out where → you add distributed tracing.

Every line on this diagram has a story. You'll live each one."

**There is no separate User service.** Identity stays inside Ordering. If a student counts five services, that is the moment to say: extraction needs a *trigger* — fault isolation, a distinct scaling profile, org ownership — and identity never earned one. This diagram is the canonical answer; the count is always **four services plus a gateway**.

## Week-by-Week Box Count

| Week | New Boxes Added | Total |
|------|----------------|-------|
| 1 | API + PostgreSQL | 2 |
| 2 | Read Replica | 3 |
| 3 | Redis | 4 |
| 4 | Payment extracted + own DB | 6 |
| 5 | Kafka + Delivery extracted + own DB | 9 |
| 6 | Restaurant extracted + own DB + YARP Gateway | 12 |
| 7 | CDN + ALB + Terraform + GitHub Actions + Docker + ECS Fargate | 18 |
| 8 | OpenTelemetry + Prometheus + Grafana + Jaeger | 22 |
