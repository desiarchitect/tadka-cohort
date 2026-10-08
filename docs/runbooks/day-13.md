# Day 13: Runbook: Observability for the 4-service system (OpenTelemetry, Jaeger, Prometheus, Grafana)

**Branch:** `day-13`  ·  **What changed since Day 12:** [`docs/changelog.md`](../changelog.md).

Tadka's choreographed Kafka saga (Ordering → Payment → Delivery → Restaurant) across **4 services + an API gateway** is now **visible**. Three pillars of observability are instrumented once with the vendor-neutral **OpenTelemetry (OTel)** standard ([ADR-040](../adrs/040-observability-opentelemetry-otlp.md)), exported over OTLP to a lean OSS stack: **OpenTelemetry Collector → Jaeger (distributed traces) + Prometheus (RED metrics) + Grafana (dashboards & alerting)**. Structured logs are emitted as machine-readable JSON to the console, enriched with `trace_id` and `span_id`. Trace context crosses **both Kafka and the Transactional Outbox** via W3C `traceparent` headers ([ADR-041](../adrs/041-trace-context-propagation-http-kafka-outbox.md)), and metrics are strictly bounded to **low-cardinality** dimensions ([ADR-042](../adrs/042-metrics-cardinality-limits.md)).

> **Demo password:** `Password123!` for customer Priya (`priya@tadka.test`) and Admin (`admin@tadka.test`).  
> **Grafana credentials:** `admin` / `admin`.

Every command below is given twice, bash first and PowerShell second, wherever the two shells differ. They are not the same commands with `curl` swapped for `curl.exe`: bash's `VAR=$(...)`, `sed -E`, and inline `VAR=value command` syntax do not run in plain PowerShell at all. Windows PowerShell 5.1 also cannot read an HTTP status code off a 4xx/5xx response without the call throwing, so the small `Get-StatusCode` helper defined in section 1 is reused throughout. Every bash and PowerShell block here was run live against this branch (the five services, Postgres, Kafka, Redis, PgBouncer, OTel Collector, Jaeger, Prometheus, and Grafana all up) before being written down.

> **Using Git Bash on Windows?** Git Bash rewrites any argument that looks like a Unix path, so `docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh ...` fails with `C:/Program Files/Git/opt/kafka/...: no such file`. Run `export MSYS_NO_PATHCONV=1` once per terminal first (WSL, macOS and Linux do not need it). Git Bash's `curl` may also exit with code 23 after printing the right answer when you use `-o /dev/null`; the printed status is still correct.

---

## 1. Start the stack

### Demo Day Fresh Reset (Clean Slate)
To start from a clean slate (wiping every volume, lingering test containers, and Prometheus TSDB series). **Do this if you want a guaranteed clean baseline**:

**Bash:**
```bash
docker compose --profile observability down -v --remove-orphans && docker compose --profile observability up -d
until docker inspect tadka-kafka --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
until docker inspect tadka-restaurant-db --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
docker compose ps
```

**PowerShell:**
```powershell
docker compose --profile observability down -v --remove-orphans; docker compose --profile observability up -d
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-kafka --format "{{.State.Health.Status}}") -eq "healthy")
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-restaurant-db --format "{{.State.Health.Status}}") -eq "healthy")
docker compose ps
```

*(This wipes volume directories `pgdata`, `pgdata_replica`, `pgdata_payment`, `pgdata_delivery`, `pgdata_restaurant`, and brings up the 13 containers fresh from scratch).*

---

### Standard Launch
If starting existing containers without wiping data:

**What this does.** [`docker-compose.yml`](../../docker-compose.yml) with `--profile observability` starts the 9 core infrastructure containers (monolith Postgres 5432 + replica 5433, `payment-db` 5434, `delivery-db` 5435, `restaurant-db` 5436, Redis 6379, Kafka 9092, Kafka UI 8090, PgBouncer 6432) plus the **4 observability containers**:
- **`tadka-otel-collector`** (`:4317` gRPC / `:4318` HTTP / `:8889` Prometheus exporter): Receives OTLP telemetry from apps, batches it, and fans out traces to Jaeger and metrics to Prometheus.
- **`tadka-jaeger`** (`:16686` UI): Distributed trace storage and waterfall visualization.
- **`tadka-prometheus`** (`:9090` UI): Time-series database scraping the Collector's `:8889` endpoint every 5s ([`prometheus.yml`](../../docker/observability/prometheus.yml)).
- **`tadka-grafana`** (`:3000` UI): Pre-provisioned datasources, RED dashboard, and alert rules.

```bash
git checkout day-13
docker compose --profile observability up -d
until docker inspect tadka-kafka --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
until docker inspect tadka-restaurant-db --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
docker compose ps
```
```powershell
git checkout day-13
docker compose --profile observability up -d
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-kafka --format "{{.State.Health.Status}}") -eq "healthy")
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-restaurant-db --format "{{.State.Health.Status}}") -eq "healthy")
docker compose ps
```

**Build once before starting the apps.** All services reference the new `Tadka.Telemetry` library. Running one `dotnet build` first prevents file contention errors on `Tadka.Telemetry.dll`:
```bash
dotnet build Tadka.slnx
```
```powershell
dotnet build Tadka.slnx
```

**Pre-create the eight Kafka topics this branch uses.**  
Kafka requires SASL credentials on Day 13 (`--command-config /etc/kafka/docker/client.properties`). Running this topic pre-creation loop prevents `Subscribed topic not available` warnings when consumers launch:
```bash
for t in order-placed payment-results order-confirmed delivery-assigned refund-requested payment-refunded menu-updated restaurant-response; do
  docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
done
```
```powershell
foreach ($t in "order-placed","payment-results","order-confirmed","delivery-assigned","refund-requested","payment-refunded","menu-updated","restaurant-response") {
  docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
}
```

### Launch the Five Services
Telemetry export is **gated** on the environment variable `OTEL_EXPORTER_OTLP_ENDPOINT`. When this variable is unset, apps run in standalone/test mode without attempting network export to the Collector. **Set it in every terminal before running each service**:

Five processes, five terminals:
```bash
# Terminal 1: Restaurant Service (:5260)
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
dotnet run --project src/Tadka.Restaurant.Api

# Terminal 2: Payment Service (:5240)
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
dotnet run --project src/Tadka.Payment.Api

# Terminal 3: Delivery Service (:5250)
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
dotnet run --project src/Tadka.Delivery.Api

# Terminal 4: Ordering Monolith (:5224)
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
dotnet run --project src/Tadka.Api

# Terminal 5: API Gateway (:8080)
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
dotnet run --project src/Tadka.Gateway
```

```powershell
# Terminal 1: Restaurant Service (:5260)
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Restaurant.Api

# Terminal 2: Payment Service (:5240)
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Payment.Api

# Terminal 3: Delivery Service (:5250)
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Delivery.Api

# Terminal 4: Ordering Monolith (:5224)
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Api

# Terminal 5: API Gateway (:8080)
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Gateway
```

