# Day 11: Runbook: Extract Delivery (3rd service) + the API Gateway

**Branch:** `day-11`  ·  **What changed since Day 10:** [`docs/changelog.md`](../changelog.md). **What's new:** the 3rd service, **`Tadka.Delivery.Api`** (own DB `delivery-db` 5435; **Redis-geo** live location; Kafka-driven assignment, ADR-033/034), and a **YARP API gateway** (`Tadka.Gateway`, :8080, single entry + edge rate-limit, ADR-035). The order→payment→**delivery** flow is now a **3-participant Saga**, and it just earned a real 4th failure mode: a restaurant can **reject** an already-paid order, which needs a **compensating refund** (ADR-045), Day 9's choreography pattern reused. **3 services + gateway** (monolith :5224, payment :5240, delivery :5250, YARP :8080). Restaurant is still in the monolith; the 4th service is Day 12. Also: **PgBouncer** (`:6432`, ADR-015 landed), the Day-5 pool-exhaustion promise, paid off now that 2+ app instances actually exist.

> New here? Read [`README.md`](README.md). Demo password `Password123!`. Deep Saga treatment: `cohort-prep/day-11/saga-deep-dive.md` (instructor pack, not in this repo).

Every command below is given twice, bash first and PowerShell second. They are not the same commands with `curl` swapped for `curl.exe`: bash's `VAR=$(...)`, `sed -E`, and inline `VAR=value command` syntax do not run in plain PowerShell at all. Windows PowerShell 5.1 also cannot read an HTTP status code off a 401/403 response without the call throwing, so one small helper is defined once in section 1 and reused. Every PowerShell block here was run live against this branch before being written down.

---

## 1. Run it (infra + 3 services + gateway)

### Demo Day Fresh Reset (Clean Slate)
If you want to remove all existing containers, network overlays, and persistent database volumes (and ensure any leftover Keycloak or test containers are destroyed) to start completely fresh:

**Bash:**
```bash
docker compose --profile auth-prod down -v --remove-orphans && docker compose up -d
until docker inspect tadka-delivery-db --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
until docker inspect tadka-kafka --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
docker compose ps
```

**PowerShell:**
```powershell
docker compose --profile auth-prod down -v --remove-orphans; docker compose up -d
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-delivery-db --format "{{.State.Health.Status}}") -eq "healthy")
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-kafka --format "{{.State.Health.Status}}") -eq "healthy")
docker compose ps
```

*(This stops all running services, wipes volume directories `pgdata`, `pgdata_replica`, `pgdata_payment`, `pgdata_delivery`, and brings up the 8 containers fresh from scratch).*

---

### Standard Launch
If starting existing containers without wiping data:

```bash
git checkout day-11
docker compose up -d            # + delivery-db (5435), pgbouncer (6432)
until docker inspect tadka-delivery-db --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
until docker inspect tadka-kafka --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
docker compose ps
```
```powershell
git checkout day-11
docker compose up -d
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-delivery-db --format "{{.State.Health.Status}}") -eq "healthy")
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-kafka --format "{{.State.Health.Status}}") -eq "healthy")
docker compose ps
```

**Kafka requires a login on this branch.** The broker only accepts clients that authenticate with SASL/SCRAM-SHA-256. Payment, Delivery and the monolith already carry the demo credentials (`appsettings.Development.json`), and so does Kafka UI (`docker-compose.yml`). The Kafka command-line tools you run through `docker exec` are clients too, so each one takes `--command-config /etc/kafka/docker/client.properties` (`kafka-topics.sh`, `kafka-consumer-groups.sh`), or `--producer.config` / `--consumer.config` for the console producer and consumer. Without credentials a Kafka command hangs and prints nothing. Details are in the Day 9 runbook and ADR-027's security addendum.

**Pre-create the six Kafka topics this branch uses** (once per fresh broker, right after the containers are healthy). On a broker with no topics yet, each service subscribes the moment it starts, and a consumer that starts before anyone has published to its topic logs `Confluent.Kafka.ConsumeException: Subscribed topic not available` once a second. It is harmless (the consumer keeps retrying and picks the topic up once it exists), but it looks alarming on a first run. Auto-create is on, so skipping this step breaks nothing. The `*.dlq` topics are not in the list on purpose: nothing subscribes to them, and they appear on their own the first time a poison message is parked.
```bash
for t in order-placed payment-results order-confirmed delivery-assigned refund-requested payment-refunded; do
  docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
done
```
```powershell
foreach ($t in "order-placed","payment-results","order-confirmed","delivery-assigned","refund-requested","payment-refunded") {
  docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
}
```

Four processes, four terminals (identical command in either shell). The gateway goes last, since it only has something to route to once the other three are listening:

```
dotnet run --project src/Tadka.Payment.Api     # :5240
dotnet run --project src/Tadka.Delivery.Api    # :5250 (consumes order-confirmed; seeds riders)
dotnet run --project src/Tadka.Api             # :5224
dotnet run --project src/Tadka.Gateway         # :8080 (the single entry point)
```

### PowerShell helper: reading a status code without the call throwing
Several demos below check a status code like `403` or `401`. In bash, `curl -s -o /dev/null -w "%{http_code}"` does this without complaint. In PowerShell, `Invoke-WebRequest` and `Invoke-RestMethod` throw on any non-2xx response, so the code has to be read off the exception. Define this once, in the same terminal you'll run every PowerShell block below in:
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

> **Timing you will hit on a cold start:** the first order after a service (re)starts can take noticeably longer than later ones, because the JIT is cold and the Kafka consumers have to join their group before the first message is processed. Measured on this branch: a warm order confirms in about 1 second; the first order after a monolith restart took over 30 seconds to fully settle in the refund demo. So every wait below is a poll with a timeout, not a fixed `sleep`.

---

## 2. The 3-service Saga: order → payment → delivery (ADR-029/033)

**What you're proving:** the Saga that Day 9 built for 2 participants (order + payment) grows a 3rd leg without changing its shape. Delivery reacts to an event it never asked for, the same way Payment did on Day 9.

```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
BODY='{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}'
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
for i in $(seq 1 30); do S=$(curl -s http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/\1/'); [ "$S" = "Confirmed" ] && break; sleep 1; done; echo "order: $S"
curl -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"   # {"agentName":"<first available>","status":"Assigned",...}
```
```powershell
$TOKEN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$H = @{ Authorization = "Bearer $TOKEN" }
$BODY = '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}'
$ORDER = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY).id
$sw = [Diagnostics.Stopwatch]::StartNew()
do { Start-Sleep -Seconds 1; $status = (Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders/$ORDER" -Headers $H).status } until ($status -eq "Confirmed" -or $sw.Elapsed.TotalSeconds -gt 30)
"order: $status"
Invoke-RestMethod -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/track" -Headers $H
```
Assignment is `FirstOrDefault(Available)`, not nearest and not `GEOSEARCH`: whichever rider is `Available` wins. Reset the agents if a prior run left them `OnDelivery` (`docker compose down -v` does it).

**Captured live:** order `Confirmed` in about 1 second on a warm stack, rider assigned (**Lakshmi** in one run, **Suresh** in another, whoever the query returns first, never a fixed name). Flow: order Created → paid → **Confirmed** → monolith publishes `order-confirmed` (Outbox→Kafka) → **Delivery consumes it → assigns a rider → publishes `delivery-assigned`**. See `saga-deep-dive.md` for choreography vs orchestration at this scale, and section 3.5 for what happens when the restaurant *rejects*.

### How this is actually implemented
- **The trigger.** The monolith writes `order-confirmed` to its Outbox in the same transaction as the confirmation, carrying the delivery latitude and longitude, so Delivery never calls back for them.
- **The consumer.** [`Messaging/OrderConfirmedConsumer.cs`](../../src/Tadka.Delivery.Api/Messaging/OrderConfirmedConsumer.cs) reads the topic and calls `DeliveryService.AssignAsync`.
- **The assignment.** [`DeliveryService.cs`](../../src/Tadka.Delivery.Api/DeliveryService.cs) does an idempotency check on the order id first, then takes the first `AgentStatus.Available` rider, flips it to `OnDelivery`, and saves. A unique index on the order id makes a redelivered event, or a race between two consumers, return the existing assignment instead of assigning twice:
```csharp
var agent = await db.Agents.FirstOrDefaultAsync(a => a.Status == AgentStatus.Available, ct);
...
catch (DbUpdateException) // lost the unique-index race → idempotent
{
    var winner = await db.Assignments.AsNoTracking().FirstAsync(a => a.OrderId == orderId, ct);
    ...
}
```

### Option space: how to coordinate three services
| Approach | What it is | Best at | Cost |
|---|---|---|---|
| **Choreography** (what Tadka does) | Each service reacts to events and emits its own | 2-3 linear participants, no central component | Flow lives scattered across handlers; hard to read at 4-5 participants |
| **Orchestration** | A coordinator sends commands, reacts to replies, drives compensation | 4+ participants, timeouts, branching, long-running flows | A new component that can itself become a single point of failure |
| **2PC / XA** | Distributed lock, prepare then commit | Nothing here | Anti-pattern for this: blocks under partition |

**Rule of thumb:** if you cannot read one file and say what happens next, bring in an orchestrator. Tools by stack: MassTransit state machine or NServiceBus sagas (.NET), Axon or Camunda (Java), Temporal (Go, Java, .NET, TypeScript), AWS Step Functions, Azure Durable Functions.

---

## 3. Redis-geo live location (ADR-034)

**What you're proving:** live location is a different workload from the assignment history you'd keep in Postgres. It is a latest-wins overwrite, not a growing table, so it lives in Redis, not in the Delivery service's relational DB.

