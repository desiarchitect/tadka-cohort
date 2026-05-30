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

    subgraph services["Microservices"]
        direction LR
        order["📦 Order<br/>Service"]
        restaurant["🍽️ Restaurant<br/>Service"]
        delivery["🚴 Delivery<br/>Service"]
        user["👤 User<br/>Service"]
        payment["💳 Payment<br/>Service"]
    end

    subgraph datastores["Data Stores"]
        direction LR
        pg_primary[("🐘 PostgreSQL<br/>Primary")]
        pg_replica[("📖 PostgreSQL<br/>Read Replica")]
        redis[("⚡ Redis 7<br/>Cache + Locks")]
    end

    subgraph messaging["Event Streaming"]
        kafka["📨 Apache Kafka<br/>Order Events · Delivery Events · Payment Events"]
    end

    subgraph observability["Observability"]
        direction LR
        prometheus["📊 Prometheus<br/>Metrics"]
        grafana["📈 Grafana<br/>Dashboards"]
        tempo["🔍 Tempo<br/>Traces"]
        loki["📝 Loki<br/>Logs"]
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
    yarp --> user
    yarp --> payment

    order --> pg_primary
    restaurant --> pg_primary
    user --> pg_primary
    payment --> pg_primary
    delivery --> pg_primary

    order -.->|reads| pg_replica
    restaurant -.->|reads| pg_replica

    order --> redis
    restaurant --> redis
    delivery --> redis

    pg_primary -.->|replication| pg_replica

    order --> kafka
    payment --> kafka
    delivery --> kafka
    kafka --> order
    kafka --> delivery
    kafka --> payment

    services --> prometheus
    prometheus --> grafana
    services --> tempo
    services --> loki

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
    style user fill:#1e3a5f,stroke:#3b82f6,color:#93c5fd
    style payment fill:#1e3a5f,stroke:#3b82f6,color:#93c5fd
    
    style pg_primary fill:#2d1f0e,stroke:#f59e0b,color:#fbbf24
    style pg_replica fill:#2d1f0e,stroke:#f59e0b,color:#fbbf24
    style redis fill:#2d1f0e,stroke:#ef4444,color:#fca5a5
    style kafka fill:#0f2918,stroke:#10b981,color:#6ee7b7
    style yarp fill:#1e1b4b,stroke:#8b5cf6,color:#c4b5fd
    style cdn fill:#1e293b,stroke:#06b6d4,color:#67e8f9
    style lb fill:#1e293b,stroke:#f97316,color:#fdba74

    style prometheus fill:#2d1a2e,stroke:#ec4899,color:#f9a8d4
    style grafana fill:#2d1a2e,stroke:#ec4899,color:#f9a8d4
    style tempo fill:#2d1a2e,stroke:#ec4899,color:#f9a8d4
    style loki fill:#2d1a2e,stroke:#ec4899,color:#f9a8d4
    
    style ecs fill:#1e1b4b,stroke:#6366f1,color:#a5b4fc
    style terraform fill:#1e1b4b,stroke:#6366f1,color:#a5b4fc
    style ghactions fill:#1e1b4b,stroke:#6366f1,color:#a5b4fc
    
    style mobile fill:#1e293b,stroke:#6366f1,color:#a5b4fc
    style web fill:#1e293b,stroke:#6366f1,color:#a5b4fc
```

## What to Tell Students

"Count the boxes. CDN, load balancer, API gateway, 5 services, primary database, read replica, Redis cache, Kafka event bus, 4 observability tools, containerized deployment with Infrastructure as Code and CI/CD.

You won't understand every box right now. That's the point.

By Week 8, you'll have built every single one of these. Not because someone told you to add them, but because you felt the pain that made each one necessary.

Week 2: Your menu listing is slow → you add Redis.
Week 4: Your monolith deploys are blocking each other → you extract Payment as a service.
Week 5: Your order placement is coupled to delivery assignment → you add Kafka.
Week 7: Something breaks in production and you can't figure out where → you add distributed tracing.

Every line on this diagram has a story. You'll live each one."

## Week-by-Week Box Count

| Week | New Boxes Added | Total |
|------|----------------|-------|
| 1 | API + PostgreSQL | 2 |
| 2 | Read Replica | 3 |
| 3 | Redis | 4 |
| 4 | MediatR (internal, not a box) | 4 |
| 5 | Kafka + 2 extracted services + YARP | 8 |
| 6 | Docker + ECS Fargate | 10 |
| 7 | CDN + ALB + Terraform + GitHub Actions | 14 |
| 8 | Prometheus + Grafana + Tempo + Loki + Polly | 19 |
