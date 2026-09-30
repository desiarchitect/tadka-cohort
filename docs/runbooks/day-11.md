# Day 11: Runbook: Extract Delivery (3rd service) + the API Gateway

**Branch:** `day-11`  ·  **What changed since Day 10:** [`docs/changelog.md`](../changelog.md). **What's new:** the 3rd service, **`Tadka.Delivery.Api`** (own DB `delivery-db` 5435; **Redis-geo** live location; Kafka-driven assignment, ADR-033/034), and a **YARP API gateway** (`Tadka.Gateway`, :8080, single entry + edge rate-limit, ADR-035). The order→payment→**delivery** flow is now a **3-participant Saga**, and it just earned a real 4th failure mode: a restaurant can **reject** an already-paid order, which needs a **compensating refund** (ADR-045), Day 9's choreography pattern reused. **3 services + gateway** (monolith :5224, payment :5240, delivery :5250, YARP :8080). Restaurant is still in the monolith; the 4th service is Day 12. Also: **PgBouncer** (`:6432`, ADR-015 landed), the Day-5 pool-exhaustion promise, paid off now that 2+ app instances actually exist.

> New here? Read [`README.md`](README.md). Demo password `Password123!`. Deep Saga treatment: `cohort-prep/day-11/saga-deep-dive.md` (instructor pack, not in this repo).

Every command below is given twice, bash first and PowerShell second. They are not the same commands with `curl` swapped for `curl.exe`: bash's `VAR=$(...)`, `sed -E`, and inline `VAR=value command` syntax do not run in plain PowerShell at all. Windows PowerShell 5.1 also cannot read an HTTP status code off a 401/403 response without the call throwing, so one small helper is defined once in section 1 and reused. Every PowerShell block here was run live against this branch before being written down.

---

## 1. Run it (infra + 3 services + gateway)

Bring up infra first. Delivery needs its own Postgres (`delivery-db`, 5435), and PgBouncer (`6432`) now sits next to it, both new since Day 10. To start from a clean slate (wipes every volume, so the seed data and rider states are fresh):

```bash
docker compose down -v
```
```powershell
docker compose down -v
```

Then:
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