Only **the rider on this order** (or Admin) may post its location, so log in as that rider first. Each seeded rider has a login: the rider's name in lower case plus `.rider@tadka.test` (`suresh.rider@`, `lakshmi.rider@`, `imran.rider@`), same demo password. Priya's own token is refused on this endpoint: she can watch the rider, not move them.

```bash
TRACK=$(curl -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN")
RIDER_EMAIL="$(echo "$TRACK" | sed -E 's/.*"agentName":"([^"]+)".*/\1/' | tr 'A-Z' 'a-z').rider@tadka.test"
RIDER=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d "{\"email\":\"$RIDER_EMAIL\",\"password\":\"Password123!\"}" | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "Priya moves the rider: %{http_code}\n" -X PUT http://localhost:5250/api/v1/deliveries/$ORDER/location -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"latitude":12.0,"longitude":75.0}'   # 403
curl -s -o /dev/null -w "rider posts location: %{http_code}\n" -X PUT http://localhost:5250/api/v1/deliveries/$ORDER/location -H "Authorization: Bearer $RIDER" -H "Content-Type: application/json" -d '{"latitude":12.95,"longitude":77.64}'   # 204
TRACK=$(curl -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"); echo "$TRACK"   # location: {latitude:12.95, longitude:77.64}
AGENT=$(echo "$TRACK" | sed -E 's/.*"agentId":"([^"]+)".*/\1/')
docker exec tadka-redis redis-cli GEOPOS delivery:agents $AGENT   # raw geo; overwrite-latest, sub-ms
```
```powershell
$track = Invoke-RestMethod -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/track" -Headers $H
$riderEmail = "$($track.agentName.ToLower()).rider@tadka.test"
$RIDER = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body (@{ email = $riderEmail; password = "Password123!" } | ConvertTo-Json)).accessToken
$HR = @{ Authorization = "Bearer $RIDER" }
"Priya moves the rider: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/location" -Method Put -Headers $H -Body '{"latitude":12.0,"longitude":75.0}')   # 403
"rider posts location: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/location" -Method Put -Headers $HR -Body '{"latitude":12.95,"longitude":77.64}')   # 204
$track = Invoke-RestMethod -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/track" -Headers $H
$track
docker exec tadka-redis redis-cli GEOPOS delivery:agents $track.agentId
```
**Captured live (before the rider-login change):** `track` returned the exact coordinates just PUT (`12.95…, 77.64…`); `GEOPOS` on the same agent id returned the same pair straight out of Redis. Redis stores geo coordinates in a 52-bit geohash, so you get `12.950000663…` back, not exactly `12.95`; that is normal, and the error is well under a metre. The 403/204 pair above is pinned by `DeliveryOwnershipTests` and `RealJwtAuthorizationTests` (real RS256 tokens through the real JWT handler); the rider-login commands themselves have not yet been re-run against a live stack.

### How this is actually implemented
[`LocationStore.cs`](../../src/Tadka.Delivery.Api/LocationStore.cs) hides Redis behind `ILocationStore`, so tests run with a no-op `NullLocationStore` and need no Redis. The real one is two calls:
```csharp
public Task SetAsync(Guid agentId, double latitude, double longitude)
    => _db.GeoAddAsync("delivery:agents", longitude, latitude, agentId.ToString());
...
var pos = await _db.GeoPositionAsync("delivery:agents", agentId.ToString());
```
`GEOADD` on an existing member overwrites it, which is the whole point: one key, one entry per rider, no history growing. The durable record (which rider got which order) stays in the Delivery Postgres.

### Option space: where does live location live?
| Option | Good | Bad | Use when |
|---|---|---|---|
| Postgres, same table as orders | Simplest | Every 2-3 second ping is a write to the transactional DB | ~10 riders |
| Postgres, separate service/table | Isolates the blast radius | A relational engine still absorbs a high-frequency overwrite | Moderate scale |
| **Redis-geo** (what Tadka does) | Overwrite in place, sub-millisecond reads, `GEOSEARCH` available | Ephemeral: a Redis restart blanks live location until the next ping | Latest-position workloads |
| Time-series store (InfluxDB, Timestream) | Full path history and analytics | Another system to run | You need route history, not just the last point |

Cross-stack: the `GEOADD` / `GEOPOS` / `GEOSEARCH` command surface is identical in StackExchange.Redis (.NET), Lettuce or Jedis (Java), ioredis (Node) and go-redis (Go). The decision is language-neutral: any stack pays the same connection-exhaustion tax for routing a high-frequency overwrite through a relational engine.

---

## 3.5. Restaurant rejects an already-paid order → compensating refund (ADR-045)

**What you're proving:** Day 9's saga compensation only ever ran on a *declined* payment, where no money had moved yet, so cancelling the order was the whole fix. Day 11 adds a harder case: the restaurant can reject an order **after** the customer has been charged. Cancelling alone would leave a `Completed` payment with nobody's money coming back. This section proves the fix, then proves what happens if you skip it.

Restart the monolith (Ctrl+C its terminal first) with the reject lever on, so every new order is rejected the moment it is paid:

```bash
Restaurant__AcceptMode=Reject Restaurant__RefundOnReject=true dotnet run --project src/Tadka.Api
```
```powershell
$env:Restaurant__AcceptMode = "Reject"; $env:Restaurant__RefundOnReject = "true"
dotnet run --project src/Tadka.Api
# after you stop it (Ctrl+C), clear the levers so later sections behave normally:
Remove-Item Env:Restaurant__AcceptMode, Env:Restaurant__RefundOnReject
```
**Log in again after every monolith restart.** The monolith keeps its JWT signing keys in memory only (ADR-067), so a restart creates new keys and every token you issued before it now returns `401`. That is why each block below starts with a fresh login. `$BODY` is the order body from section 2 (if this is a new terminal, set it again first). The orders in this section go into `$REJECTED` on purpose, so `$ORDER` from section 2 (the one with a rider) is still intact for sections 4.5 and 4.6.

Then, in another terminal:
```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
REJECTED=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
for i in $(seq 1 45); do S=$(curl -s http://localhost:5224/api/v1/orders/$REJECTED -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/\1/'); P=$(curl -s http://localhost:5240/payments/$REJECTED -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/\1/'); [ "$S" = "Cancelled" ] && [ "$P" = "Refunded" ] && break; sleep 2; done
echo "order: $S  payment: $P"
curl -s http://localhost:5240/payments/$REJECTED -H "Authorization: Bearer $TOKEN"   # {"status":"Refunded","gatewayReference":"FAKEREF-..."}
```
```powershell
$TOKEN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$H = @{ Authorization = "Bearer $TOKEN" }
$REJECTED = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY).id
$sw = [Diagnostics.Stopwatch]::StartNew()
do { Start-Sleep -Seconds 2; $s = (Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders/$REJECTED" -Headers $H).status; $p = (Invoke-RestMethod -Uri "http://localhost:5240/payments/$REJECTED" -Headers $H).status } until (($s -eq "Cancelled" -and $p -eq "Refunded") -or $sw.Elapsed.TotalSeconds -gt 90)
"order: $s  payment: $p"
Invoke-RestMethod -Uri "http://localhost:5240/payments/$REJECTED" -Headers $H
```
**Captured live:** order `Cancelled`, payment **`Refunded`** with its own gateway reference (`FAKEREF-…`, different from the original `FAKEPAY-…` charge, proving a real second gateway call happened and not a status flip). On a monolith restarted seconds earlier the order stayed `Created`/`Completed` for over 12 seconds before the refund chain ran, then finished about 10 seconds later. Give the first order after a restart up to a minute; warm ones are much faster.

Now the break: turn the compensation off and watch the money get stuck. Restart the monolith again:

```bash
Restaurant__AcceptMode=Reject Restaurant__RefundOnReject=false dotnet run --project src/Tadka.Api
```
```powershell
$env:Restaurant__AcceptMode = "Reject"; $env:Restaurant__RefundOnReject = "false"
dotnet run --project src/Tadka.Api
# after you stop it: Remove-Item Env:Restaurant__AcceptMode, Env:Restaurant__RefundOnReject
```
```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
REJECTED=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
for i in $(seq 1 45); do S=$(curl -s http://localhost:5224/api/v1/orders/$REJECTED -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/\1/'); [ "$S" = "Cancelled" ] && break; sleep 2; done
sleep 5; echo "order: $S"
curl -s http://localhost:5240/payments/$REJECTED -H "Authorization: Bearer $TOKEN"   # {"status":"Completed", ...}: STILL Completed
```
```powershell
$TOKEN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$H = @{ Authorization = "Bearer $TOKEN" }
$REJECTED = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY).id
$sw = [Diagnostics.Stopwatch]::StartNew()
do { Start-Sleep -Seconds 2; $s = (Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders/$REJECTED" -Headers $H).status } until ($s -eq "Cancelled" -or $sw.Elapsed.TotalSeconds -gt 90)
Start-Sleep -Seconds 5
"order: $s"
Invoke-RestMethod -Uri "http://localhost:5240/payments/$REJECTED" -Headers $H   # status: Completed, STILL
```
**Captured live:** order `Cancelled` after 36 seconds on a freshly restarted monolith, payment stayed **`Completed`**: money genuinely stuck, until someone flips the lever back and reconciles by hand. The monolith's log says so out loud: `Order {id} cancelled after restaurant rejection, but Restaurant:RefundOnReject is OFF, the completed payment is NOT refunded.` This is a real gap shown on purpose, not hidden behind a passing test.