Observability web portals:
- **Jaeger UI:** [http://localhost:16686](http://localhost:16686)
- **Prometheus UI:** [http://localhost:9090](http://localhost:9090)
- **Grafana UI:** [http://localhost:3000](http://localhost:3000) (User: `admin`, Password: `admin`)
- **Kafka UI:** [http://localhost:8090](http://localhost:8090)

---

> **After a service restart, wait about 40 seconds before the first order is processed.** Kafka consumers that were killed (Ctrl+C or `Stop-Process`) do not say goodbye to the broker; the group coordinator keeps their seat until the session expires (about 45 seconds by default), and only then hands the partition to the new process. An order placed in that window simply waits in Kafka, and you will see it as a long empty gap before the `consume` span in Jaeger (Section 3, "Latency"). It is not a bug, and it is a good thing to point at in the waterfall.

> **Kafka after a Docker or laptop restart.** `docker-compose.yml` keeps the broker on `restart: unless-stopped`, and [`docker/kafka-scram-entrypoint.sh`](../../docker/kafka-scram-entrypoint.sh) now wipes its own storage on every start, so a restarted container comes back healthy (topics are gone, because the broker has no volume by design; re-run the topic loop above). If you ever see `Log directory /tmp/kafka-logs is already formatted` in a restart loop, you are on an older copy of that script: `docker compose up -d --force-recreate kafka` fixes it once.

### PowerShell helper: reading a status code without the call throwing
Define this once in the terminal where you execute test commands:
```powershell
function Get-StatusCode {
    param($Uri, $Method = "GET", $Headers = @{}, $Body = $null, $ContentType = "application/json")
    try {
        $params = @{ Uri = $Uri; Method = $Method; Headers = $Headers; UseBasicParsing = $true }
        if ($Body) { $params.Body = $Body; $params.ContentType = $ContentType }
        $r = Invoke-WebRequest @params
        return [int]$r.StatusCode
    } catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        else { throw }
    }
}
```

### Shared setup: Customer Priya Token & Order Payload
```bash
TOKEN=$(curl -s -X POST http://localhost:8080/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
BODY='{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":2}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}'
```
```powershell
$TOKEN = (Invoke-RestMethod -Uri http://localhost:8080/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$H = @{ Authorization = "Bearer $TOKEN" }
$BODY = '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":2}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}'
```

---

## 2. Architecture & Wiring: The 3 Pillars through OTLP

Prior to Day 13, the choreographed saga was functionally working but completely blind. When an order was stuck, operators had to open five separate terminal windows and manually match log messages.

Day 13 introduces the three pillars of observability using the OpenTelemetry standard ([ADR-040](../adrs/040-observability-opentelemetry-otlp.md)):
1. **Traces:** *Where did the request go across boundaries?* Shows the unified waterfall of the distributed transaction.
2. **Metrics:** *How fast and how healthy is the fleet?* Aggregated rates, error percentages, and latency percentiles.
3. **Logs:** *What exactly happened?* Low-level details emitted as structured JSON to stdout, enriched with `trace_id` and `span_id`.

```mermaid
flowchart TD
    Client["Client / Browser"] -->|HTTP /orders| GW["Tadka.Gateway (:8080)"]
    GW -->|HTTP traceparent| API["Tadka.Api Monolith (:5224)"]
    
    subgraph Saga ["Asynchronous Choreographed Saga"]
        API -->|Outbox enqueue with traceparent| DB[(Postgres Outbox)]
        DB -->|OutboxRelay publish| K1[Kafka: order-placed]
        K1 -->|Kafka Header traceparent| PAY["Tadka.Payment.Api (:5240)"]
        PAY -->|OutboxRelay publish| K2[Kafka: payment-results]
        K2 -->|Kafka Header traceparent| API2["Tadka.Api (PaymentResultsConsumer)"]
        API2 -->|OutboxRelay publish| K3[Kafka: order-confirmed]
        K3 -->|Kafka Header traceparent| DEL["Tadka.Delivery.Api (:5250)"]
        K3 -->|Kafka Header traceparent| RST["Tadka.Restaurant.Api (:5260)"]
    end

    GW -.->|OTLP gRPC :4317| Collector["tadka-otel-collector (:4317)"]
    API -.->|OTLP gRPC :4317| Collector
    PAY -.->|OTLP gRPC :4317| Collector
    DEL -.->|OTLP gRPC :4317| Collector
    RST -.->|OTLP gRPC :4317| Collector

    Collector -->|Fan-out traces| JAEGER["tadka-jaeger (:16686)"]
    Collector -->|Expose metrics :8889| PROM["tadka-prometheus (:9090)"]
    PROM --> GRAFANA["tadka-grafana (:3000)"]
```

### Context Propagation across the Transactional Outbox (ADR-041)
In synchronous HTTP calls, `HttpClient` automatically propagates the W3C `traceparent` header. However, in our event-driven architecture, services write to a **Transactional Outbox** table inside the local database transaction. 

By the time the background `OutboxRelay` worker polls the database and publishes to Kafka:
1. The original HTTP request has completed and returned HTTP `201 Created` to the client.
2. The ambient thread's `Activity.Current` is gone.

**The Fix (ADR-041):**
- When enqueuing an outbox message, we capture `Activity.Current?.Id` (a W3C `traceparent` string) with `TadkaTrace.CurrentTraceParent()` and write it to the `TraceParent` column of the outbox row: `ordering.outbox_messages`, `payment.outbox_messages` and `restaurant.outbox_messages` ([`OrdersController.cs`](../../src/Tadka.Api/Controllers/OrdersController.cs), [`OutboxMessage.cs`](../../src/Tadka.Payment.Api/Data/OutboxMessage.cs)). It commits in the same transaction as the business row.
- When `OutboxRelay` picks up the row (`FOR UPDATE SKIP LOCKED`), it parses that string back with `TadkaTrace.ParseContext(message.TraceParent)` and starts a child span `outbox publish <topic>` ([`OutboxRelay.cs`](../../src/Tadka.Api/Infrastructure/Messaging/OutboxRelay.cs)).
- The relay then puts that span's id on the Kafka message as a `traceparent` header ([`KafkaProducer.PublishRawAsync`](../../src/Tadka.Api/Infrastructure/Messaging/KafkaProducer.cs): `message.Headers = new Headers { { TadkaTrace.TraceParentHeader, Encoding.UTF8.GetBytes(traceParent) } }`).
- Each consumer (`OrderPlacedConsumer`, `PaymentResultsConsumer`, `OrderConfirmedConsumer`, and the others) reads the header with its own small `ReadTraceParent(cr)`, turns it into a context with `TadkaTrace.ParseContext(...)`, and starts `consume <topic>` as a remote child. A missing or garbled header degrades to a brand-new root trace, never an exception.

---

## 3. Demo 1: The Saga in One Trace (Distributed Tracing in Jaeger)

**What you're proving:** The choreographed Kafka saga—which crossed 5 operating system processes, 2 databases, and 3 Kafka topics—is stitched into a single, continuous waterfall trace. When a step fails, the trace immediately pinpoints the culprit.

### Baseline: Happy Path Saga (13 spans across 5 services)
Place an order for 2 × Chicken Biryani at Meghana Foods (₹598) through the API Gateway:

**Bash:**
```bash
ORDER=$(curl -s -X POST http://localhost:8080/api/v1/orders \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "$BODY")
echo "$ORDER" | sed -E 's/.*"status":"([^"]+)".*"totalAmount":\{"amount":([0-9.]+).*/status: \1  total: \2/'
ID=$(echo "$ORDER" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
```

**PowerShell:**
```powershell
$order = Invoke-RestMethod -Uri http://localhost:8080/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY
"status: " + $order.status + "  total: " + $order.totalAmount.amount
```

**Captured live:** `status: Created  total: 598.00`

Wait 2 seconds for the background saga to complete, then inspect the order status:

**Bash:**
```bash
sleep 2
curl -s http://localhost:8080/api/v1/orders/$ID -H "Authorization: Bearer $TOKEN" | grep -o '"status":"[^"]*"'
```

**PowerShell:**
```powershell
Start-Sleep -Seconds 2
$status = (Invoke-RestMethod -Uri "http://localhost:8080/api/v1/orders/$($order.id)" -Method Get -Headers $H).status
"Final Order Status: $status"
```
**Captured live:** `Final Order Status: Confirmed` (bash prints it as `"status":"Confirmed"`).

#### Inspecting the Trace in Jaeger UI
1. Open **[http://localhost:16686](http://localhost:16686)**.
2. In the left navigation, select **Service:** `Tadka.Gateway`.
3. Select **Operation:** `POST` (or leave as all operations).
4. Click **Find Traces**.
5. Click the top trace (duration typically 1.5s – 2.5s).

You will see one waterfall with **13 spans across 5 services** (Gateway, Ordering, Payment, Delivery, Restaurant). The `Tadka.Api` rows are the monolith (Ordering). Timings are from a warm run:

| # | Service | Span name | Kind | Starts at | Takes | Notes |
|---|---|---|---|---|---|---|
| 1 | `Tadka.Gateway` | `POST /api/v1/{**remainder}` | server | 0 ms | 28 ms | `http.response.status_code=201` |
| 2 | `Tadka.Gateway` | `POST` | client | 1 ms | 27 ms | the YARP forward |
| 3 | `Tadka.Api` | `POST api/v1/orders` | server | 1 ms | 27 ms | `order.id=<guid>` |
| 4 | `Tadka.Api` | `outbox publish order-placed` | producer | 461 ms | 61 ms | the relay, after its 500 ms poll |
| 5 | `Tadka.Payment.Api` | `consume order-placed` | consumer | 522 ms | 225 ms | child of span 4, across Kafka |
| 6 | `Tadka.Payment.Api` | `ProcessPayment` | internal | 531 ms | 208 ms | `payment.amount=598.00`, `payment.status=success` |
| 7 | `Tadka.Payment.Api` | `outbox publish payment-results` | producer | 1145 ms | 9 ms | |
| 8 | `Tadka.Api` | `consume payment-results` | consumer | 1154 ms | 22 ms | the order is confirmed here |
| 9 | `Tadka.Api` | `outbox publish order-confirmed` | producer | 1553 ms | 60 ms | |
| 10 | `Tadka.Delivery.Api` | `consume order-confirmed` | consumer | 1613 ms | 218 ms | assigns a rider |
| 11 | `Tadka.Restaurant.Api` | `consume order-confirmed` | consumer | 1613 ms | 14 ms | runs in parallel with Delivery |
| 12 | `Tadka.Restaurant.Api` | `outbox publish restaurant-response` | producer | 1696 ms | 58 ms | |
| 13 | `Tadka.Api` | `consume restaurant-response` | consumer | 1755 ms | 10 ms | |

Three things to read off this table, because they are the whole lesson:
- The customer's HTTP call (spans 1 to 3) is over after **28 ms**. The saga it started keeps going for another **1.7 seconds**. A trace is not the same thing as an HTTP request.
- The empty gaps between spans (for example from 739 ms to 1145 ms, after `ProcessPayment`) are the time a message waits for the next Outbox relay poll (every 500 ms) and for Kafka. Nothing is "slow" there; it is waiting.
- Spans 10 and 11 start at the same moment: Delivery and Restaurant both consume `order-confirmed`. Parallel work shows up as siblings, not as a chain.

Span attributes you can rely on are the ones the code sets: `order.id`, `payment.amount`, `payment.status` ([`PaymentService.cs`](../../src/Tadka.Payment.Api/PaymentService.cs)) and the HTTP ones the ASP.NET Core instrumentation adds. `ProcessPayment` is a custom business span; everything else comes from the HTTP, Outbox and consumer instrumentation.

The same trace id appears in every service's log, which Section 5 shows.

### Latency: a slow payment provider
The Day 7 `Slow` behaviour makes the fake gateway sleep. Keep the delay under the 2 second Polly timeout so the charge still succeeds, only late. Restart Payment:

**Bash:**
```bash
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
export Payment__Gateway__Behavior="Slow"
export Payment__Gateway__SlowDelaySeconds="1.5"
dotnet run --project src/Tadka.Payment.Api
```

**PowerShell:**
```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
$env:Payment__Gateway__Behavior = "Slow"
$env:Payment__Gateway__SlowDelaySeconds = "1.5"
dotnet run --project src/Tadka.Payment.Api
```

Place an order as before and open the newest trace. **Captured live:** `ProcessPayment` took **2043 ms** (the 1.5 s sleep, plus the database writes and the first-call warm-up), and in that run the `consume order-placed` span began **35.8 seconds** after the producer span, because Payment had just been restarted and the consumer group was still handing it the partition (see the note in Section 1). The whole order took 39 seconds to confirm, and not one log line complained. Only the waterfall shows where the time went: 36 s waiting in Kafka, 2 s charging.

Restore Payment (`unset Payment__Gateway__Behavior Payment__Gateway__SlowDelaySeconds` in bash; `$env:Payment__Gateway__Behavior = $null; $env:Payment__Gateway__SlowDelaySeconds = $null` in PowerShell) before the next demo.

---

### The Wound: Simulating Payment Gateway Failure
In Day 8, when a payment gateway failed, the order quietly sat in limbo while operators guessed what happened. With distributed tracing, the exact failure point is highlighted in red.

**Stop the Payment service (Ctrl+C in Terminal 2)** and restart it with the failure simulation flag enabled:

**Bash:**
```bash
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
export Payment__Gateway__Behavior="Failing"
dotnet run --project src/Tadka.Payment.Api
```

**PowerShell:**
```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
$env:Payment__Gateway__Behavior = "Failing"
dotnet run --project src/Tadka.Payment.Api
```

Now, submit a new order through the Gateway:
**Bash:**
```bash
curl -s -X POST http://localhost:8080/api/v1/orders \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "$BODY" | sed -E 's/.*"id":"([^"]+)".*"status":"([^"]+)".*/id: \1  status: \2/'
```

**PowerShell:**
```powershell
$failedOrder = Invoke-RestMethod -Uri http://localhost:8080/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY
"id: " + $failedOrder.id + "  status: " + $failedOrder.status
```

**Captured live:** `id: 432961ed-d771-4129-a439-1d69afed52b0  status: Created`

The order never becomes `Confirmed`. Wait about 40 seconds (the consumer-group handover described in Section 1, plus the saga's own hops) and read it again:

**Bash:**
```bash
curl -s http://localhost:8080/api/v1/orders/<order-id> -H "Authorization: Bearer $TOKEN" | grep -o '"status":"[^"]*"\|"cancellationReason":[^,}]*'
```

**PowerShell:**
```powershell
$o = Invoke-RestMethod -Uri "http://localhost:8080/api/v1/orders/$($failedOrder.id)" -Headers $H
"status: " + $o.status + "  reason: " + $o.cancellationReason
```
**Captured live:** `status: Cancelled  reason: Payment failed: PaymentDeclinedException: Gateway declined the payment (simulated).` Payment reported the decline as a business answer on `payment-results`, and the Day 9 saga cancelled the order. All four orders placed in the capture ended the same way.

#### Inspect the Failed Trace in Jaeger
Refresh Jaeger at **[http://localhost:16686](http://localhost:16686)** and open the newest trace:
- **Total Spans:** 8 spans across 3 services (Gateway, Ordering, Payment). The last one is `consume payment-results`, where Ordering cancels the order.
- **Does it reach Delivery?** **NO.** There are 0 spans for `Tadka.Delivery.Api`, because the order was never confirmed.
- **The Culprit:** The `ProcessPayment` span in `Tadka.Payment.Api` is highlighted with an **error badge**:
  - `error = True`
  - `otel.status_code = ERROR`
  - `otel.status_description = "PaymentDeclinedException: Gateway declined the payment (simulated)."`
  - `payment.status = "failed"`

**What this proved:** Without touching a single log file, the on-call engineer can see in one second that the saga halted at Payment due to an upstream gateway rejection.

#### The Fix
Stop Payment (Ctrl+C), reset the environment variable, and restart:
```bash
unset Payment__Gateway__Behavior
dotnet run --project src/Tadka.Payment.Api
```
```powershell
$env:Payment__Gateway__Behavior = $null
dotnet run --project src/Tadka.Payment.Api
```

---

### 3.5 Inspecting Context Propagation in Kafka UI
To visually verify that Kafka messages carry the W3C `traceparent` across broker topics:
1. Open Kafka UI at **[http://localhost:8090](http://localhost:8090)**.
2. In the left navigation, click **Topics** → click `order-placed`.
3. Switch to the **Messages** tab.
4. Expand the newest message:
   - Notice the **Headers** table contains:
     - Key: `traceparent`
     - Value: `00-f702b761785651a3b5a7057190bb727a-d4e491a662faeb10-01`
   - Notice the trace ID (`f702b761785651a3b5a7057190bb727a`) exactly matches the trace ID in Jaeger!
5. Now check `payment-results` and `order-confirmed`: each carries the same trace ID. The span id (the third field) changes at every hop, because each hop is a new span.

The same check from a terminal, with no UI (reads the newest message of a topic and prints its headers):

**Bash:**
```bash
export MSYS_NO_PATHCONV=1
K="docker exec tadka-kafka /opt/kafka/bin"
END=$($K/kafka-get-offsets.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --topic order-placed | sed -E 's/.*:([0-9]+)$/\1/')
$K/kafka-console-consumer.sh --bootstrap-server localhost:9092 --consumer.config /etc/kafka/docker/client.properties --topic order-placed --partition 0 --offset $((END-1)) --max-messages 1 --timeout-ms 8000 --property print.headers=true --property print.key=true
```

**PowerShell:**
```powershell
$end = [int]((docker exec tadka-kafka /opt/kafka/bin/kafka-get-offsets.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --topic order-placed) -replace '.*:','')
docker exec tadka-kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --consumer.config /etc/kafka/docker/client.properties --topic order-placed --partition 0 --offset ($end-1) --max-messages 1 --timeout-ms 8000 --property print.headers=true --property print.key=true
```
**Captured live:** `traceparent:00-7d14f831108d1c184d29b7399379251a-db6f2c1630845a62-01` on `order-placed`, and `traceparent:00-7d14f831108d1c184d29b7399379251a-5a03c15ae319446c-01` on that order's `payment-results`. Same trace id (`7d14f8...`), different span ids. The four fields are version, trace id, parent span id, flags.

---

### 3.6 Hoisting Spans Across Consumer Exceptions (Fix 6)
In Day 12 and earlier, Kafka consumers declared their span inside a scoped block that did not wrap consumer exceptions. If message processing threw an uncaught exception, the activity was aborted without marking the error state, leaving Jaeger traces marked green or orphaned.

In Day 13 ([Fix 6]), all 9 Kafka consumers hoist `Activity? activity` outside the `try/catch/finally` block:
```csharp
Activity? activity = null;
try
{
    activity = TadkaDiagnostics.ActivitySource.StartActivity(
        $"consume {Topics.OrderPlaced}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));

    await HandleAsync(cr.Message.Value, stoppingToken);
    consumer.Commit(cr);
}
catch (ConsumeException ex)
{
    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
    activity?.AddException(ex);
    logger.LogError(ex, "OrderPlacedConsumer consume error.");
}
catch (Exception ex) when (cr is not null)
{
    // Fix 6: Mark span as Error and record full stack trace before poison routing
    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
    activity?.AddException(ex);
    await HandlePoisonAsync(consumer, cr, ex, stoppingToken);
}
finally
{
    activity?.Dispose();
}
```
**Why this matters:** When a consumer fails (e.g., transient database error or poison message), the Jaeger span is tagged in bright red with `otel.status_code = ERROR` and includes the exact C# exception type and stack trace as span events.

**Try it:** publish a message that is not valid JSON to `order-placed` (Payment is running). Payment retries it three times (ADR-051) and then parks it on `order-placed.dlq`. Each attempt is a red span.

**Bash:**
```bash
export MSYS_NO_PATHCONV=1
echo 'poison-1|{ this is not valid json ' | docker exec -i tadka-kafka /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server localhost:9092 --producer.config /etc/kafka/docker/client.properties --topic order-placed --property parse.key=true --property 'key.separator=|'
```

**PowerShell** (Windows PowerShell 5.1 puts a byte-order mark at the start of text piped into a program, so send the line from inside the container instead):
```powershell
function Send-Kafka($Topic, $Line) {
  $send = 'echo "$MSG" | /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server localhost:9092 --producer.config /etc/kafka/docker/client.properties --topic ' + $Topic + ' --property parse.key=true --property "key.separator=|"'
  docker exec -e "MSG=$($Line -replace '"','\"')" tadka-kafka bash -c ($send -replace '"','\"') 2>&1 | Where-Object { $_ -notmatch 'WARN' }
}
Send-Kafka order-placed 'poison-1|{ this is not valid json '
```

In Jaeger, service `Tadka.Payment.Api`, operation `consume order-placed`, tag `error=true`. **Captured live:** three traces, each with a `consume order-placed` span in error (`otel.status_code = ERROR`, an `exception` event of type `System.Text.Json.JsonException`), 118 to 330 ms each, and `order-placed.dlq` appeared in the topic list.

---

### 3.7 Break propagation on purpose (the trace that starts in the middle)
Publish a perfectly valid `order-placed` message that carries **no** `traceparent` header, as if a producer had forgotten it. Payment is running and healthy.

**Bash:**
```bash
export MSYS_NO_PATHCONV=1
O=$(python -c "import uuid;print(uuid.uuid4())"); M=$(python -c "import uuid;print(uuid.uuid4())")
echo "$O|{\"MessageId\":\"$M\",\"OrderId\":\"$O\",\"Amount\":598.00,\"Currency\":\"INR\",\"Version\":1}" | docker exec -i tadka-kafka /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server localhost:9092 --producer.config /etc/kafka/docker/client.properties --topic order-placed --property parse.key=true --property 'key.separator=|'
```

**PowerShell** (uses `Send-Kafka` from Section 3.6):
```powershell
$O = [guid]::NewGuid(); $M = [guid]::NewGuid()
Send-Kafka order-placed ("$O|{`"MessageId`":`"$M`",`"OrderId`":`"$O`",`"Amount`":598.00,`"Currency`":`"INR`",`"Version`":1}")
```

Search Jaeger for service `Tadka.Payment.Api` and tag `order.id=<that guid>`. **Captured live:** a **separate trace of 4 spans** (`consume order-placed`, `ProcessPayment`, `outbox publish payment-results`, `consume payment-results`) that **starts at Payment**: no Gateway, no Ordering. The payment succeeded (`payment.status=success`), so the business worked; only the trace lost its beginning. This is exactly what a forgotten header looks like at 2 AM.

### 3.8 Stop the Collector (telemetry down, business up)
**Bash / PowerShell** (same command):
```
docker stop tadka-otel-collector
```
Place an order through the gateway as in Section 3. **Captured live:** `HTTP 201 in 0.046 s`, and the order reached `Confirmed` about 4 seconds later. Nothing in the application logs mentions telemetry. Now search Jaeger for that order's `order.id`: **zero traces**. The spans were dropped, silently.

```
docker start tadka-otel-collector
```
Place another order: the full 13-span trace is back. Remember what you saw: a missing trace does not mean a missing order (ADR-040: "Collector down: we lose visibility, not the order path").

---

## 4. Demo 2: RED Metrics & The Cardinality Blow-Up (Prometheus & Grafana)

**What you're proving:** Metrics provide aggregated fleet-level health (Rate, Errors, Duration). However, putting high-cardinality values like `order_id` into Prometheus metric labels causes a catastrophic explosion of time-series, leading to memory exhaustion (OOM).

### Baseline: RED Metrics
Open Grafana at **[http://localhost:3000](http://localhost:3000)** (login: `admin` / `admin`).
Navigate to **Dashboards → Tadka → Tadka — System Overview (RED + business)**.

The dashboard displays:
1. **Request Rate (req/s):** PromQL:
   ```promql
   sum by (service_name) (rate(http_server_request_duration_seconds_count[1m]))
   ```
2. **Error rate (5xx req/s):** PromQL:
   ```promql
   sum by (service_name) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[1m]))
   ```
3. **P95 Latency:** PromQL:
   ```promql
   histogram_quantile(0.95, sum by (le, service_name) (rate(http_server_request_duration_seconds_bucket[5m])))
   ```
4. **Orders placed (rate/min)** and 5. **Payment results by status** (`tadka_orders_placed_total`, `tadka_payment_result_total{status=...}`), and 6. **Metric series count (cardinality watch)** (`prometheus_tsdb_head_series`).

> **Why Payment shows a request rate of 0.** `http_server_request_duration_seconds_count` counts HTTP requests. Payment's work arrives as Kafka messages, so its HTTP rate is 0 even while it charges every order. The business counters (panels 4 and 5) are what show Payment working. **Captured live:** Gateway 0.034 req/s, Ordering 0.039 req/s, Payment 0 req/s over the same ten minutes.

> **Note on Metric Cadence:** The OpenTelemetry .NET SDK exports metrics on a **60-second periodic interval**. Unlike traces (which appear in Jaeger immediately), Prometheus will show metric increments after the current 60s scrape cycle completes.

---

### The Wound: Cardinality Explosion Anti-Pattern
Time-series databases like Prometheus store data in memory indexed by the unique combination of labels. A label with millions of distinct values creates millions of active series in the TSDB Head chunk.

Stop the Ordering Monolith (`Tadka.Api`, Ctrl+C in Terminal 4) and relaunch it with the cardinality explosion demo enabled:

**Bash:**
```bash
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
export OTEL_CARDINALITY_DEMO=true
dotnet run --project src/Tadka.Api
```

**PowerShell:**
```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
$env:OTEL_CARDINALITY_DEMO = "true"
dotnet run --project src/Tadka.Api
```

When `OTEL_CARDINALITY_DEMO=true`, [`OrdersController.cs`](../../src/Tadka.Api/Controllers/OrdersController.cs) runs:
```csharp
// CARDINALITY-BLOWUP DEMO (ADR-042): order_id as a metric label
if (Environment.GetEnvironmentVariable("OTEL_CARDINALITY_DEMO") == "true")
    Tadka.Telemetry.TadkaDiagnostics.OrdersPlacedByIdBAD.Add(1,
        new KeyValuePair<string, object?>("order_id", order.Id.ToString()));
```

Now, place 6 orders in rapid succession. The monolith's signing keys live in memory, so your old token stopped working when you restarted it: log in again first.

**Bash:**
```bash
TOKEN=$(curl -s -X POST http://localhost:8080/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
for i in 1 2 3 4 5 6; do
  echo "Placed order $i : $(curl -s -X POST http://localhost:8080/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)"
done
```

**PowerShell:**
```powershell
# Re-login (monolith keys reset in memory)
$TOKEN = (Invoke-RestMethod -Uri http://localhost:8080/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$H = @{ Authorization = "Bearer $TOKEN" }
for ($i = 1; $i -le 6; $i++) {
    $res = Invoke-RestMethod -Uri http://localhost:8080/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY
    "Placed order $i : $($res.id)"
}
```

Wait 60 seconds for the OTel metric push, then query Prometheus directly at [http://localhost:9090](http://localhost:9090):
```promql
count(tadka_orders_placed_by_id_total)
```
**Captured live:** `6` distinct series!

Inspect the TSDB Head series gauge in Prometheus:
```promql
prometheus_tsdb_head_series
```
In Grafana, the panel **Metric series count (cardinality watch)** steps up with the new series. **Captured live:** `count(tadka_orders_placed_by_id_total)` = **6** (one series per order, each with its own `order_id` label), and `prometheus_tsdb_head_series` went from 2,986 to 3,192. Most of that +206 is the restarted monolith's own metrics: every process instance is its own set of series (`exported_instance`), so restarts add series too. That is a second, quieter source of cardinality, and one more reason to keep the *labels* bounded.

#### The Math (ADR-042)
- At **100,000 orders/day**, this single bad metric creates **100,000 new series every 24 hours**.
- In Prometheus: Each series retains index chunks in RAM, leading to memory bloat and eventual out-of-memory crashes (`OOMKilled`).
- In Datadog or AWS CloudWatch: Custom metrics are billed by active series combinations. 100,000 series can generate surprise monthly bills exceeding thousands of dollars!

#### The Fix
Stop the monolith, clear the flag, and restart:

**Bash:**
```bash
unset OTEL_CARDINALITY_DEMO
dotnet run --project src/Tadka.Api
```

**PowerShell:**
```powershell
$env:OTEL_CARDINALITY_DEMO = $null
dotnet run --project src/Tadka.Api
```

**The Hard Rule (ADR-042):** High-cardinality values (`order_id`, `user_id`, `email`, `session_token`) belong exclusively on **trace span attributes** and **structured logs**. Allowed Prometheus labels are strictly bounded dimensions: `service_name`, `http_route`, `status_code`, and bounded business enums (`status="success|failed"`).

---

## 5. Demo 3: Correlated Structured Logs by `trace_id`

**What you're proving:** Traces give the macro view; structured logs give the micro details. By enriching Serilog with OpenTelemetry's ambient `Activity.Current`, every log line across all services automatically carries the exact same `trace_id` and `span_id`.

Each service writes its logs to its own terminal. To grep one trace across all five, run each service with its output copied to a file, for example:

**Bash:**
```bash
mkdir -p logs && dotnet run --project src/Tadka.Payment.Api 2>&1 | tee logs/payment.log
```
**PowerShell:**
```powershell
New-Item -ItemType Directory -Force logs | Out-Null; dotnet run --project src/Tadka.Payment.Api 2>&1 | Tee-Object -FilePath logs\payment.log
```
Then search all of them for a trace id (copy it from Jaeger):
```bash
grep -h "<trace-id>" logs/*.log
```
```powershell
Select-String -Path logs\*.log -Pattern "<trace-id>" | ForEach-Object { $_.Line }
```
**Captured live:** one trace id found 2 lines in Gateway, 3 in Ordering, and 1 each in Payment, Delivery and Restaurant: all five services, one `trace_id`. Delivery's line read `Order ... assigned to rider Imran`.

Inspect the console log output of `Tadka.Api` (the monolith) after placing an order. Note that every line has both Serilog's own `@tr`/`@sp` and our `trace_id`/`span_id`:
```json
{
  "@t": "2026-10-06T14:05:02.7798871Z",
  "@mt": "Order {OrderId} auto-confirmed after successful payment.",
  "OrderId": "80bc590f-54e4-4e8d-833c-f9cc4b8a8eae",
  "SourceContext": "Tadka.Api.Domain.Orders.Events.Handlers.ConfirmOrderOnPaymentCompleted",
  "service.name": "Tadka.Api",
  "trace_id": "f702b761785651a3b5a7057190bb727a",
  "span_id": "b72451b1bfa5d37c"
}
```

Now copy that `trace_id` (`f702b761785651a3b5a7057190bb727a`) and grep across the other services' consoles:

**Payment Service (`Tadka.Payment.Api`):**
```json
{
  "@t": "2026-10-06T14:05:02.3188811Z",
  "@mt": "💳 Payment COMPLETED for order {OrderId} (ref {Reference}).",
  "OrderId": "80bc590f-54e4-4e8d-833c-f9cc4b8a8eae",
  "Reference": "FAKEPAY-2DC5D5C59FE1",
  "service.name": "Tadka.Payment.Api",
  "trace_id": "f702b761785651a3b5a7057190bb727a",
  "span_id": "d4e491a662faeb10"
}
```

**Delivery Service (`Tadka.Delivery.Api`):**
```json
{
  "@t": "2026-10-06T14:05:03.4279346Z",
  "@mt": "🛵 Order {OrderId} assigned to rider {Agent} ({AgentId}).",
  "OrderId": "80bc590f-54e4-4e8d-833c-f9cc4b8a8eae",
  "Agent": "Imran",
  "service.name": "Tadka.Delivery.Api",
  "trace_id": "f702b761785651a3b5a7057190bb727a",
  "span_id": "b3f7ad5564144e26"
}
```

**Restaurant Service (`Tadka.Restaurant.Api`):**
```json
{
  "@t": "2026-10-06T14:05:03.3875079Z",
  "@mt": "Restaurant {Status} order {OrderId} (restaurantId={RestaurantId}).",
  "Status": "Accepted",
  "OrderId": "80bc590f-54e4-4e8d-833c-f9cc4b8a8eae",
  "service.name": "Tadka.Restaurant.Api",
  "trace_id": "f702b761785651a3b5a7057190bb727a",
  "span_id": "6fec31698b485cf2"
}
```

**What this proved:** Without requiring a heavyweight centralized log indexing cluster like ELK/Loki in the local demo environment, any developer or automated agent can correlate the entire cross-service lifecycle of a transaction with a simple filter on `trace_id`.

---

## 6. Demo 4: Differentiated SLOs & Actionable Alerting

**What you're proving:** A uniform "four nines (99.99%)" SLO across all services is architectural theatre. SLOs must reflect underlying dependencies (e.g., Payment relies on third-party Razorpay). Furthermore, alerts must trigger on **business failure signals**, not transport HTTP errors (in Tadka, a declined payment returns `200 OK` with an internal failure state).

### Differentiated SLO Targets for Tadka
| Service | Availability SLO | Latency Target | Rationale |
|---|---|---|---|
| **Ordering** | 99.9% | P95 < 200 ms | Fully under internal control (Postgres connection pool + local read model). |
| **Payment** | **99.5%** | P95 < 500 ms | Rides third-party payment gateways (Razorpay/Stripe SLA is ~99.9%). Demanding 99.99% is unrealistic. |
| **Restaurant**| 99.9% | P95 < 100 ms | Read-heavy and heavily cached. |
| **Delivery**  | 99.9% | P95 < 150 ms | Fast in-memory Redis geospatial lookups. |

---

### Tripping the Live Alert
Grafana includes a pre-provisioned alert rule: **"Payment failure rate above SLO (P1)"**.  
It triggers when the 5-minute increase in failed payments exceeds 3:
```promql
sum(increase(tadka_payment_result_total{status="failed"}[5m])) > 3
```

0. **Why the counters start at zero.** `increase()` needs two samples to see a change. A counter that is first created by its first real event is exported for the first time with a value already above zero, so that first burst reads as 0 and the alert would never fire. Tadka therefore creates both `status` series at 0 when the process starts ([`TadkaDiagnostics.PrimePaymentCounters`](../../src/Tadka.Telemetry/TadkaDiagnostics.cs), registered in [`Tadka.Payment.Api/Program.cs`](../../src/Tadka.Payment.Api/Program.cs)). You can see it: right after Payment starts, `tadka_payment_result_total{status="failed"}` exists with value `0`.
1. Restart Payment with failure enabled:
   ```powershell
   $env:Payment__Gateway__Behavior = "Failing"
   dotnet run --project src/Tadka.Payment.Api
   ```
   ```bash
   export Payment__Gateway__Behavior="Failing"
   dotnet run --project src/Tadka.Payment.Api
   ```
   Wait for Payment to start, then wait about a minute so the zero-valued series has been exported once.
2. Place a burst of 4 orders through Gateway:
   ```bash
   for i in 1 2 3 4; do
     curl -s -o /dev/null -X POST http://localhost:8080/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY"
   done
   ```
   ```powershell
   for ($i = 1; $i -le 4; $i++) {
       Invoke-RestMethod -Uri http://localhost:8080/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY
   }
   ```
3. Wait 60s for the metric cycle to complete, then inspect Grafana Alerting at **[http://localhost:3000/alerting/list](http://localhost:3000/alerting/list)**:
   - State transitions from **Normal** → **Pending** (during the 30-second dwell window `for: 30s`).
   - After 30 seconds, state transitions to **Firing** (colored red).

   Or read the state from a terminal (Grafana allows anonymous access in this demo):
   ```bash
   curl -s http://localhost:3000/api/prometheus/grafana/api/v1/rules | python -c "import sys,json;[print(r['name'],'->',r['state']) for g in json.load(sys.stdin)['data']['groups'] for r in g['rules']]"
   ```
   ```powershell
   (Invoke-RestMethod http://localhost:3000/api/prometheus/grafana/api/v1/rules).data.groups.rules | ForEach-Object { $_.name + " -> " + $_.state }
   ```
   **Captured live:** four failing orders at 17:39:27; `Pending` at 17:40:24 (57 s later: the 60 s metric export plus the next 10 s evaluation); `Firing` at 17:40:57 (33 s after that: the `for: 30s` dwell); back to `Normal` by 17:45:24, when the failures fell out of the 5 minute window. The value that tripped it: `sum(increase(tadka_payment_result_total{status="failed"}[5m]))` = **4.007**, against a threshold of 3.
4. Restore Payment to normal:
   ```bash
   unset Payment__Gateway__Behavior
   dotnet run --project src/Tadka.Payment.Api
   ```
   ```powershell
   $env:Payment__Gateway__Behavior = $null
   dotnet run --project src/Tadka.Payment.Api
   ```
   As the 5-minute rolling window clears, the alert automatically recovers to **Normal**.

> **Not 1 failure, 4.** The rule fires above 3 failures in 5 minutes ([`alerts.yaml`](../../docker/observability/grafana/provisioning/alerting/alerts.yaml)). One declined payment is normal life (a customer's card, a bank hiccup) and should not wake anyone. A burst is a pattern.

---

## 7. Excluded Endpoints ("When NOT to Trace")

Not every HTTP request should generate a trace span. High-frequency infrastructure requests create immense noise and skew latency distributions.

In [`src/Tadka.Telemetry/TelemetryRegistration.cs`](../../src/Tadka.Telemetry/TelemetryRegistration.cs), ASP.NET Core instrumentation is configured with a filter:
```csharp
.AddAspNetCoreInstrumentation(o =>
    o.Filter = http => !IsNoise(http.Request.Path.Value))   // skip /health + /metrics + / noise

internal static bool IsNoise(string? path) =>
    path is not null &&
    (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
     path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase) ||
     path == "/");
```

Why this matters:
- Kubernetes readiness/liveness probes query `/health` every 2–5 seconds. If traced, 95% of Jaeger spans would just be empty `/health` calls.
- A Prometheus scrape hits a `/metrics` endpoint every few seconds (here it scrapes the Collector's `:8889` every 5s). Tracing the scraper creates circular telemetry overhead.
- `/` is only ever a browser, a probe or a load balancer poking the root; it carries no business meaning either.

---

## 8. Run the tests

Run the complete test suite across all services:
```bash
dotnet test Tadka.slnx
```
```powershell
dotnet test Tadka.slnx
```

**Expected output:**
```
Passed!  - Failed: 0, Passed: 191, Skipped: 0, Total: 191
```
When `OTEL_EXPORTER_OTLP_ENDPOINT` is unset in CI/unit tests, `Tadka.Telemetry` disables remote network export, ensuring tests run offline, fast, and completely deterministic.

### 8.1 Unit Testing Trace Context Round-Trips
[`TraceContextPropagationTests.cs`](../../tests/Tadka.Api.Tests/Telemetry/TraceContextPropagationTests.cs) provides deterministic, zero-infrastructure unit tests for the core W3C contract:
```csharp
[Fact]
public void TraceParent_round_trips_into_a_remote_context_with_matching_ids()
{
    using var source = new ActivitySource("test-source");
    using var listener = new ActivityListener { ShouldListenTo = _ => true, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded };
    ActivitySource.AddActivityListener(listener);

    using var activity = source.StartActivity("enqueue", ActivityKind.Internal)!;
    var captured = TadkaTrace.CurrentTraceParent();   // written to outbox row

    var parsed = TadkaTrace.ParseContext(captured);   // consumer reconstructs parent

    Assert.NotNull(captured);
    Assert.Equal(activity.TraceId, parsed.TraceId);
    Assert.Equal(activity.SpanId, parsed.SpanId);
    Assert.True(parsed.IsRemote);                     // marked as remote parent from another process
}
```
A companion theory tests that `null`, empty, or invalid `traceparent` strings safely degrade to `default` (a fresh root span) without throwing exceptions, guaranteeing consumer resiliency even under corrupt or missing headers.

[`CounterPrimingAndNoiseTests.cs`](../../tests/Tadka.Api.Tests/Telemetry/CounterPrimingAndNoiseTests.cs) adds two more guarantees: after `PrimePaymentCounters()` both `status=success` and `status=failed` exist at 0 (observed with an in-process `MeterListener`, no Collector), and `/health`, `/metrics` and `/` are filtered out of tracing while `/api/v1/orders` is not.

The monolith's test project is 103 tests, Payment 31, Delivery 23, Restaurant 19, Gateway 15.

---

## 9. Wiring Reference & Cross-Stack Architecture

How the OpenTelemetry patterns implemented in .NET map to other major enterprise stacks:

| Concept | .NET (Tadka) | Java / Spring Boot | Node.js (Nest/Express) | Go |
|---|---|---|---|---|
| **Core SDK** | `OpenTelemetry.Extensions.Hosting` | OpenTelemetry Java Agent (zero-code) | `@opentelemetry/sdk-node` | `go.opentelemetry.io/otel` |
| **Custom Spans** | `ActivitySource.StartActivity("name")` | `@WithSpan` or `Tracer.spanBuilder()` | `tracer.startActiveSpan()` | `tracer.Start(ctx, "name")` |
| **Custom Metrics**| `Meter.CreateCounter<long>()` | Micrometer `Counter.builder()` | `meter.createCounter()` | `meter.Int64Counter()` |
| **Structured Logs**| Serilog JSON + `ActivityEnricher` | Logback JSON + MDC `traceId` | Pino + `pino-opentelemetry-transport` | Zap / Slog with `trace_id` handler |
| **HTTP Propagation**| Automatic via `HttpClient` | Automatic via `WebClient` / RestClient | `@opentelemetry/instrumentation-http` | `otelhttp.NewTransport()` |
| **Kafka Propagation**| Custom byte header injection + Outbox column | Spring-Kafka `ObservationRegistry` + Outbox JPA column | `kafkajs` custom header inject/extract | Sarama / Kafka-go manual header inject |
| **Trace Backend** | Jaeger (`:16686`) | Jaeger / Tempo | Jaeger / Tempo | Jaeger / Tempo |
| **Metrics Backend**| Prometheus (`:9090`) | Prometheus / Micrometer | Prometheus | Prometheus |

---

## 10. Demo vs Production: Named Gaps

| Dimension | Day 13 Demo Stack | Production Standard | Path to Production |
|---|---|---|---|
| **Trace Storage** | Jaeger all-in-one (in-memory) | **Grafana Tempo** or Jaeger backed by Cassandra/Elasticsearch | Reconfigure OTel Collector `exporters:` to point to Tempo object storage. |
| **Trace Sampling** | 100% head-based | **10% head-based** + Collector tail-based sampling | Configure `tail_sampling` processor in `otel-collector-config.yml` to preserve 100% of errors/slow traces while sampling successful ones. |
| **Log Aggregation**| Structured console JSON | **Grafana Loki** or OpenSearch | Deploy Grafana Loki and configure Promtail/FluentBit to ship container stdout. |
| **Alert Routing** | Grafana local alert state | PagerDuty / OpsGenie / Slack webhooks | Configure Grafana Contact Points with API tokens. |
| **Host Metrics** | Application OTel metrics only | Node Exporter + cAdvisor | Add `node-exporter` and `cadvisor` to `docker-compose.yml`. |

> **What the Collector does today:** one processor, `batch` ([`otel-collector-config.yaml`](../../docker/observability/otel-collector-config.yaml)). Production adds `memory_limiter` (so a telemetry flood cannot OOM the Collector), `tail_sampling`, and attribute filtering or redaction.

> **Tail Sampling Note:** It is a common misconception that an application SDK sampler can "sample 10% but always keep errors". A head-based sampler makes the sampling decision at the very beginning of the trace, before it is known whether an error will occur. True error preservation under sampling requires **tail sampling at the Collector**, which buffers spans until the trace completes.

---

## 11. Reset

To shut down observability while keeping database volumes intact:
```bash
docker compose --profile observability down
```
```powershell
docker compose --profile observability down
```

To wipe everything completely (databases, volumes, Prometheus TSDB, and Kafka offsets):
```bash
docker compose --profile observability down -v
```
```powershell
docker compose --profile observability down -v
```

To run apps in normal dev mode without telemetry:
```bash
unset OTEL_EXPORTER_OTLP_ENDPOINT
```
```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = $null
```

---

## 12. Ports

| Service / Container | Port | Protocol / Purpose |
|---|---|---|
| `tadka-otel-collector` | `4317` | OTLP gRPC endpoint |
| `tadka-otel-collector` | `4318` | OTLP HTTP endpoint |
| `tadka-otel-collector` | `8889` | Prometheus scrape endpoint |
| `tadka-jaeger` | `16686`| Jaeger UI |
| `tadka-prometheus` | `9090` | Prometheus UI |
| `tadka-grafana` | `3000` | Grafana UI (admin / admin) |
| `tadka-kafka-ui` | `8090` | Kafka UI |
| `Tadka.Gateway` | `8080` | Reverse Proxy entry point |
| `Tadka.Api` (Monolith) | `5224` | Ordering / Auth |
| `Tadka.Payment.Api` | `5240` | Payment Service |
| `Tadka.Delivery.Api` | `5250` | Delivery Service |
| `Tadka.Restaurant.Api`| `5260` | Restaurant Service |
| `tadka-pgbouncer` | `6432` | Postgres Connection Pooler |
| `tadka-postgres` | `5432` | Monolith primary DB |
| `tadka-postgres-replica` | `5433` | Monolith read replica |
| `tadka-payment-db` | `5434` | Payment DB |
| `tadka-delivery-db` | `5435` | Delivery DB |
| `tadka-restaurant-db` | `5436` | Restaurant DB |
| `tadka-redis` | `6379` | Delivery geo-cache |
| `tadka-kafka` | `9092` | Kafka broker (SASL auth) |

---

## 13. Done When Checklist

- [ ] All 13 Docker containers running healthy (`docker compose --profile observability ps`).
- [ ] Kafka topics created with SASL credentials (`--command-config /etc/kafka/docker/client.properties`).
- [ ] Five services running with `$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"`.
- [ ] Placed an order via Gateway (:8080) and verified the **13-span trace across 5 services** in Jaeger UI (:16686).
- [ ] Simulated payment gateway failure (`Payment__Gateway__Behavior=Failing`) and observed the failed trace **ending at Payment** (8 spans) with `error = True`, and the order ending `Cancelled`.
- [ ] Viewed RED metrics in Grafana (:3000) on dashboard "Tadka — System Overview (RED + business)".
- [ ] Demonstrated cardinality explosion using `OTEL_CARDINALITY_DEMO=true` and verified its resolution.
- [ ] Extracted a `trace_id` from Jaeger and grepped it across stdout of all 4 backend services.
- [ ] Tripped the P1 SLO alert rule in Grafana Alerting under payment failure (4 failed orders; Normal, Pending, Firing, then Normal again).
- [ ] Sent an `order-placed` message with no `traceparent` and saw the Payment trace start in the middle (Section 3.7).
- [ ] Stopped the Collector: order still `201` and `Confirmed`, no trace recorded (Section 3.8).
- [ ] All unit and integration tests passing (`dotnet test Tadka.slnx` → 191 passed).

---

## 14. Troubleshooting

### 1. `docker exec tadka-kafka` hangs or prints nothing
Kafka is configured with SASL/SCRAM authentication. Any command-line tool executed inside the container must provide authentication credentials:
```powershell
docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --list
```

### 2. Consumer logs `Subscribed topic not available`
Harmless warning emitted when a consumer subscribes before the topic has been created. Run the topic pre-creation loop in Section 1.

### 3. Prometheus shows no data for new metrics
The OpenTelemetry .NET SDK exports metrics every **60 seconds**. Wait at least 60 seconds after placing requests before querying Prometheus.

### 4. PowerShell `Invoke-WebRequest` throws on 4xx/5xx responses
Use the `Get-StatusCode` helper defined in Section 1, or use `Invoke-RestMethod` within a `try/catch` block.

### 5. Services fail to build with `Cannot open Tadka.Telemetry.dll for writing`
Occurs when multiple `dotnet run` commands attempt to build the shared dependency simultaneously. Run `dotnet build Tadka.slnx` once before starting services, and add `--no-build` to `dotnet run`.

### 6. The first order after a restart sits in `Created` for ~40 seconds
Consumer-group handover after a killed process (Section 1). Wait, or stop services with Ctrl+C (a clean shutdown leaves the group at once).

### 7. Kafka restarts in a loop after Docker was restarted
`Log directory /tmp/kafka-logs is already formatted`: an old copy of `docker/kafka-scram-entrypoint.sh`. Pull the branch, then `docker compose up -d --force-recreate kafka` and re-run the topic loop.

### 8. PowerShell: text piped to `docker exec -i` arrives with a stray first character
Windows PowerShell 5.1 prepends a byte-order mark to piped text. Use the `Send-Kafka` helper (Section 3.6), which sends the line from inside the container.