```bash
curl -s -o /dev/null -X PUT http://localhost:5250/api/v1/deliveries/$ORDER/location -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"latitude":12.95,"longitude":77.64}'
TRACK=$(curl -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"); echo "$TRACK"   # location: {latitude:12.95, longitude:77.64}
AGENT=$(echo "$TRACK" | sed -E 's/.*"agentId":"([^"]+)".*/\1/')
docker exec tadka-redis redis-cli GEOPOS delivery:agents $AGENT   # raw geo; overwrite-latest, sub-ms
```
```powershell
Invoke-RestMethod -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/location" -Method Put -Headers $H -ContentType "application/json" -Body '{"latitude":12.95,"longitude":77.64}'
$track = Invoke-RestMethod -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/track" -Headers $H
$track
docker exec tadka-redis redis-cli GEOPOS delivery:agents $track.agentId
```
**Captured live:** `track` returned the exact coordinates just PUT (`12.95…, 77.64…`); `GEOPOS` on the same agent id returned the same pair straight out of Redis. Redis stores geo coordinates in a 52-bit geohash, so you get `12.950000663…` back, not exactly `12.95`; that is normal, and the error is well under a metre.

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
Then, in another terminal (your `$TOKEN` normally survives the restart on this branch; if you get a 401, log in again as in section 2):
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
for i in $(seq 1 45); do S=$(curl -s http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/\1/'); P=$(curl -s http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/\1/'); [ "$S" = "Cancelled" ] && [ "$P" = "Refunded" ] && break; sleep 2; done
echo "order: $S  payment: $P"
curl -s http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $TOKEN"   # {"status":"Refunded","gatewayReference":"FAKEREF-..."}
```
```powershell
$ORDER = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY).id
$sw = [Diagnostics.Stopwatch]::StartNew()
do { Start-Sleep -Seconds 2; $s = (Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders/$ORDER" -Headers $H).status; $p = (Invoke-RestMethod -Uri "http://localhost:5240/payments/$ORDER" -Headers $H).status } until (($s -eq "Cancelled" -and $p -eq "Refunded") -or $sw.Elapsed.TotalSeconds -gt 90)
"order: $s  payment: $p"
Invoke-RestMethod -Uri "http://localhost:5240/payments/$ORDER" -Headers $H
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
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
for i in $(seq 1 45); do S=$(curl -s http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/\1/'); [ "$S" = "Cancelled" ] && break; sleep 2; done
sleep 5; echo "order: $S"
curl -s http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $TOKEN"   # {"status":"Completed", ...}: STILL Completed
```
```powershell
$ORDER = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY).id
$sw = [Diagnostics.Stopwatch]::StartNew()
do { Start-Sleep -Seconds 2; $s = (Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders/$ORDER" -Headers $H).status } until ($s -eq "Cancelled" -or $sw.Elapsed.TotalSeconds -gt 90)
Start-Sleep -Seconds 5
"order: $s"
Invoke-RestMethod -Uri "http://localhost:5240/payments/$ORDER" -Headers $H   # status: Completed, STILL
```
**Captured live:** order `Cancelled` after 36 seconds on a freshly restarted monolith, payment stayed **`Completed`**: money genuinely stuck, until someone flips the lever back and reconciles by hand. The monolith's log says so out loud: `Order {id} cancelled after restaurant rejection, but Restaurant:RefundOnReject is OFF, the completed payment is NOT refunded.` This is a real gap shown on purpose, not hidden behind a passing test.

Restart the monolith with no env overrides (`AcceptMode` defaults to `Auto`) before continuing. Every section after this one assumes orders confirm normally.

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
curl -s -o /dev/null -w "POST /orders: %{http_code}\n" -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY"   # 201
curl -s -o /dev/null -w "menu: %{http_code}\n" http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu   # 200
```
```powershell
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

Start two instances, both **direct** to Postgres on `:5432`:
```bash
ASPNETCORE_URLS=http://localhost:5226 ConnectionStrings__TadkaDb="Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=5;Maximum Pool Size=60" dotnet run --project src/Tadka.Api --no-launch-profile &
ASPNETCORE_URLS=http://localhost:5227 ConnectionStrings__TadkaDb="Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=5;Maximum Pool Size=60" dotnet run --project src/Tadka.Api --no-launch-profile &
```
```powershell
$env:ConnectionStrings__TadkaDb = "Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=5;Maximum Pool Size=60"
$env:ASPNETCORE_URLS = "http://localhost:5226"
Start-Process dotnet -ArgumentList "run","--project","src/Tadka.Api","--no-launch-profile"
$env:ASPNETCORE_URLS = "http://localhost:5227"
Start-Process dotnet -ArgumentList "run","--project","src/Tadka.Api","--no-launch-profile"
$env:ConnectionStrings__TadkaDb = $null; $env:ASPNETCORE_URLS = $null
```
> **`--no-launch-profile` matters here.** Without it, `dotnet run` applies `launchSettings.json`'s own `applicationUrl` (`:5224`) *after* your `ASPNETCORE_URLS`, so both instances silently try to bind the same port and crash.

Fire 150 concurrent requests at each instance, then look at how many real connections the database is holding:
```bash
powershell.exe -NoProfile -Command "& './docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1' -Urls @('http://localhost:5226','http://localhost:5227') -Label 'DIRECT :5432' -RequestsPerInstance 150"
docker exec tadka-postgres psql -U tadka -d tadka -t -A -c "SELECT count(*) FROM pg_stat_activity WHERE usename='tadka';"
```
```powershell
.\docs\demo-scripts\02-pgbouncer-connection-exhaustion.ps1 -Urls @("http://localhost:5226","http://localhost:5227") -Label "DIRECT :5432" -RequestsPerInstance 150
docker exec tadka-postgres psql -U tadka -d tadka -t -A -c "SELECT count(*) FROM pg_stat_activity WHERE usename='tadka';"
```
> The script is PowerShell either way (`pwsh` is not installed on a stock Windows machine, so the bash line calls `powershell.exe`). Pass `-Urls` as an array; a single comma-joined string binds as one bad URI.

Now stop both instances and restart them pointed at PgBouncer `:6432`. In transaction-pooling mode a physical connection can be handed to a different client between statements, so .NET's own client-side pooling must be turned off (`Pooling=false`), otherwise Npgsql may reuse session state PgBouncer has already wiped (see `docs/database/connection-pooling-guide.md`):
```bash
ASPNETCORE_URLS=http://localhost:5226 ConnectionStrings__TadkaDb="Host=localhost;Port=6432;Database=tadka;Username=tadka;Password=tadka_local;Pooling=false" dotnet run --project src/Tadka.Api --no-launch-profile &
ASPNETCORE_URLS=http://localhost:5227 ConnectionStrings__TadkaDb="Host=localhost;Port=6432;Database=tadka;Username=tadka;Password=tadka_local;Pooling=false" dotnet run --project src/Tadka.Api --no-launch-profile &
powershell.exe -NoProfile -Command "& './docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1' -Urls @('http://localhost:5226','http://localhost:5227') -Label 'VIA PGBOUNCER :6432' -RequestsPerInstance 150"
docker exec tadka-postgres psql -U tadka -d tadka -t -A -c "SELECT count(*) FROM pg_stat_activity WHERE usename='tadka';"
```
```powershell
$env:ConnectionStrings__TadkaDb = "Host=localhost;Port=6432;Database=tadka;Username=tadka;Password=tadka_local;Pooling=false"
$env:ASPNETCORE_URLS = "http://localhost:5226"
Start-Process dotnet -ArgumentList "run","--project","src/Tadka.Api","--no-launch-profile"
$env:ASPNETCORE_URLS = "http://localhost:5227"
Start-Process dotnet -ArgumentList "run","--project","src/Tadka.Api","--no-launch-profile"
$env:ConnectionStrings__TadkaDb = $null; $env:ASPNETCORE_URLS = $null
.\docs\demo-scripts\02-pgbouncer-connection-exhaustion.ps1 -Urls @("http://localhost:5226","http://localhost:5227") -Label "VIA PGBOUNCER :6432" -RequestsPerInstance 150
docker exec tadka-postgres psql -U tadka -d tadka -t -A -c "SELECT count(*) FROM pg_stat_activity WHERE usename='tadka';"
```
**Captured live, and read this before you promise a failure on stage.** Whether the *direct* run shows failed requests is **not deterministic**. In earlier runs 3-4 of 300 requests failed with Npgsql's `"sorry, too many clients already"` (surfaced as HTTP 500); on a later, warmer run the same load returned 300/300 and even 600/600 with zero failures. What *was* deterministic, every time: after the direct bursts the two instances held **100 of 100** allowed connections (`pg_stat_activity` = 100 against `max_connections` = 100), so the server was sitting at its ceiling and any further connection attempt would be refused. Via PgBouncer the same load returned **300/300** with only **13** real connections open. So the lesson to show is the connection count (100 of 100 vs 13), with request failures as a bonus when they happen. If you want more pressure, raise `-RequestsPerInstance` or run the direct case right after starting the instances.

`Pooling=false` is what a real service ships with once it is behind PgBouncer, per `connection-pooling-guide.md`. The captured comparison used the same `Maximum Pool Size=60` string on both legs for an apples-to-apples run; mention the difference if a student asks why the two commands differ. Full numbers: ADR-015.

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
dotnet test    # 47/47: monolith 35 + Payment 6 + Delivery 6
```
```powershell
dotnet test
```
> If you last checked this number before `4a5988e`/`d2bb1c5` landed you may remember `40/40`; those two commits added `LocationTrackingTests.cs` (3) and `OrderTrackingAuthorizationTests.cs` (4) to the suites. `dotnet test` is the source of truth, not a number in a doc. The integration tests use Testcontainers, so Docker must be running.

---

## 8. Wiring Reference & Cross-Stack Architecture

### Where the code lives in Tadka
- **Delivery extraction (ADR-033):** [`src/Tadka.Delivery.Api/DeliveryService.cs`](../../src/Tadka.Delivery.Api/DeliveryService.cs) (assignment), [`Messaging/OrderConfirmedConsumer.cs`](../../src/Tadka.Delivery.Api/Messaging/OrderConfirmedConsumer.cs), [`Data/DeliveryDbContext.cs`](../../src/Tadka.Delivery.Api/Data/DeliveryDbContext.cs) (own Postgres, own migration history, the ADR-026 pattern reused).
- **Redis-geo (ADR-034):** [`LocationStore.cs`](../../src/Tadka.Delivery.Api/LocationStore.cs) (`GEOADD`/`GEOPOS`), [`OrderTrackingPublisher.cs`](../../src/Tadka.Delivery.Api/OrderTrackingPublisher.cs) (re-publishes location pings onto the Day-6 SSE backplane, ADR-020/036).
- **API gateway (ADR-035):** [`src/Tadka.Gateway/Program.cs`](../../src/Tadka.Gateway/Program.cs), YARP routes plus a fixed-window per-IP limiter.
- **Refund saga (ADR-045):** [`RestaurantAcceptanceOptions.cs`](../../src/Tadka.Api/Domain/Restaurants/RestaurantAcceptanceOptions.cs), [`RefundSagaOrchestrator.cs`](../../src/Tadka.Api/Infrastructure/Messaging/RefundSagaOrchestrator.cs), [`PaymentRefundedConsumer.cs`](../../src/Tadka.Api/Infrastructure/Messaging/PaymentRefundedConsumer.cs) (monolith); [`RefundRequestedConsumer.cs`](../../src/Tadka.Payment.Api/Messaging/RefundRequestedConsumer.cs), `PaymentService.RefundAsync` (Payment).
- **SSE ownership fix (ADR-031):** [`OrderTrackingController.cs`](../../src/Tadka.Api/Controllers/OrderTrackingController.cs), `GetEvents`.
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

- **Rider assignment is first-available, not nearest.** ADR-034 names `GEOSEARCH` for "nearby agents", but `DeliveryService.cs` has no `ORDER BY` distance and never calls `GEOSEARCH`: whichever `Available` rider the query returns first gets the order, even if a closer one exists. Proximity-based dispatch is the natural next step, not yet built. Real dispatch uses `GEOSEARCH` or geohash, and at extreme scale H3/S2 cells.
- **A single gateway instance is a new SPOF.** Section 5 showed the 502 when a target is down; the gateway itself needs 2+ instances behind its own load balancer in production.
- **The refund saga is choreographed and in-process on Day 11, both temporary by design.** ADR-045 names its own revisit triggers: once Restaurant is extracted (Day 12) the `AcceptMode` decision belongs in `Restaurant.Api` reacting to `order-confirmed`; and a refund that fails at the gateway needs its own failure path and a reconciliation job, not yet modelled.
- **PgBouncer transaction mode has real limits.** No reliable session state across statements, so anything using advisory locks across calls, `LISTEN/NOTIFY` or long-lived prepared statements needs session mode or a direct connection.

## ✅ Done when
- [ ] Order paid → Confirmed → `track` shows an assigned rider (3-service saga).
- [ ] PUT location → `track` returns it (Redis-geo); `GEOPOS` shows the raw entry.
- [ ] Restaurant reject + refund: `AcceptMode=Reject, RefundOnReject=true` → order Cancelled, payment **Refunded** with a new gateway reference.
- [ ] The break lever: `RefundOnReject=false` → order Cancelled, payment stays **Completed**, money stuck, and the log says so.
- [ ] Delivery **down** → orders 201 + menu 200 (fault isolation).
- [ ] Live-tracking SSE stream: the order's owner gets events; a different customer's token on the same order id gets **403**.
- [ ] All services reachable via **one host** `:8080`; payment-no-token via gateway still **401**; a dead route's target returns **502**.
- [ ] PgBouncer: direct-to-Postgres leaves the instances holding **100 of 100** connections; via `:6432` the same load runs on about **13**.
- [ ] `dotnet test` → **47/47**.

## Troubleshooting
- **First order stays `Created` for a long time after a restart:** cold JIT plus Kafka consumers joining their group. Wait up to a minute; later orders confirm in about a second.
- **No rider assigned:** is the Delivery service up and `tadka-kafka` healthy? Check its log for `OrderConfirmedConsumer subscribed` and `🛵 Order … assigned to rider`. The monolith publishes `order-confirmed` on auto-confirm after payment.
- **`track` location is null:** PUT a location first; Redis must be up (`Redis` in the Delivery service's `appsettings.Development.json`).
- **Refund never happens / payment stays `Completed` with `RefundOnReject=true`:** it is a 2-hop Kafka round trip (`refund-requested`, then `payment-refunded`). If still stuck past a minute, check the monolith log for `PaymentRefundedConsumer subscribed to payment-refunded` and Payment's for `RefundRequestedConsumer subscribed`.
- **SSE stream returns 403 for the order's real owner:** wrong token. Re-login and confirm the `sub` claim matches the order's `customerId`.
- **Gateway 502 on a route:** the target service is down; start all three before the gateway demo.
- **Both `Tadka.Api` instances crash on startup in the PgBouncer demo:** you forgot `--no-launch-profile`; `launchSettings.json`'s `applicationUrl` overrides `ASPNETCORE_URLS` and both fight over `:5224`.
- **The demo script says every request failed with "Invalid URI":** `-Urls` was passed as one comma-joined string. Pass a real array (`@("http://…","http://…")`).
- **`pwsh: command not found`:** `pwsh` (PowerShell 7) is not installed by default. The demo script runs fine under Windows PowerShell 5.1: use `powershell.exe` as shown above.
- **PgBouncer `SHOW POOLS` fails with "not allowed":** connect as the `tadka` user (set via `ADMIN_USERS` in `docker-compose.yml`), not `postgres`; or just count `pg_stat_activity` as this runbook does.

➡️ Next (Day 12): extract **Restaurant** → the canonical **4 services + gateway**; **zero-downtime migrations (Expand & Contract)**; and **deploy**: Terraform/ECS plus a cloud **ALB / API Gateway** as a black-box (results, not HCL).