Restart the monolith with no env overrides (`AcceptMode` defaults to `Auto`) before continuing. Every section after this one assumes orders confirm normally. That restart invalidates your tokens again, so log in once more and put the result back in `$TOKEN` / `$H` (and `$RAHUL` / `$RIDER` if a later section uses them):
```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
```
```powershell
$TOKEN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$H = @{ Authorization = "Bearer $TOKEN" }
```

### How this is actually implemented
[`RefundSagaOrchestrator.cs`](../../src/Tadka.Api/Infrastructure/Messaging/RefundSagaOrchestrator.cs) is the one named place that sequences the compensation: cancel the order, then (if `RefundOnReject`) write a `refund-requested` row to the Outbox in the same transaction as the cancellation:
```csharp
var cancelResult = order.Cancel("Restaurant rejected the order.");
if (refundOnReject)
    db.Set<OutboxMessage>().Add(new OutboxMessage { Topic = Topics.RefundRequested, Key = order.Id.ToString(), Payload = JsonSerializer.Serialize(refundMessage) });
```
Then Payment's [`RefundRequestedConsumer.cs`](../../src/Tadka.Payment.Api/Messaging/RefundRequestedConsumer.cs) calls the gateway's `RefundAsync`, sets the payment `Refunded`, and publishes `payment-refunded`, which the monolith's `PaymentRefundedConsumer` puts on the live-tracking stream. Both consumers dedupe with an Inbox, and Payment short-circuits an already-`Refunded` payment, so a redelivery never refunds twice. The levers live in `RestaurantAcceptanceOptions` (`AcceptMode`, `RefundOnReject`); `Saga:Mode` only changes how the sequence is *logged* (`Orchestration` prints step 1/2, 2/2), never what it does.

### Option space: undoing a step that already moved money
| Approach | What it is | Catch |
|---|---|---|
| **Compensating action** (what Tadka does) | A new forward step, `refund`, that semantically undoes the charge | Undo is business logic, not a DB rollback; needs idempotency |
| Void before capture | Authorize first, capture only after acceptance | Needs a gateway that supports auth/capture; changes the payment flow |
| Manual reconciliation | A human fixes stuck payments | It is the break case above, at scale |
| Distributed transaction (2PC) | One atomic commit across order and payment | Rejected in ADR-029: blocks under partition |

Two things juniors miss: a **timeout is not "didn't charge"**, so a refund needs an idempotency key and a reconciliation job, not a blind retry; and some steps are **pivotal** (cannot be undone), so put them last or make them retryable.

---

## 4. Fault isolation: kill Delivery (ADR-033)

**What you're proving:** Delivery is its own failure domain, same as Payment was on Day 8. Killing it degrades exactly one thing (live tracking) and nothing else.

Stop the Delivery service (Ctrl+C in its terminal), then:
```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "POST /orders: %{http_code}\n" -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY"   # 201
curl -s -o /dev/null -w "menu: %{http_code}\n" http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu   # 200
```
```powershell
# Self-contained: run this in ANY PowerShell window. It defines the helper, logs in and sets $BODY only if they are missing.
if (-not (Get-Command Get-StatusCode -ErrorAction SilentlyContinue)) {
    function Get-StatusCode {
        param($Uri, $Method = "GET", $Headers = @{}, $Body = $null, $ContentType = "application/json")
        try {
            $params = @{ Uri = $Uri; Method = $Method; Headers = $Headers; UseBasicParsing = $true }
            if ($Body) { $params.Body = $Body; $params.ContentType = $ContentType }
            return [int](Invoke-WebRequest @params).StatusCode
        } catch { if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode } else { throw } }
    }
}
$TOKEN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$H = @{ Authorization = "Bearer $TOKEN" }
if (-not $BODY) { $BODY = '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}' }

"POST /orders: " + (Get-StatusCode -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -Body $BODY)   # 201
"menu: " + (Get-StatusCode -Uri http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu)   # 200
```
**Captured live: 201, then 200.** Delivery down, and ordering, payment and browsing are all unaffected. Only live tracking degrades. The `order-confirmed` messages wait in Kafka, so on restart Delivery catches up and assigns the riders (restart it now before the next section).

### How this is actually implemented
There is nothing to implement, and that is the design. The monolith never calls Delivery: it only writes `order-confirmed` to its own Outbox. A dead consumer means messages wait in the topic, exactly as Payment's did on Day 9. The Delivery service has its own process, its own database (`delivery-db`) and its own JWT validation, so no shared resource can drag the others down.

### Option space: what protects the order path from a slow neighbour
| Approach | Order path when the neighbour is down |
|---|---|
| Synchronous HTTP call | Blocked or failed (Day 8's wound) |
| Sync HTTP + circuit breaker | Fails fast, still loses the feature (Day 14) |
| **Async event over Kafka** (what Tadka does) | Unaffected; the work waits and catches up |

---

## 4.5. The live-tracking stream needs the same ownership check as REST (ADR-031)

**What you're proving:** Day 10 taught resource ownership as a REST rule (`order.CustomerId == User.UserId()`). Day 11 opened a *new* kind of endpoint, a Server-Sent Events stream, and before this branch's `d2bb1c5` fix that check had been forgotten on it: any logged-in customer could stream **any** order's live status, and the rider's GPS riding along with it, just by guessing a guid. An ownership check is per endpoint, not something you learn once and get everywhere for free.

```bash
# Priya (the order's owner) opens her own stream; events start immediately (Ctrl+C to stop):
curl -s -N http://localhost:5224/api/v1/orders/$ORDER/events -H "Authorization: Bearer $TOKEN"
# Rahul (a different customer) tries the same order id:
RAHUL=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"rahul@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "Rahul streams Priya's order: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER/events -H "Authorization: Bearer $RAHUL"   # 403
```
```powershell
# Priya (the order's owner) opens her own stream (Ctrl+C to stop). curl.exe streams; Invoke-RestMethod would buffer:
curl.exe -s -N "http://localhost:5224/api/v1/orders/$ORDER/events" -H "Authorization: Bearer $TOKEN"
# Rahul (a different customer) tries the same order id:
$RAHUL = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"rahul@tadka.test","password":"Password123!"}').accessToken
"Rahul streams Priya's order: " + (Get-StatusCode -Uri "http://localhost:5224/api/v1/orders/$ORDER/events" -Headers @{ Authorization = "Bearer $RAHUL" })   # 403
```
**Captured live:** Priya's own stream opens and pushes `event: Confirmed` with the current status straight away; Rahul's attempt on the same order id returns **403** before the stream ever opens.

### How this is actually implemented
[`OrderTrackingController.GetEvents`](../../src/Tadka.Api/Controllers/OrderTrackingController.cs) does the same check as `OrdersController.GetById`, before touching the stream:
```csharp
var order = await _orders.GetByIdAsync(id);
if (order is null) return NotFound();
if (!User.IsAdmin() && order.CustomerId != User.UserId()) return Forbid();
```
`[Authorize]` on the class only proves *who you are*; this line decides *whether this order is yours*. That is the 401-versus-403 distinction from Day 10, now on a streaming endpoint.

### Option space: enforcing ownership
| Approach | Where the rule lives | Trade-off |
|---|---|---|
| **Inline check per endpoint** (what Tadka does) | In the action, next to the data load | Simple and honest for 4 roles; easy to forget on a new endpoint, which is exactly what happened here |
| Policy / resource-based authorization | One reusable handler applied by attribute | Forgetting becomes harder; more indirection |
| Spring Security `PermissionEvaluator`, NestJS guards, CASL abilities (Node) | Same idea in other stacks | Pick by stack |
| Push it into the query (`WHERE customer_id = @caller`) | The data layer | Cannot forget it, but hides the 403 vs 404 distinction |

---

## 4.6. …and so do Delivery's own endpoints, in a different service (ADR-031)

**What you're proving:** the SSE fix above lives in the monolith. Delivery is a separate service with its own `GET /deliveries/{orderId}/track` and `PUT /deliveries/{orderId}/location`, and it inherited nothing: until this fix both only required *a* valid token, so Rahul could read Priya's rider and live GPS, or move the rider's dot to the middle of the sea on her map. Delivery also had no way to check, because it has no copy of the orders table. So the fix is partly **data**: `order-confirmed` now carries `CustomerId` (event metadata, not a credential), Delivery stores it on the assignment, and each rider record is linked to that rider's login (`DeliveryAgent.UserId`).

```bash
curl -s -o /dev/null -w "Priya tracks her order: %{http_code}\n" http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"   # 200
curl -s -o /dev/null -w "Rahul tracks Priya's order: %{http_code}\n" http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $RAHUL"   # 403
curl -s -o /dev/null -w "Rahul moves Priya's rider: %{http_code}\n" -X PUT http://localhost:5250/api/v1/deliveries/$ORDER/location -H "Authorization: Bearer $RAHUL" -H "Content-Type: application/json" -d '{"latitude":12.0,"longitude":75.0}'   # 403
```
```powershell
"Priya tracks her order: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/track" -Headers $H)   # 200
"Rahul tracks Priya's order: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/track" -Headers @{ Authorization = "Bearer $RAHUL" })   # 403
"Rahul moves Priya's rider: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/location" -Method Put -Headers @{ Authorization = "Bearer $RAHUL" } -Body '{"latitude":12.0,"longitude":75.0}')   # 403
```
> Pinned by `DeliveryOwnershipTests` and `RealJwtAuthorizationTests`; not yet re-run against a live stack. An order placed **before** this change has no `CustomerId` on its assignment, so its owner gets 403 too (only Admin and the rider can read it): the safe default when there is no owner to compare against. Place a fresh order.

### How this is actually implemented
[`Tadka.Delivery.Api/Program.cs`](../../src/Tadka.Delivery.Api/Program.cs), per endpoint:
```csharp
static bool IsAssignedRider(ClaimsPrincipal user, DeliveryAgent? agent)
    => user.IsRider() && agent?.UserId is { } riderUser && riderUser == user.UserId();

// GET /track:    Admin, the customer who placed the order, or the rider on it
var isOwner = a.CustomerId is { } owner && owner == user.UserId();
if (!user.IsAdmin() && !isOwner && !IsAssignedRider(user, agent)) return Results.Forbid();

// PUT /location and PATCH /status: Admin or the rider on it (not even the owner)
if (!user.IsAdmin() && !IsAssignedRider(user, agent)) return Results.Forbid();
```
One more line matters as much as those: `options.MapInboundClaims = false;` in Delivery's JWT setup. Without it the JWT handler renames the `role` claim to a long legacy URI, `RoleClaimType = "role"` matches nothing, and `IsInRole("DeliveryAgent")` is false for every **real** rider token, so the assigned rider would get 403. The test suite's `TestAuthHandler` never goes through that renaming, which is exactly why `RealJwtAuthorizationTests` signs real tokens: with that one line commented out, it fails with `Expected: NoContent, Actual: Forbidden`.

---

## 4.7. A busy dinner rush no longer drops orders; riders are released

**What you're proving:** Delivery used to have two quiet holes that together meant a fresh database could assign exactly **three** orders, ever. (1) With no rider free it logged "left unassigned", the consumer stamped the Inbox and committed the offset, and nothing ever retried that order. (2) Nothing ever set a rider back to `Available`. Now an order that finds nobody free is **parked** in `delivery.pending_assignments` (durable, one row per order), and a background sweeper retries the oldest waiting orders every few seconds (`Delivery:PendingRetrySeconds`, default 5). A rider finishing a delivery frees themselves through a new endpoint:

```bash
# The rider on $ORDER: pick up, then deliver. Skipping pickup (Assigned → Delivered) returns 422.
curl -s -o /dev/null -w "picked up: %{http_code}\n" -X PATCH http://localhost:5250/api/v1/deliveries/$ORDER/status -H "Authorization: Bearer $RIDER" -H "Content-Type: application/json" -d '{"status":"PickedUp"}'    # 204
curl -s -o /dev/null -w "delivered: %{http_code}\n" -X PATCH http://localhost:5250/api/v1/deliveries/$ORDER/status -H "Authorization: Bearer $RIDER" -H "Content-Type: application/json" -d '{"status":"Delivered"}'   # 204 → rider is Available again
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "SELECT \"Name\",\"Status\" FROM delivery.agents;"
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "SELECT \"OrderId\",\"Attempts\",\"CreatedAt\" FROM delivery.pending_assignments;"
```
```powershell
"picked up: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/status" -Method Patch -Headers $HR -Body '{"status":"PickedUp"}')    # 204
"delivered: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/status" -Method Patch -Headers $HR -Body '{"status":"Delivered"}')   # 204
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "SELECT \"Name\",\"Status\" FROM delivery.agents;"
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "SELECT \"OrderId\",\"Attempts\",\"CreatedAt\" FROM delivery.pending_assignments;"
```
To see the parking itself: place four orders on a fresh stack (the three riders take the first three), and the fourth appears in `pending_assignments` with the log line `No available rider for order … parked in pending_assignments`. Deliver one of the first three as above, and within `PendingRetrySeconds` the log shows `Waiting order … finally got rider …` and the row is gone.
> Pinned by `PendingAssignmentTests`, `DeliveryStatusTests` and `ConcurrentAssignmentTests`; not yet re-run against a live stack.

### How this is actually implemented
[`DeliveryService.cs`](../../src/Tadka.Delivery.Api/DeliveryService.cs) (`AssignAsync`, `RetryPendingAsync`, `ChangeStatusAsync`) and [`PendingAssignmentSweeper.cs`](../../src/Tadka.Delivery.Api/PendingAssignmentSweeper.cs). Because a sweeper now assigns at the same time as the Kafka consumer, the rider claim became **atomic**; the one-assignment-per-order unique index never protected the rider:
```csharp
var rows = await db.Agents
    .Where(a => a.Id == candidate.Id && a.Status == AgentStatus.Available)
    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentStatus.OnDelivery), ct);
if (rows == 1) { claimed = ...; break; }   // 0 rows: someone else just took them, try the next candidate
```
The claim, the assignment insert and the removal of the parked row run in one transaction, so losing the order-level race also undoes the rider claim. `Delivered`/`Cancelled` release the rider in the same `SaveChanges` as the status change. Assignment is still **first-available**, not nearest.

---

## 4.8. A failing message is retried, then dead-lettered, never silently skipped (ADR-051)

**What was wrong:** every consumer in all three services had this loop:
```csharp
await HandleAsync(cr.Message.Value, stoppingToken);
consumer.Commit(cr);
...
catch (Exception ex) { logger.LogError(ex, "... will reprocess (idempotent)."); }
```
The Kafka client keeps its read position **in memory**. After a failure the next `Consume()` returned the *next* message, and committing that one moved the group's offset past the failed message too. A database blip on message 41 meant message 41 was skipped for good, while the log promised a retry.

**Now** (`PoisonMessageTracker.cs` plus `HandlePoisonAsync` in all 5 consumers): on a failure the consumer **seeks back** to the failed offset (300 ms pause), so the same message comes round again. On the 3rd failure it stops blocking the partition: a `DlqMessage` (original topic, the raw original payload, the error, the attempt count, a timestamp) is published to **`{topic}.dlq`** (for example `order-confirmed.dlq`), and only then is the offset committed. If even that publish fails, nothing is committed: the consumer seeks back and tries again, and the failure does not escape the loop (an exception out of a `catch` block would stop the whole hosted service). Dead-letter topics: `order-placed.dlq`, `refund-requested.dlq` (Payment); `payment-results.dlq`, `payment-refunded.dlq` (monolith); `order-confirmed.dlq` (Delivery).

To replay once the cause is fixed: `pwsh scripts/replay-dlq.ps1 -DlqTopic order-confirmed.dlq` republishes each quarantined payload onto its original topic (the handlers are idempotent, so a replay is safe). Kafka UI (`:8090`) shows every `*.dlq` topic.

> Pinned by `PoisonMessageTrackerTests` in each service's test project (Docker-free); the seek-and-dead-letter path has not yet been exercised against the live broker. The attempt counter is process-local, so a consumer restart gives a message a fresh 3 attempts, and three quick attempts can outlast only a very short outage (both are named in ADR-051).

---

## 4.9. The auth and PII hardening behind the three services (ADR-030/031, 052, 053, 060 to 062)

**What you're proving:** with three services verifying tokens, *how* they verify matters more. Nothing here shares a secret.

**RS256 + JWKS (ADR-067).** `Tadka.Api` signs access tokens with an RSA private key that never leaves its process and publishes the public keys. Payment and Delivery each fetch them (cached 5 minutes by `Jwt:JwksCacheMinutes`) and verify by `kid`, so a compromised Delivery can verify tokens but cannot mint one. There is no `Jwt:SigningKey` in any `appsettings.json`.
```bash
curl -s http://localhost:5224/.well-known/jwks.json           # the public keys (kty, kid, n, e)
ADMIN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"admin@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -X POST http://localhost:5224/api/v1/auth/rotate-signing-key -H "Authorization: Bearer $ADMIN"   # Admin only; a fresh key becomes current
curl -s -o /dev/null -w "old token after one rotation: %{http_code}\n" http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"
```
```powershell
Invoke-RestMethod -Uri http://localhost:5224/.well-known/jwks.json
$ADMIN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"admin@tadka.test","password":"Password123!"}').accessToken
Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/rotate-signing-key -Method Post -Headers @{ Authorization = "Bearer $ADMIN" }
"old token after one rotation: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/track" -Headers $H)
```
The monolith keeps the current key plus one previous, so a token signed before ONE rotation still verifies (the `track` call returns 200, not 401); after a second rotation that token is 401. A monolith restart generates new keys, so every token signed before the restart is rejected until the client refreshes.

**Access and refresh tokens (ADR-066).** Login returns a 15-minute access token and a 7-day refresh token. `POST /api/v1/auth/refresh` consumes the presented refresh token and returns a new pair; presenting an already-used one revokes the whole family (a theft signal). `POST /api/v1/auth/logout` revokes the caller's family (the current access token still works until it expires).

**Login is rate limited (ADR-065).** The auth endpoints allow 5 requests per 10 seconds per IP (`Auth:RateLimit`) and lock an account for 60 seconds after 5 wrong passwords (`Auth:Lockout`). **A script that logs several users in back to back can hit 429**: space the logins out, or raise `Auth:RateLimit:PermitLimit` for a demo run.

**Payment's HTTP surface is authorised, not just authenticated (ADR-031).** `POST /payments/charge` is Admin-only (the real charge flow runs off the `order-placed` Kafka event, in-process). `GET /payments/{orderId}` returns 403 unless the caller is the customer who placed the order (`order-placed` carries `CustomerId`) or Admin. `PATCH /orders/{id}/status` for a `RestaurantOwner` requires that the order belongs to their restaurant.
```bash
curl -s -o /dev/null -w "Rahul reads Priya's payment: %{http_code}\n" http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $RAHUL"   # 403
curl -s -o /dev/null -w "Priya reads her payment: %{http_code}\n" http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $TOKEN"            # 200
```
```powershell
"Rahul reads Priya's payment: " + (Get-StatusCode -Uri "http://localhost:5240/payments/$ORDER" -Headers @{ Authorization = "Bearer $RAHUL" })   # 403
"Priya reads her payment: " + (Get-StatusCode -Uri "http://localhost:5240/payments/$ORDER" -Headers $H)                                            # 200
```

**PII at rest and card tokens (ADR-052, ADR-053).** `identity.users.Phone` is AES-GCM encrypted in the database (random nonce, so it is not searchable) and decrypted transparently for the owner; the card number becomes a keyed HMAC token the instant it reaches Payment (`payment.payments` has `CardToken` and `CardLast4`, no PAN column). `Demo:EncryptPiiAtRest=false` turns field encryption off; flipping it on an existing volume fails with `FormatException`, so reset volumes when you change it.
```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\", \"Phone\" FROM identity.users LIMIT 3;"     # ciphertext, not phone numbers
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"CardToken\",\"CardLast4\" FROM payment.payments WHERE \"CardToken\" IS NOT NULL;"
```

**Swap the issuer for a real identity provider (Keycloak, optional).** Because Payment and Delivery verify through the JWKS contract, pointing them at Keycloak is configuration only. Keycloak listens on **host port 8081** here, because the gateway owns 8080.
```bash
docker compose --profile auth-prod up -d keycloak
curl -s http://localhost:8081/realms/tadka/.well-known/openid-configuration | grep jwks_uri
KC=$(curl -s -X POST http://localhost:8081/realms/tadka/protocol/openid-connect/token -d "client_id=tadka-api" -d "username=priya@tadka.test" -d "password=Password123!" -d "grant_type=password" | sed -E 's/.*"access_token":"([^"]+)".*/\1/')
dotnet run --project src/Tadka.Payment.Api --launch-profile Keycloak     # src/Tadka.Delivery.Api has the same profile name
curl -s -o /dev/null -w "Payment with a Keycloak token: %{http_code}\n" http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $KC"
docker compose --profile auth-prod down
```
```powershell
docker compose --profile auth-prod up -d keycloak
curl.exe -s http://localhost:8081/realms/tadka/.well-known/openid-configuration
$KC = (Invoke-RestMethod -Uri http://localhost:8081/realms/tadka/protocol/openid-connect/token -Method Post -Body @{ client_id = "tadka-api"; username = "priya@tadka.test"; password = "Password123!"; grant_type = "password" }).access_token
dotnet run --project src/Tadka.Payment.Api --launch-profile Keycloak
"Payment with a Keycloak token: " + (Get-StatusCode -Uri "http://localhost:5240/payments/$ORDER" -Headers @{ Authorization = "Bearer $KC" })
docker compose --profile auth-prod down
```
The realm (`infra/keycloak/tadka-realm.json`) also defines the three riders (`suresh.rider@`, `lakshmi.rider@`, `imran.rider@tadka.test`) with role `DeliveryAgent`. Full walkthrough: [`docs/learn/keycloak-integration-showcase.md`](../learn/keycloak-integration-showcase.md); the token lifecycle: [`docs/learn/token-and-refresh-flow.md`](../learn/token-and-refresh-flow.md).

> **Not re-run against a live stack on this branch:** the commands in this section were carried over from the Day 10 runbook (where they were run live) and adapted for this branch (the Keycloak port, the gateway). The behaviour itself is pinned by the test suite (`JwksTests`, `RefreshTokenTests`, `RateLimitingTests`, `AuthorizationTests`, `JwksValidationTests`, `RealJwtAuthorizationTests`, `FieldCipherTests`, `CardTokenizerTests`, `PaymentServiceTests`).

---

## 5. API gateway: one entry point (ADR-035)

**What you're proving:** the mobile client should not need to know there are 3 backend hosts. One host, `:8080`, routes by path, and per-service auth still holds underneath, so the gateway is a router, not a trust boundary.

```bash
curl -s -o /dev/null -w "login via gateway: %{http_code}\n" -X POST http://localhost:8080/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}'   # 200 → monolith
curl -s -o /dev/null -w "restaurants via gateway: %{http_code}\n" http://localhost:8080/api/v1/restaurants                                   # 200 → monolith
curl -s -o /dev/null -w "payment via gateway (no token): %{http_code}\n" http://localhost:8080/api/v1/payments/$ORDER                        # 401 → payment (per-service auth, even through the gateway)
curl -s -o /dev/null -w "delivery via gateway: %{http_code}\n" http://localhost:8080/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"   # 200 → delivery
```
```powershell
"login via gateway: " + (Get-StatusCode -Uri http://localhost:8080/api/v1/auth/login -Method Post -Body '{"email":"priya@tadka.test","password":"Password123!"}')   # 200
"restaurants via gateway: " + (Get-StatusCode -Uri http://localhost:8080/api/v1/restaurants)   # 200
"payment via gateway (no token): " + (Get-StatusCode -Uri "http://localhost:8080/api/v1/payments/$ORDER")   # 401
"delivery via gateway: " + (Get-StatusCode -Uri "http://localhost:8080/api/v1/deliveries/$ORDER/track" -Headers $H)   # 200
```
**Captured live: 200 / 200 / 401 / 200.** Login and browsing route to the monolith; payment through the gateway with no token is still 401, so per-service auth held even through the front door; delivery routes correctly with the token. Routing: `/api/v1/payments/**` goes to payment (path transformed to `/payments/**`), `/api/v1/deliveries/**` to delivery, everything else to the monolith. The **edge rate-limit** is a fixed window per IP (tune `Gateway:RateLimitPerMinute`; a burst past it returns **429**).

One more honest edge case: stop Delivery and hit its route through the gateway. You get **502**, plainly, because the target is down. A single YARP instance is a new single point of failure the three-separate-hosts world did not have; production runs it behind its own load balancer.

### How this is actually implemented
[`src/Tadka.Gateway/Program.cs`](../../src/Tadka.Gateway/Program.cs) is under 40 lines and does exactly two things, routing and rate limiting, and deliberately nothing else:
```csharp
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
... RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", ...)
app.MapReverseProxy();   // forwards Authorization as-is; each service validates the JWT itself
```
The route table is data (`ReverseProxy` in `appsettings.json`), not code, so adding a service is a config change.

### Option space: an edge in front of the services
| Option | Fits | Watch out for |
|---|---|---|
| **YARP** (what Tadka does) | .NET teams, code-level control | You own its availability |
| Spring Cloud Gateway | Java stacks | Same shape |
| Kong, Envoy, Traefik, nginx | Any stack, plugin ecosystems | More moving parts to operate |
| Cloud-managed (AWS ALB + API Gateway, Azure App Gateway + APIM, GCP Cloud Load Balancing) | Production | Vendor features and cost |

**The trap to name:** the "God Gateway". Business logic creeps in ("call Payment, then Order, then merge the JSON") and it becomes a second monolith. Keep it dumb: routing and rate limiting only.

---

## 6. PgBouncer: the Day-5 promise comes due (ADR-015 landed)

Day 5 showed that one `Tadka.Api` instance cannot truly exhaust a pool on a laptop. Day 11 is the first day with **2+ instances**, so this is where the real demo lands. Each instance keeps its own Npgsql pool (`Maximum Pool Size=60`), the pools know nothing about each other, and 2 x 60 = 120 is more than Postgres's `max_connections` of 100.

**What this section is.** A side experiment, separate from your normal stack. You do **not** stop or restart any of your normal services (Postgres, PgBouncer, Kafka, Payment, Delivery, Gateway, and the monolith on `:5224` all keep running). You start **two extra copies** of the monolith on ports `5226` and `5227`, fire the same burst of requests at them twice, and compare how many real Postgres connections each run used:

| Run | The two extra copies connect to | What to look at |
|---|---|---|
| A. Direct | Postgres `:5432` (each copy keeps its own 60-connection pool) | peak real connections, any failed requests |
| B. Via PgBouncer | PgBouncer `:6432` (many app connections share a few real ones) | peak real connections, and `SHOW POOLS` |

**Before you start:** run `docker compose ps` and confirm `tadka-pgbouncer` is `healthy` (it was added to `docker-compose.yml` on this branch; `docker compose up -d` starts it). Your normal stack already holds about 4 to 6 Postgres connections, so every number below includes that baseline.

> **Windows: `--no-build` is required.** Your running monolith has `Tadka.Api.exe` open. A plain `dotnet run` tries to rebuild it, cannot overwrite the locked file, and fails with `MSB3021 ... file is locked by Tadka.Api`. `--no-build` reuses the binaries you already built. (Run `dotnet build Tadka.slnx` once first if you have not built this branch yet.) The extra copies also get `Kafka__BootstrapServers=` (empty), which switches Kafka off for them, so they do not join the monolith's consumer group and disturb your normal stack.

### Step 1. Start the two extra copies, connected directly to Postgres
```bash
Kafka__BootstrapServers= ASPNETCORE_URLS=http://localhost:5226 ConnectionStrings__TadkaDb="Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=5;Maximum Pool Size=60" dotnet run --project src/Tadka.Api --no-launch-profile --no-build &
Kafka__BootstrapServers= ASPNETCORE_URLS=http://localhost:5227 ConnectionStrings__TadkaDb="Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=5;Maximum Pool Size=60" dotnet run --project src/Tadka.Api --no-launch-profile --no-build &
until curl -sf http://localhost:5226/health >/dev/null && curl -sf http://localhost:5227/health >/dev/null; do sleep 2; done; echo "both copies are up"
```
```powershell
$env:ConnectionStrings__TadkaDb = "Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=5;Maximum Pool Size=60"
$env:Kafka__BootstrapServers = ""
foreach ($port in 5226,5227) {
  $env:ASPNETCORE_URLS = "http://localhost:$port"
  Start-Process dotnet -ArgumentList "run","--project","src/Tadka.Api","--no-launch-profile","--no-build" -WindowStyle Minimized
}
$env:ConnectionStrings__TadkaDb = $null; $env:ASPNETCORE_URLS = $null; $env:Kafka__BootstrapServers = $null
foreach ($port in 5226,5227) { do { Start-Sleep -Seconds 2; try { $c = [int](Invoke-WebRequest "http://localhost:$port/health" -UseBasicParsing -TimeoutSec 3).StatusCode } catch { $c = 0 } } until ($c -eq 200); "port $port is up" }
```
> `--no-launch-profile` still matters: without it `dotnet run` applies `launchSettings.json`'s own `applicationUrl` (`:5224`) *after* your `ASPNETCORE_URLS`, and both copies would try to take the same port and crash. In PowerShell, `Start-Process` opens a minimized window for each copy; you will see them in the taskbar.

### Step 2. Run A: fire the burst and measure the peak
The number that matters is the **peak** count of real Postgres connections *during* the burst. Postgres closes idle connections within moments, so a count taken after the burst is too low. This starts a watcher that samples the count for about 14 seconds, then fires 150 requests at each copy:
```bash
( m=0; end=$((SECONDS+14)); while [ $SECONDS -lt $end ]; do n=$(docker exec tadka-postgres psql -U tadka -d tadka -t -A -c "SELECT count(*) FROM pg_stat_activity WHERE usename='tadka';"); [ "$n" -gt "$m" ] && m=$n; done; echo "$m" > /tmp/peak.txt ) &
WATCH=$!
sleep 1
powershell.exe -NoProfile -Command "& './docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1' -Urls @('http://localhost:5226','http://localhost:5227') -Label 'A: DIRECT :5432' -RequestsPerInstance 150"
wait $WATCH; echo "Peak real Postgres connections during the burst: $(cat /tmp/peak.txt)"
```
```powershell
$watch = Start-Job { $m = 0; $end = (Get-Date).AddSeconds(14); while ((Get-Date) -lt $end) { $n = [int](docker exec tadka-postgres psql -U tadka -d tadka -t -A -c "SELECT count(*) FROM pg_stat_activity WHERE usename='tadka';"); if ($n -gt $m) { $m = $n } }; $m }
Start-Sleep -Seconds 1
.\docs\demo-scripts\02-pgbouncer-connection-exhaustion.ps1 -Urls @("http://localhost:5226","http://localhost:5227") -Label "A: DIRECT :5432" -RequestsPerInstance 150
"Peak real Postgres connections during the burst: " + (Receive-Job $watch -Wait)
```
> The load script is PowerShell either way (`pwsh` is not on a stock Windows machine, so the bash line calls `powershell.exe`). Pass `-Urls` as an array; a single comma-joined string binds as one bad URI.

Write down two numbers: **Failed** (from the script's `RESULTS` box) and the **peak connections** line.

### Step 3. Stop the two extra copies
Only these two. Your normal stack on `:5224`, `:5240`, `:5250` and `:8080` is not touched. This works from any PowerShell window (on macOS or Linux use `kill $(lsof -ti :5226 :5227)`):
```powershell
foreach ($p in 5226,5227) { Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force } }
```

### Step 4. Start the two copies again, this time through PgBouncer
Only the port in the connection string changes (`5432` becomes `6432`), plus `Pooling=false`. In transaction-pooling mode a physical connection can be handed to a different client between statements, so .NET's own client-side pooling must be off. Otherwise Npgsql may reuse session state PgBouncer has already wiped (see `docs/database/connection-pooling-guide.md`).
```bash
Kafka__BootstrapServers= ASPNETCORE_URLS=http://localhost:5226 ConnectionStrings__TadkaDb="Host=localhost;Port=6432;Database=tadka;Username=tadka;Password=tadka_local;Pooling=false" dotnet run --project src/Tadka.Api --no-launch-profile --no-build &
Kafka__BootstrapServers= ASPNETCORE_URLS=http://localhost:5227 ConnectionStrings__TadkaDb="Host=localhost;Port=6432;Database=tadka;Username=tadka;Password=tadka_local;Pooling=false" dotnet run --project src/Tadka.Api --no-launch-profile --no-build &
until curl -sf http://localhost:5226/health >/dev/null && curl -sf http://localhost:5227/health >/dev/null; do sleep 2; done; echo "both copies are up"
```
```powershell
$env:ConnectionStrings__TadkaDb = "Host=localhost;Port=6432;Database=tadka;Username=tadka;Password=tadka_local;Pooling=false"
$env:Kafka__BootstrapServers = ""
foreach ($port in 5226,5227) {
  $env:ASPNETCORE_URLS = "http://localhost:$port"
  Start-Process dotnet -ArgumentList "run","--project","src/Tadka.Api","--no-launch-profile","--no-build" -WindowStyle Minimized
}
$env:ConnectionStrings__TadkaDb = $null; $env:ASPNETCORE_URLS = $null; $env:Kafka__BootstrapServers = $null
foreach ($port in 5226,5227) { do { Start-Sleep -Seconds 2; try { $c = [int](Invoke-WebRequest "http://localhost:$port/health" -UseBasicParsing -TimeoutSec 3).StatusCode } catch { $c = 0 } } until ($c -eq 200); "port $port is up" }
```

### Step 5. Run B: the same burst, the same measurement
```bash
( m=0; end=$((SECONDS+14)); while [ $SECONDS -lt $end ]; do n=$(docker exec tadka-postgres psql -U tadka -d tadka -t -A -c "SELECT count(*) FROM pg_stat_activity WHERE usename='tadka';"); [ "$n" -gt "$m" ] && m=$n; done; echo "$m" > /tmp/peak.txt ) &
WATCH=$!
sleep 1
powershell.exe -NoProfile -Command "& './docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1' -Urls @('http://localhost:5226','http://localhost:5227') -Label 'B: VIA PGBOUNCER :6432' -RequestsPerInstance 150"
wait $WATCH; echo "Peak real Postgres connections during the burst: $(cat /tmp/peak.txt)"
```
```powershell
$watch = Start-Job { $m = 0; $end = (Get-Date).AddSeconds(14); while ((Get-Date) -lt $end) { $n = [int](docker exec tadka-postgres psql -U tadka -d tadka -t -A -c "SELECT count(*) FROM pg_stat_activity WHERE usename='tadka';"); if ($n -gt $m) { $m = $n } }; $m }
Start-Sleep -Seconds 1
.\docs\demo-scripts\02-pgbouncer-connection-exhaustion.ps1 -Urls @("http://localhost:5226","http://localhost:5227") -Label "B: VIA PGBOUNCER :6432" -RequestsPerInstance 150
"Peak real Postgres connections during the burst: " + (Receive-Job $watch -Wait)
```

### Step 6. See PgBouncer itself (the "where can I see it" part)
PgBouncer has its own admin console. This asks it how its pool looks right now (works in either shell, since it just calls `docker exec`):
```bash
docker exec -e PGPASSWORD=tadka_local tadka-postgres psql -h pgbouncer -p 5432 -U tadka pgbouncer -c "SHOW POOLS"
docker exec -e PGPASSWORD=tadka_local tadka-postgres psql -h pgbouncer -p 5432 -U tadka pgbouncer -c "SHOW STATS"
```
In the `SHOW POOLS` row for database `tadka`: `pool_mode` is `transaction`, `cl_active` / `cl_waiting` are the **client** (app) connections PgBouncer is serving or holding, and `sv_active` / `sv_idle` are the few **real** connections to Postgres. Run it *during* a burst (start the burst, then run this in another terminal) to see `cl_active` jump far above `sv_active`. `SHOW STATS` has the totals (`total_xact_count`, `avg_wait_time`). To see the real connections on the Postgres side, by state: `docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT usename, state, count(*) FROM pg_stat_activity GROUP BY 1,2"`.

### Step 7. Clean up
Run the Step 3 command again to stop the two extra copies. Nothing else needs restarting: the normal stack never changed.

### What you should see
Measured on this branch (Windows, Docker Desktop), 150 requests at each of two copies:

| | A. Direct to `:5432` | B. Via PgBouncer `:6432` |
|---|---|---|
| Requests that succeeded | 295 to 300 of 300 | 300 of 300 |
| Peak real Postgres connections | about 55 to 101 (101 means the ceiling of 100 was reached) | about 12 |
| Wall clock for the 300 requests | about 0.5 to 1.3 s | about 1.9 to 2.1 s |
| p99 latency | about 90 to 550 ms | about 700 to 840 ms |
| `SHOW POOLS`, real (`sv_*`) connections | n/a | about 8 to 10, capped by `default_pool_size=20` |

**Read this before you promise a failure on stage.** Failures on the direct run are **not deterministic**. One run saw 5 of 300 requests fail with HTTP 500 (Npgsql `"sorry, too many clients already"`); the next runs returned 300/300 and even 800/800 with zero failures. The peak on the direct run also varies with how warm the stack is and how much of `/api/v1/restaurants` is served from the Redis cache (cached reads need no database connection): an earlier capture showed `100 of 100` allowed connections, the latest showed 57. What does **not** vary is the shape: direct connections grow with `instances x pool size` and with concurrency until they hit `max_connections` (100), while through PgBouncer they stay near a small fixed number no matter how many clients connect. So the lesson to show is the peak connection count, with failed requests as a bonus when they happen. For more pressure, raise `-RequestsPerInstance` or run Step 2 immediately after starting the copies, before their caches warm.

`Pooling=false` is what a real service ships with once it is behind PgBouncer, per `connection-pooling-guide.md`. The first run used `Maximum Pool Size=60` on the direct leg and `Pooling=false` on the PgBouncer leg; mention that if a student asks why the two connection strings differ. Full numbers: ADR-015.

### How to read these numbers (and what to say in class)
Three things in the table are not what people expect, so say them out loud before a student asks:

1. **Zero failures direct does not mean it was safe.** A direct run can sit at `100 of 100` connections and still return 300/300, because connections free up within milliseconds and Npgsql waits for one. The danger is zero headroom, not guaranteed errors. One more instance, one migration or one engineer running `psql` at that moment would be refused with "too many clients".
2. **PgBouncer is slower here, and that is honest.** In every run it cost latency (about 1.5 to 4 times the wall clock in the warm direct runs). Three reasons: every request opens a fresh connection to PgBouncer because the app runs with `Pooling=false` (a login each time), requests queue for 10 to 20 backend slots, and there is one more network hop. PgBouncer buys safety and headroom, not speed.
3. **`SHOW STATS` totals are cumulative** since PgBouncer started, so they include earlier runs. Do not quote `total_xact_count` as "the 300 requests". The useful figures are `avg_wait_time` (microseconds a client waited for a backend; about 300 here, so almost none) and `total_server_assignment_count` equal to `total_xact_count` (every transaction borrowed a backend connection and returned it).

**Why 12 and not 20?** 20 is the cap, not the target. Each query takes about 2 ms, so the burst needed only about 10 backends at once. The 12 is your normal stack's baseline (about 4 to 6 connections, plus the watcher) plus the pooled ones.

**A script for the room (about 90 seconds):**
- *Setup.* "Each app instance keeps up to 60 connections, and Postgres allows 100 in total. Day 5 had one instance, so it looked fine. Two instances means 120 people wanting a seat in a 100-seat hall."
- *After run A.* "Peak is about 100 out of 100. The hall is full. Nothing failed this time, and I will not promise you it fails on demand. But there are no spare seats. That is a ticking bomb, not an error."
- *After run B.* "Same 300 requests, same two instances, 12 connections instead of 100. PgBouncer is the host at the door: 300 guests wait in the lobby while a few tables turn over quickly, because each transaction holds a table for about 2 ms. `SHOW POOLS` shows the guests (`cl_*`) on one side and the few real tables (`sv_*`) on the other."
- *The cost, said before anyone asks.* "It is slower here: about 1.9 seconds against 1.3. We added a hop, a queue and a login per request. We spend a little latency to buy headroom."

### PgBouncer: advantages and disadvantages
| Advantage | In this demo |
|---|---|
| Fewer real database connections (many app connections share a few real ones) | about 12 instead of about 100 for the same 300 requests |
| Headroom under `max_connections` | direct leaves 0 spare connections; via PgBouncer about 85 are free |
| Scale-out is safe: more app instances do not mean more database connections | two instances at 60 each behave like a small fixed pool |
| Spikes queue at PgBouncer instead of being refused by Postgres | 300 clients served by about 10 backends, average wait about 0.3 ms |
| One central place for pool limits and stats | `SHOW POOLS` and `SHOW STATS` cover every instance |
| Stack-agnostic | only the port in the connection string changed (5432 to 6432) |
| Less memory: each real connection is a server process of about 1 to 3 MB | about 12 processes instead of about 100 |

| Disadvantage | What it means |
|---|---|
| Extra latency: another hop, a queue for a backend slot, and a login per request when the app uses `Pooling=false` | about 1.5 times the wall clock in the captured run |
| Transaction mode breaks session features: advisory locks across statements, `LISTEN/NOTIFY`, session-level `SET`, prepared statements spanning transactions | the app must set `Pooling=false` so it does not reuse state PgBouncer already wiped |
| One more component to run, monitor and keep available, and it sits on the path of every query | if PgBouncer is down, every app that uses it loses the database; production runs at least two |
| It hides problems, it does not fix them | a slow query or long transaction still holds a backend, so Day 5's indexing still matters |
| Not a performance tool | it does not make a single query faster |
| Another setting to tune (`default_pool_size`, `MAX_CLIENT_CONN`) | too small and clients queue, too large and you are back at the ceiling |
| Overkill for small systems | at Tadka's real scale (about 1.2 orders per second on average) you would not need it; it appears here because the demo runs 2 or more instances |

**When to use it:** when **instances x pool size is more than `max_connections`** (here 2 x 60 = 120 against 100), or when many small services or serverless functions each open their own connections. Skip it for one or two instances with small pools; set `Maximum Pool Size` sensibly instead.

**The trade-off in one line:** you give up a little latency, some session features and one more thing to operate, and in return the database stops being the thing that falls over when you scale out.

### How this is actually implemented
Two pieces. In [`docker-compose.yml`](../../docker-compose.yml) a `pgbouncer` service runs in **transaction** pooling mode on `:6432` with `default_pool_size=20`, so at most about 20 real backend connections regardless of how many clients connect. The apps change nothing except the port in their connection string (and `Pooling=false`). The load generator, [`docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1`](../../docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1), fires the requests; which port each instance's pool points at is decided at instance startup by `ConnectionStrings__TadkaDb`.

### Option space: taming connection counts
| Option | What it does | Catch |
|---|---|---|
| Shrink each app's `Maximum Pool Size` | Keeps the sum under the limit | You guess a number that must hold as instance count changes |
| **PgBouncer, transaction mode** (what Tadka does) | Many client connections share a small backend pool | Session features (advisory locks held across statements, `LISTEN/NOTIFY`, prepared statements spanning transactions) do not work reliably |
| PgBouncer, session mode | Preserves session state | Far less multiplexing |
| pgpool-II | Pooling plus load balancing | More to run and tune |
| Managed proxy (RDS Proxy, cloud poolers) | Same idea, managed | Vendor cost; the cloud equivalent on Day 12 |

**What PgBouncer is and is not:** it does not make Postgres accept more connections. It makes many client-side connections share a few real ones. The pattern (many client pools into one small backend pool) is universal; PgBouncer sits in front of Postgres identically whether the app is .NET, Java, Node or Go.

---

## 7. Run the tests
```bash
dotnet test    # 114/114: monolith 63 + Payment 28 + Delivery 23
```
```powershell
dotnet test
```
> The count went from 59 to 114 with the Delivery ownership, rider-lifecycle and dead-letter fixes (sections 4.6 to 4.8) and the auth and PII hardening (section 4.9): `DeliveryOwnershipTests`, `PendingAssignmentTests`, `DeliveryStatusTests`, `ConcurrentAssignmentTests`, `RealJwtAuthorizationTests`, and a Docker-free `PoisonMessageTrackerTests` in each service. If you last checked this number before `4a5988e`/`d2bb1c5` landed you may remember `40/40`; those two commits added `LocationTrackingTests.cs` (3) and `OrderTrackingAuthorizationTests.cs` (4) to the suites. `dotnet test` is the source of truth, not a number in a doc. The integration tests use Testcontainers, so Docker must be running.

---

## 8. Wiring Reference & Cross-Stack Architecture

### Where the code lives in Tadka
- **Delivery extraction (ADR-033):** [`src/Tadka.Delivery.Api/DeliveryService.cs`](../../src/Tadka.Delivery.Api/DeliveryService.cs) (assignment), [`Messaging/OrderConfirmedConsumer.cs`](../../src/Tadka.Delivery.Api/Messaging/OrderConfirmedConsumer.cs), [`Data/DeliveryDbContext.cs`](../../src/Tadka.Delivery.Api/Data/DeliveryDbContext.cs) (own Postgres, own migration history, the ADR-026 pattern reused).
- **Redis-geo (ADR-034):** [`LocationStore.cs`](../../src/Tadka.Delivery.Api/LocationStore.cs) (`GEOADD`/`GEOPOS`), [`OrderTrackingPublisher.cs`](../../src/Tadka.Delivery.Api/OrderTrackingPublisher.cs) (re-publishes location pings onto the Day-6 SSE backplane, ADR-020/036).
- **API gateway (ADR-035):** [`src/Tadka.Gateway/Program.cs`](../../src/Tadka.Gateway/Program.cs), YARP routes plus a fixed-window per-IP limiter.
- **Refund saga (ADR-045):** [`RestaurantAcceptanceOptions.cs`](../../src/Tadka.Api/Domain/Restaurants/RestaurantAcceptanceOptions.cs), [`RefundSagaOrchestrator.cs`](../../src/Tadka.Api/Infrastructure/Messaging/RefundSagaOrchestrator.cs), [`PaymentRefundedConsumer.cs`](../../src/Tadka.Api/Infrastructure/Messaging/PaymentRefundedConsumer.cs) (monolith); [`RefundRequestedConsumer.cs`](../../src/Tadka.Payment.Api/Messaging/RefundRequestedConsumer.cs), `PaymentService.RefundAsync` (Payment).
- **SSE ownership fix (ADR-031):** [`OrderTrackingController.cs`](../../src/Tadka.Api/Controllers/OrderTrackingController.cs), `GetEvents`.
- **Delivery ownership + rider logins (ADR-031/033):** [`src/Tadka.Delivery.Api/Program.cs`](../../src/Tadka.Delivery.Api/Program.cs) (`/track`, `/location`, `/status`), `DeliveryAgent.UserId`, `DeliveryAssignment.CustomerId`; rider accounts in the monolith's `AuthSeeder`.
- **Parked orders + rider release:** [`DeliveryService.cs`](../../src/Tadka.Delivery.Api/DeliveryService.cs), [`PendingAssignmentSweeper.cs`](../../src/Tadka.Delivery.Api/PendingAssignmentSweeper.cs), table `delivery.pending_assignments`.
- **Consumer retry + dead-letter:** `PoisonMessageTracker.cs` and `HandlePoisonAsync` in each service's consumers; `{topic}.dlq` topics; `scripts/replay-dlq.ps1`.
- **PgBouncer (ADR-015):** the `pgbouncer` service in [`docker-compose.yml`](../../docker-compose.yml); demo load in [`docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1`](../../docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1).

### Cross-Stack Implementation Matrix

| Concern | .NET Core (This Repo) | Java (Spring Boot) | Node.js (TypeScript) | Go |
|---|---|---|---|---|
| **Live-location geo store** | `StackExchange.Redis` `GEOADD`/`GEOPOS` | Lettuce / Jedis, identical Redis GEO commands | `ioredis`, identical command surface | `go-redis`, identical command surface |
| **Reverse-proxy gateway** | YARP | Spring Cloud Gateway | Express-gateway / a thin Node proxy | Kong / Envoy / Traefik |
| **Cloud-managed gateway** | n/a | AWS ALB + API Gateway | Azure App Gateway + APIM | GCP Cloud Load Balancing + Apigee/Kong |
| **Saga orchestration (4+ participants)** | MassTransit state machine / NServiceBus saga | Axon `@SagaEventHandler` / Camunda-Zeebe | Temporal (TS SDK) | Temporal (Go SDK) |
| **Connection pooling in front of Postgres** | PgBouncer (stack-agnostic) | same PgBouncer | same PgBouncer | same PgBouncer |

**Pattern is language-neutral; the engine matches your stack and flow complexity.** Choreography (what Tadka does, here and through ADR-045) is "services react to domain events", expressible in Spring Kafka, kafkajs or kafka-go with no change in shape.

---

## 9. Demo vs. Production: named gaps, not overclaimed features

- **Rider assignment is first-available, not nearest,** though the claim is now atomic so one rider never gets two orders. ADR-034 names `GEOSEARCH` for "nearby agents", but `DeliveryService.cs` has no `ORDER BY` distance and never calls `GEOSEARCH`: whichever `Available` rider the query returns first gets the order, even if a closer one exists. Proximity-based dispatch is the natural next step, not yet built. Real dispatch uses `GEOSEARCH` or geohash, and at extreme scale H3/S2 cells.
- **A single gateway instance is a new SPOF.** Section 5 showed the 502 when a target is down; the gateway itself needs 2+ instances behind its own load balancer in production.
- **The refund saga is choreographed and in-process on Day 11, both temporary by design.** ADR-045 names its own revisit triggers: once Restaurant is extracted (Day 12) the `AcceptMode` decision belongs in `Restaurant.Api` reacting to `order-confirmed`; and a refund that fails at the gateway needs its own failure path and a reconciliation job, not yet modelled.
- **Delivery still publishes `delivery-assigned` straight to Kafka** (no Delivery-side Outbox). A crash between the assignment commit and the publish loses that announcement, not the assignment. Nothing consumes `delivery-assigned` yet.
- **A poison message is parked, not fixed.** `{topic}.dlq` needs someone watching it; there is no alert on DLQ depth, and a replay is a manual script run.
- **Rider accounts are demo seeds.** A rider record links to one login (`DeliveryAgent.UserId`); onboarding a new rider means creating both, by hand, in two services.
- **PgBouncer transaction mode has real limits.** No reliable session state across statements, so anything using advisory locks across calls, `LISTEN/NOTIFY` or long-lived prepared statements needs session mode or a direct connection.

- **Kafka is authenticated, not encrypted or isolated.** The broker requires SASL/SCRAM-SHA-256, which stops anonymous access to every topic. It is `SASL_PLAINTEXT` (production uses `SASL_SSL`), every service and tool shares one `tadka` user with no ACLs (real isolation is a user per service plus topic ACLs), and the password is a demo default committed to `appsettings.Development.json`. The Azure/cloud Kafka is a separate plain container reachable only inside the private network and is not covered. See ADR-027's security addendum.

## ✅ Done when
- [ ] Order paid → Confirmed → `track` shows an assigned rider (3-service saga).
- [ ] PUT location → `track` returns it (Redis-geo); `GEOPOS` shows the raw entry.
- [ ] Restaurant reject + refund: `AcceptMode=Reject, RefundOnReject=true` → order Cancelled, payment **Refunded** with a new gateway reference.
- [ ] The break lever: `RefundOnReject=false` → order Cancelled, payment stays **Completed**, money stuck, and the log says so.
- [ ] Delivery **down** → orders 201 + menu 200 (fault isolation).
- [ ] Live-tracking SSE stream: the order's owner gets events; a different customer's token on the same order id gets **403**.
- [ ] All services reachable via **one host** `:8080`; payment-no-token via gateway still **401**; a dead route's target returns **502**.
- [ ] PgBouncer: the peak of real Postgres connections during the burst is clearly higher direct to `:5432` (about 55 to 100) than via `:6432` (about **12**), and `SHOW POOLS` shows `pool_mode = transaction`.
- [ ] Delivery ownership: Rahul on Priya's `track` gets **403**; Priya's token on `PUT location` gets **403**; the rider's own token gets **204**.
- [ ] Rider lifecycle: `PickedUp` then `Delivered` returns the rider to `Available`; a 4th order on a fresh stack waits in `pending_assignments` and gets that rider on the next sweep.
- [ ] RS256: `/.well-known/jwks.json` lists the public keys; tokens verify in all three services with no shared secret; one rotation keeps old tokens valid, a second rejects them.
- [ ] Payment authz: Rahul on Priya's payment gets **403**, Priya **200**; `POST /payments/charge` with a customer token gets **403**.
- [ ] `dotnet test` → **114/114**.

## Troubleshooting
- **First order stays `Created` for a long time after a restart:** cold JIT plus Kafka consumers joining their group. Wait up to a minute; later orders confirm in about a second.
- **No rider assigned:** is the Delivery service up and `tadka-kafka` healthy? Check its log for `OrderConfirmedConsumer subscribed` and `🛵 Order … assigned to rider`. The monolith publishes `order-confirmed` on auto-confirm after payment. If the log says `parked in pending_assignments`, every rider is busy: deliver one (section 4.7) or `docker compose down -v` for fresh riders.
- **`track` returns 403 for the order's real owner:** the order was placed before `order-confirmed` carried `CustomerId`, so its assignment has no owner on record. Place a fresh order.
- **The rider's token gets 403 on `location` / `status`:** it is a different rider's token. Log in as the rider named in `track`'s `agentName` (`<name>.rider@tadka.test`).
- **A `*.dlq` topic appears in Kafka UI:** a message failed 3 times. Its `DlqMessage` JSON carries the error and the original payload; fix the cause, then run `scripts/replay-dlq.ps1 -DlqTopic <that topic>`.
- **`track` location is null:** PUT a location first; Redis must be up (`Redis` in the Delivery service's `appsettings.Development.json`).
- **Refund never happens / payment stays `Completed` with `RefundOnReject=true`:** it is a 2-hop Kafka round trip (`refund-requested`, then `payment-refunded`). If still stuck past a minute, check the monolith log for `PaymentRefundedConsumer subscribed to payment-refunded` and Payment's for `RefundRequestedConsumer subscribed`.
- **SSE stream returns 403 for the order's real owner:** wrong token. Re-login and confirm the `sub` claim matches the order's `customerId`.
- **Gateway 502 on a route:** the target service is down; start all three before the gateway demo.
- **Both `Tadka.Api` instances crash on startup in the PgBouncer demo:** you forgot `--no-launch-profile`; `launchSettings.json`'s `applicationUrl` overrides `ASPNETCORE_URLS` and both fight over `:5224`.
- **The two extra copies in section 6 fail with MSB3021 ... file is locked by Tadka.Api (Windows):** your running monolith has Tadka.Api.exe open and dotnet run tried to rebuild it. Add --no-build (run dotnet build Tadka.slnx once first if the branch was never built).
- **The demo script says every request failed with "Invalid URI":** `-Urls` was passed as one comma-joined string. Pass a real array (`@("http://…","http://…")`).
- **`pwsh: command not found`:** `pwsh` (PowerShell 7) is not installed by default. The demo script runs fine under Windows PowerShell 5.1: use `powershell.exe` as shown above.
- **PgBouncer `SHOW POOLS` fails with "not allowed":** connect as the `tadka` user (set via `ADMIN_USERS` in `docker-compose.yml`), not `postgres`; or just count `pg_stat_activity` as this runbook does.
- **An app logs `Disconnected: connection closed by peer` and `1/1 brokers are down` repeatedly, and orders never confirm or no rider is assigned:** that service is connecting to Kafka without credentials. Check `Kafka:SaslUsername` and `Kafka:SaslPassword` in its `appsettings.Development.json` (Payment, Delivery and the monolith each have their own).
- **A Kafka command run through `docker exec` hangs and prints nothing:** add the credentials flag (`--command-config /etc/kafka/docker/client.properties`, or `--producer.config` / `--consumer.config` for the console tools).

➡️ Next (Day 12): extract **Restaurant** → the canonical **4 services + gateway**; **zero-downtime migrations (Expand & Contract)**; and **deploy**: Terraform/ECS plus a cloud **ALB / API Gateway** as a black-box (results, not HCL).
