# Day 12: Runbook: Extract Restaurant (4th service) + zero-downtime migration

**Branch:** `day-12`  ·  **What changed since Day 11:** [`docs/changelog.md`](../changelog.md).

Tadka reaches the canonical **4 services + a gateway**. Restaurant is the *last* and *hardest* extraction because order pricing reads its menu on the critical path, so it earns a **local read model** (event-carried state transfer, ADR-037) and a **zero-downtime data backfill** (ADR-038). Deploy is a black box (`deploy/README.md`, ADR-039/064). This branch is intentionally kept fast-forwarded to `main` (`docs/runbooks/DAY-EVOLUTION.md`), so it also carries the live-tracking SSE hardening (ownership check plus a per-user stream cap) that landed after this branch's own Day-12 work. Both get their own demo below, since neither exists on Day 11.

> Demo password `Password123!`.

Every command below is given twice, bash first and PowerShell second, wherever the two shells differ. They are not the same commands with `curl` swapped for `curl.exe`: bash's `VAR=$(...)`, `sed -E`, and inline `VAR=value command` syntax do not run in plain PowerShell at all. Windows PowerShell 5.1 also cannot read an HTTP status code off a 4xx/5xx response without the call throwing, so one small helper is defined once in section 1 and reused. Every PowerShell block here was run live against this branch before being written down.

---

## 1. Start the stack

### Demo Day Fresh Reset (Clean Slate)
To start from a clean slate (wiping every volume, lingering test containers, or Keycloak profile instances). **Do this if you have run this branch before**: a previous Demo 2 PATCH persists in the `restaurant-db` volume and would give you a ₹349 baseline instead of the documented ₹299:

**Bash:**
```bash
docker compose --profile auth-prod down -v --remove-orphans && docker compose up -d
until docker inspect tadka-kafka --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
until docker inspect tadka-restaurant-db --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
docker compose ps
```

**PowerShell:**
```powershell
docker compose --profile auth-prod down -v --remove-orphans; docker compose up -d
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-kafka --format "{{.State.Health.Status}}") -eq "healthy")
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-restaurant-db --format "{{.State.Health.Status}}") -eq "healthy")
docker compose ps
```

*(This wipes volume directories `pgdata`, `pgdata_replica`, `pgdata_payment`, `pgdata_delivery`, `pgdata_restaurant`, and brings up the 9 containers fresh from scratch).*

---

### Standard Launch
If starting existing containers without wiping data:

```bash
git checkout day-12
docker compose up -d                          # + restaurant-db (5436)
until docker inspect tadka-kafka --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
until docker inspect tadka-restaurant-db --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
docker compose ps
```
```powershell
git checkout day-12
docker compose up -d
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-kafka --format "{{.State.Health.Status}}") -eq "healthy")
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-restaurant-db --format "{{.State.Health.Status}}") -eq "healthy")
docker compose ps
```

**Build once before starting the apps.** All the services share the `Tadka.Telemetry` project, and if you start several `dotnet run` at once on a fresh checkout they race to build it. Two of the five failed with `Cannot open ...Tadka.Telemetry.dll for writing` in the run that produced this doc. One build first avoids it (identical in either shell):
```
dotnet build Tadka.slnx
```

**Kafka requires a login on this branch.** The local broker only accepts clients that authenticate with SASL/SCRAM-SHA-256. All four services and Kafka UI already carry the demo credentials (`appsettings.Development.json`, `docker-compose.yml`). The Kafka command-line tools you run through `docker exec` are clients too: pass `--command-config /etc/kafka/docker/client.properties` to `kafka-topics.sh` and `kafka-consumer-groups.sh`, `--producer.config` to the console producer and `--consumer.config` to the console consumer (the helper scripts under `scripts/` already do). Without credentials a Kafka command hangs and prints nothing. Details are in the Day 9 runbook and ADR-027's security addendum. **The Azure/cloud Kafka is not covered** (see section 13).

**Pre-create the eight Kafka topics this branch uses** (once per fresh broker, right after the containers are healthy). On a broker with no topics yet, each service subscribes the moment it starts, and a consumer that starts before anyone has published to its topic logs `Confluent.Kafka.ConsumeException: Subscribed topic not available` once a second. It is harmless (the consumer keeps retrying and picks the topic up once it exists), but it looks alarming on a first run. Auto-create is on, so skipping this step breaks nothing. The `*.dlq` topics are not in the list on purpose: nothing subscribes to them, and they appear on their own the first time a poison message is parked.
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

Five processes, five terminals (identical in either shell):
```
dotnet run --project src/Tadka.Payment.Api    # :5240
dotnet run --project src/Tadka.Delivery.Api   # :5250
dotnet run --project src/Tadka.Restaurant.Api # :5260  (own DB, seeds, publishes menu-updated)
dotnet run --project src/Tadka.Api            # :5224  (applies the Day-12 migration on startup)
dotnet run --project src/Tadka.Gateway        # :8080
```
On monolith startup the Day-12 migration **drops** `restaurant.{restaurants,menu_items}` and **creates and seeds** `ordering.{restaurant_replica,menu_replica}`. You may see `Subscribed topic not available` logged by a consumer that started before its topic existed. It is harmless: the consumer keeps retrying and picks the topic up once a producer creates it.

Lever: `Ordering:RestaurantReadMode` = `LocalReplica` (default) | `SyncHttp`.

### PowerShell helper: reading a status code without the call throwing
Define this once, in the same terminal you'll run every PowerShell block below in:
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

> **Timing on a cold start.** After a service (re)starts, the first request can be slow (JIT) and Kafka consumers need a few seconds to join their group. Every wait below is a poll with a timeout, not a fixed `sleep`.

Shared setup used by the demos below:
```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
BODY='{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":2}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}'
```
```powershell
$TOKEN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$H = @{ Authorization = "Bearer $TOKEN" }
$BODY = '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":2}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}'
```

---

## 2. Demo 1: Restaurant down, orders still flow

**What you're proving:** the same wound Day 8 healed for Payment (a synchronous cross-service call on the order's hot path) exists again here in a worse spot, pricing and not just charging, and the same fix (a local read model) heals it.

**Baseline** (Restaurant up): place 2 x Chicken Biryani at Meghana. The price comes from the replica.
```bash
curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"totalAmount":\{"amount":([0-9.]+).*/total: \1/'
```
```powershell
"total: " + (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY).totalAmount.amount
```
**Captured live: `total: 598.00`.**

**Log in again after every monolith restart.** The monolith keeps its JWT signing keys in memory, so a restart makes every earlier token return `401`. A `401` here would look like the wound but prove nothing, so each block below logs in first.

**The wound.** Stop the Restaurant service (Ctrl+C), restart the monolith with the lever forcing a synchronous HTTP price read, then place an order:
```bash
Ordering__RestaurantReadMode=SyncHttp dotnet run --project src/Tadka.Api
# then, in another terminal:
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "POST /orders (SyncHttp, Restaurant down): %{http_code}\n" -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY"
```
```powershell
$env:Ordering__RestaurantReadMode = "SyncHttp"
dotnet run --project src/Tadka.Api
# after you stop it (Ctrl+C), clear the lever: $env:Ordering__RestaurantReadMode = $null
# then, in another terminal (any window):
# Self-contained: defines the helper and $BODY only if this window does not have them yet.
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
if (-not $BODY) { $BODY = '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":2}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}' }
$TOKEN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$H = @{ Authorization = "Bearer $TOKEN" }
"POST /orders (SyncHttp, Restaurant down): " + (Get-StatusCode -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -Body $BODY)
```
**Captured live: `500 Internal Server Error`.** With the lever forcing a synchronous HTTP call to price the order, a dead Restaurant service takes the order path down with it: the exact Day-8 shape, now on pricing.

**The fix.** Restaurant is *still stopped*. Restart the monolith with no override (the default is `LocalReplica`) and place the same order:
```bash
dotnet run --project src/Tadka.Api
# then:
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"status":"([^"]+)".*"totalAmount":\{"amount":([0-9.]+).*/status: \1  total: \2/'
```
```powershell
dotnet run --project src/Tadka.Api
# then (log in again, the monolith restarted):
$TOKEN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$H = @{ Authorization = "Bearer $TOKEN" }
$o = Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY
"status: " + $o.status + "  total: " + $o.totalAmount.amount
```
**Captured live: `201`, status `Created`, `₹598`.** Pricing came from `ordering.menu_replica`, not a live call. Restaurant being down only blocks *menu edits*. Ordering, payment and browsing are untouched. Restart Restaurant before Demo 2.

### How this is actually implemented
The switch is one line in [`Program.cs`](../../src/Tadka.Api/Program.cs), which picks the pricing source from config:
```csharp
var readMode = builder.Configuration["Ordering:RestaurantReadMode"] ?? "LocalReplica";
if (string.Equals(readMode, "SyncHttp", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHttpClient<IRestaurantPricingSource, HttpRestaurantPricingSource>(c => { ... c.Timeout = TimeSpan.FromSeconds(2); });
else
    builder.Services.AddScoped<IRestaurantPricingSource, LocalReplicaPricingSource>();
```
Both implement `IRestaurantPricingSource`. [`LocalReplicaPricingSource`](../../src/Tadka.Api/Infrastructure/RestaurantReadModel/LocalReplicaPricingSource.cs) reads the monolith's own `restaurant_replica` and `menu_replica` tables through EF; [`HttpRestaurantPricingSource`](../../src/Tadka.Api/Infrastructure/RestaurantReadModel/HttpRestaurantPricingSource.cs) calls Restaurant over HTTP with a 2 second timeout. A fast timeout still fails the order when the peer is down, which is the point. The `SyncHttp` lever exists **for the demo only**; the default is the replica.

### Option space: pricing an order when the menu lives in another service
| Option | Order path when Restaurant is down | Freshness | Use when |
|---|---|---|---|
| Sync HTTP + circuit breaker | Fails (a breaker only fails faster) | Always fresh | The price must never be stale |
| Shared Redis cache | Better, but shared ownership | TTL | Weak service boundaries are acceptable |
| **Local read model / event-carried state transfer** (what Tadka does) | **Unaffected**, the replica is local | Eventual (seconds) | The default for a hot-path read |
| Gateway composition | Coupling moves to the edge | Fresh | Read-only joins, not pricing |
| Trust the client's price | n/a | n/a | **Never** (discount exploit) |

**The trade-off, stated honestly:** availability over freshness. There is a stale-price window of a few seconds, and the line price is **snapshotted at order capture** (ADR-009), so a later menu event does not reprice an order already placed. An interview follow-up worth having ready: *who absorbs the price difference?* Tadka's answer is that the order snapshot wins and the customer pays what they were shown; the restaurant absorbs the gap. That is a business decision, not an engineering one, and it should be written down somewhere (a platform-fee agreement), not decided silently in code.

---

## 3. Demo 2: A price change propagates (event-carried state transfer)

**What you're proving:** the replica is not a one-time snapshot. It is kept live by Restaurant's own `menu-updated` events, the same Outbox→Kafka pattern Day 9 built for Ordering.

Log in as admin and change the price 299 to 349 on the **Restaurant** service:
```bash
ADMIN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"admin@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "PATCH: %{http_code}\n" -X PATCH http://localhost:5260/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu/b1b2c3d4-0001-4000-8000-000000000001 \
  -H "Authorization: Bearer $ADMIN" -H "Content-Type: application/json" \
  -d '{"price":{"amount":349,"currency":"INR"}}'        # -> 204
```
```powershell
$ADMIN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"admin@tadka.test","password":"Password123!"}').accessToken
$AH = @{ Authorization = "Bearer $ADMIN" }
"PATCH: " + (Get-StatusCode -Uri "http://localhost:5260/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu/b1b2c3d4-0001-4000-8000-000000000001" -Method Patch -Headers $AH -Body '{"price":{"amount":349,"currency":"INR"}}')   # 204
```
> An earlier version of this runbook showed this curl with literal `{meghana}` and `{biryani}` placeholders, which cannot be pasted and run. Meghana is `a1b2c3d4-0001-4000-8000-000000000001`; Chicken Biryani is `b1b2c3d4-0001-4000-8000-000000000001`.

Then watch it reach the monolith's replica (`menu-updated` → Outbox → Kafka → `MenuUpdatedConsumer` upserts):
```bash
docker exec tadka-postgres psql -U tadka -d tadka -c 'SELECT "Name","PriceAmount" FROM ordering.menu_replica WHERE "MenuItemId"='"'"'b1b2c3d4-0001-4000-8000-000000000001'"'"';'
curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"totalAmount":\{"amount":([0-9.]+).*/new total: \1/'
```
```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \`"Name\`",\`"PriceAmount\`" FROM ordering.menu_replica WHERE \`"MenuItemId\`"='b1b2c3d4-0001-4000-8000-000000000001';"
"new total: " + (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY).totalAmount.amount
```
> In PowerShell the double quotes around `"Name"` need a backtick-escape (`` \`" ``: a backslash for the native `psql` argument, a backtick so PowerShell keeps the quote). A bare `\"` gets stripped before it reaches `psql`.

**Captured live:** the PATCH returned `204`; the replica showed `Chicken Biryani | 349.00` within about a second on a warm stack (up to a few seconds cold); a fresh 2x order totalled **`₹698`**. The event carried the full new price, so the monolith never called back to Restaurant to fetch it.

### How this is actually implemented
Restaurant writes the menu row and a full-snapshot `menu-updated` message to its Outbox in the **same transaction**, and the relay publishes it to Kafka. [`MenuUpdatedConsumer`](../../src/Tadka.Api/Infrastructure/Messaging/MenuUpdatedConsumer.cs) upserts the restaurant and menu rows in `ordering`. It is **idempotent by design (last write wins), so it needs no Inbox**: a redelivery just re-applies the same state. The message carries the whole snapshot, not a diff, precisely so the monolith never has to call back (ADR-008).

### Option space: keeping a copy of another service's data
| Approach | Consistency | Cost |
|---|---|---|
| **Full-snapshot events** (what Tadka does) | Eventual, self-healing (last write wins) | Larger messages |
| Delta events (`price-changed`) | Eventual, but a lost or reordered event corrupts the copy | Small messages, needs ordering and dedupe |
| Change data capture (Debezium) | Eventual, no app code | A whole pipeline to run; the right answer at scale |
| Query the owner on demand | Always fresh | Brings back the temporal coupling from Demo 1 |

Cross-stack: the consumer is a listener plus an upsert in any stack (Spring Kafka + JPA, kafkajs + Prisma, kafka-go + sqlc).

---

## 4. Demo 3: Online data backfill (zero-downtime)

**What you're proving:** events only carry *future* changes, so the replica starts empty and the *current* menu has to be backfilled. In production that table is huge and the system is live, so you cannot lock it or saturate the replica. This demo shows the discipline: chunked, partitioned across workers, throttled, lag-watched, idempotent, resumable.

```bash
powershell.exe -NoProfile -File ./scripts/backfill-menu-replica.ps1 -SeedExtra 5000 -ChunkSize 1000 -ThrottleMs 50
```
```powershell
.\scripts\backfill-menu-replica.ps1 -SeedExtra 5000 -ChunkSize 1000 -ThrottleMs 50
```
(The script is PowerShell either way, and it drives everything through `docker exec psql`, so no host `psql` is needed.)

**Captured live, twice:** `5016 row(s)` backfilled in **`6 batches over 7s`**, replica lag staying at **`1-10 ms`** throughout, the table never locked. (5016 = the 16 real seeded items plus 5000 synthetic ones.) Strategy in class, not script: the LLD lives in the repo, not on the slide.

### How this is actually implemented
[`scripts/backfill-menu-replica.ps1`](../../scripts/backfill-menu-replica.ps1) reads a keyset-paged chunk of the source (`WHERE Id > lastId ORDER BY Id LIMIT N`, a plain read with no row lock), upserts it with `INSERT ... ON CONFLICT ("MenuItemId") DO UPDATE`, sleeps `-ThrottleMs`, reads replication lag from `pg_stat_replication`, and backs off if it exceeds `-LagCeilingMs`. Each batch is its own transaction, so there is never a giant lock, and every batch prints its **high-water mark**: after a crash, pass the last one as `-StartAfterId` and it carries on from there. To run several workers, start the script once per worker (`-Workers 2 -Worker 0` and `-Workers 2 -Worker 1`): worker *i* only takes rows where `abs(hashtext("Id"::text)::bigint) % Workers = i`, so the slices are disjoint and complete. Because this is a copy between two databases, no row lock can be held across the read and the upsert; the **partition** is what keeps workers from colliding.

### Option space: moving data on a live table
| Technique | What it buys | Watch |
|---|---|---|
| Chunked keyset paging | No giant transaction | Page on the primary key; track a high-water mark |
| Hash partition per worker (`-Workers N -Worker i`) | N workers take disjoint slices with no coordination | Every worker must be started with the same N |
| `FOR UPDATE SKIP LOCKED` | When claim and update happen in one statement on one database, workers take disjoint batches without blocking each other (the `Name` to `DisplayName` backfill in Demo 5, the Outbox relay) | Needs the claim and the write in the same transaction, which a cross-database copy cannot have |
| Throttle + lag watch | Protects the read replica (ADR-016) | If lag grows, back off |
| Idempotent upsert | Resumable: a re-run never duplicates | Needs a natural id |
| **CDC (Debezium)** | A continuous stream instead of a one-off copy | A separate pipeline; the answer at scale |

**Why not one big `UPDATE`:** on a 5 crore row table, an unthrottled statement holds locks and pushes replica lag from milliseconds to tens of seconds, and a replica that lags serves stale reads (an order "not found" right after it was placed). The DDL is easy; the data is hard.

---

## 5. Demo 4: Restaurant service accept/reject (ADR-062)

**What you're proving:** Day 11's refund saga (ADR-045) put the accept/reject *decision* inside Ordering, because Restaurant did not exist as its own process yet. Now it does. The same refund machinery runs, but the decision genuinely lives in a different service and reaches Ordering over Kafka. See **[decision-mode-matrix.md](decision-mode-matrix.md)**: Day 11 used **Inline**, Day 12 earns **Service**.

Restart Restaurant with `AcceptMode=Reject` and the monolith with `DecisionMode=Service` (Ctrl+C each first):
```bash
Restaurant__AcceptMode=Reject dotnet run --project src/Tadka.Restaurant.Api
Restaurant__DecisionMode=Service dotnet run --project src/Tadka.Api
```
```powershell
$env:Restaurant__AcceptMode = "Reject"; dotnet run --project src/Tadka.Restaurant.Api
# after you stop it: $env:Restaurant__AcceptMode = $null
$env:Restaurant__DecisionMode = "Service"; dotnet run --project src/Tadka.Api
# after you stop it: $env:Restaurant__DecisionMode = $null
```
Ordering always confirms; Restaurant.Api decides on `order-confirmed`. Both restarts invalidated your tokens, so log in again, then place an order and poll until it settles:
```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
for i in $(seq 1 60); do S=$(curl -s http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/\1/'); P=$(curl -s http://localhost:5240/api/v1/payments/$ORDER -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/\1/'); [ "$S" = "Cancelled" ] && [ "$P" = "Refunded" ] && break; sleep 2; done
echo "order: $S  payment: $P"
curl -s http://localhost:5240/api/v1/payments/$ORDER -H "Authorization: Bearer $TOKEN"
```
```powershell
$TOKEN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$H = @{ Authorization = "Bearer $TOKEN" }
$ORDER = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY).id
$sw = [Diagnostics.Stopwatch]::StartNew()
do { Start-Sleep -Seconds 2; $s = (Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders/$ORDER" -Headers $H).status; $p = (Invoke-RestMethod -Uri "http://localhost:5240/api/v1/payments/$ORDER" -Headers $H -ErrorAction SilentlyContinue).status } until (($s -eq "Cancelled" -and $p -eq "Refunded") -or $sw.Elapsed.TotalSeconds -gt 120)
"order: $s  payment: $p"
Invoke-RestMethod -Uri "http://localhost:5240/api/v1/payments/$ORDER" -Headers $H
```
> **Note the path: `/api/v1/payments/{orderId}`.** Payment's routes were versioned on this branch; the Day-11 `/payments/{orderId}` path returns a bare `404` here (no body, which is how you tell a routing miss from a "no such payment" `404`).

**Captured live:** order `Cancelled` and payment **`Refunded`** with a fresh gateway reference (`FAKEREF-…`), about **39 seconds** after services had just restarted. Give it real time: this is a 3-hop Kafka chain (`order-confirmed` → `restaurant-response` → `refund-requested` → `payment-refunded`), longer than Demo 1's single hop or Day 11's 2-hop refund.
```
# Stuck Confirmed? Restaurant.Api or Kafka is down: see decision-mode-matrix.md.
# With Saga__Mode=Orchestration you can also query the saga state:
#   SELECT * FROM ordering.saga_instances ORDER BY "StartedAt" DESC LIMIT 5;
```

### How this is actually implemented
In `Service` mode the monolith stops deciding: it publishes `order-confirmed`, and Restaurant.Api consumes it and replies on `restaurant-response` (`Rejected` here). The monolith's `RestaurantResponseConsumer` receives that and runs the *same* `RefundSagaOrchestrator` from Day 11 (cancel + `refund-requested` via the Outbox), so only the *trigger* moved between processes. The refund half (Payment's `RefundRequestedConsumer`, `PaymentService.RefundAsync`) is unchanged.

### Option space: where the accept/reject decision lives
| | `Inline` | `Service` (this demo) |
|---|---|---|
| Who decides | Ordering, at payment-settled | Restaurant.Api, on `order-confirmed` |
| Kafka required | No | Yes, plus Restaurant.Api running |
| If Restaurant is down | Not applicable | Orders sit `Confirmed` |
| Refund path | Same orchestrator | Same, triggered by `restaurant-response` |

There is **no automatic timeout-then-reject** in the teaching build; an order whose restaurant never answers just stays `Confirmed`. ADR-062 names that as a revisit.

---

## 6. Demo 5: Expand-contract dual-write (ADR-038 live)

**What you're proving:** renaming a column on a live table without downtime. Add the new column (expand), write to **both** during the transition (dual-write), backfill the historical rows in chunks, switch reads, and only later drop the old column (contract). Every step is its own deploy, and no app version ever runs against a schema it cannot handle.

Dual-write is **on by default** (`Demo:DualWriteDisplayName`, default `true`), because reads already prefer `DisplayName`: if a rename wrote only `Name`, the API would keep showing the old name. The `=true` below just makes it explicit. To watch what it protects you from, start Restaurant with `Demo__DualWriteDisplayName=false`, rename an item, and compare the two columns: `Name` changes, `DisplayName` stays behind (this variation is covered by `ExpandContractDualWriteTests`, not re-run live). Restart Restaurant with dual-write on and change a menu item's name:
```bash
Demo__DualWriteDisplayName=true dotnet run --project src/Tadka.Restaurant.Api
# then:
ADMIN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"admin@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "PATCH name: %{http_code}\n" -X PATCH http://localhost:5260/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu/b1b2c3d4-0001-4000-8000-000000000001 -H "Authorization: Bearer $ADMIN" -H "Content-Type: application/json" -d '{"name":"Hyderabadi Chicken Biryani"}'
docker exec tadka-restaurant-db psql -U tadka -d tadka_restaurant -c 'SELECT "Name","DisplayName" FROM restaurant.menu_items WHERE "Id"='"'"'b1b2c3d4-0001-4000-8000-000000000001'"'"';'
```
```powershell
$env:Demo__DualWriteDisplayName = "true"; dotnet run --project src/Tadka.Restaurant.Api
# after you stop it: $env:Demo__DualWriteDisplayName = $null
# then:
$ADMIN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"admin@tadka.test","password":"Password123!"}').accessToken
Invoke-RestMethod -Uri "http://localhost:5260/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu/b1b2c3d4-0001-4000-8000-000000000001" -Method Patch -Headers @{ Authorization = "Bearer $ADMIN" } -ContentType "application/json" -Body '{"name":"Hyderabadi Chicken Biryani"}'
docker exec tadka-restaurant-db psql -U tadka -d tadka_restaurant -c "SELECT \`"Name\`",\`"DisplayName\`" FROM restaurant.menu_items WHERE \`"Id\`"='b1b2c3d4-0001-4000-8000-000000000001';"
```
**Captured live:** both columns hold `Hyderabadi Chicken Biryani` after one PATCH.

Now backfill the *historical* rows (the ones written before dual-write was on, including the ~5,000 synthetic rows Demo 3 seeded straight into the table):
```bash
powershell.exe -NoProfile -File ./scripts/expand-contract-demo.ps1
```
```powershell
.\scripts\expand-contract-demo.ps1
```
**Captured live:** `4815` rows with a NULL `DisplayName` backfilled in **25 batches** (24 x 200, then 15) in about **11 seconds**, ending with `remaining NULL DisplayName: 0`.

And the contrast, one giant unthrottled `UPDATE`:
```bash
powershell.exe -NoProfile -File ./scripts/expand-contract-demo.ps1 -BreakGiantUpdate
```
```powershell
.\scripts\expand-contract-demo.ps1 -BreakGiantUpdate
```
**Captured live, and be honest about it:** on 20,000 rows the giant `UPDATE` finished in **519 ms**. That is instant on a laptop. The script's own message says it "locks and blows replica lag on a 50M-row hot table", which is the *shape* of the failure, not something this small demo reproduces. Say so if a student asks why nothing broke.

> **Two real bugs fixed in `expand-contract-demo.ps1` while verifying this runbook.** (1) It failed to *parse* under Windows PowerShell 5.1, with a cascade of confusing errors (`An expression was expected after '('`): its em dashes had been corrupted into mojibake bytes, and every embedded SQL identifier used Bash's `\"` escape, which PowerShell does not understand. (2) After the backfill was complete the loop **never terminated**: `psql` prints a command tag (`UPDATE 200`, or `UPDATE 0`) after the returned rows, the loop counted output lines, so an empty batch still counted as 1 and the loop spun forever printing `batch wrote ~1 rows`. It now counts only the rows it returned (`Where-Object { $_ -eq "1" }`).

### How this is actually implemented
Restaurant's migration added a nullable `DisplayName` column (the additive, non-breaking *expand*). With `Demo:DualWriteDisplayName` on (the default), PATCH and POST fill both `Name` and `DisplayName` in the same `SaveChanges`. The API's `MapItem` reads `DisplayName ?? Name`, which is the *switch-read*. The script backfills history in `SKIP LOCKED` chunks, with a 50 ms pause between batches. The *contract* step (stop writing `Name`, drop the column) is deliberately **not** done by the script: that is a later deploy.

### Option space: schema change on a live table
| Approach | Downtime | Risk |
|---|---|---|
| Big-bang `ALTER` + deploy | A maintenance window, which 99.9% availability does not allow | Lock the table, roll back is hard |
| **Expand and contract** (what Tadka does) | None | More deploys; every step must be backward compatible |
| Online schema-change tools (gh-ost, `pg_repack`, `pt-online-schema-change`) | None | Extra tooling; needed for very large tables |
| Blue/green database | None | Data sync and cutover complexity |

Migration tooling by stack: Flyway or Liquibase (Java), Prisma Migrate or Knex (Node), goose or Atlas (Go). All of them are expand-contract underneath.

---

## 7. Demo 6: The live-tracking SSE stream, ownership and a per-user cap

**What you're proving:** two hardening fixes landed on this branch after its own Day-12 work, because they apply everywhere the SSE endpoint exists. Day 10 taught resource ownership as a REST rule; here it is enforced on the SSE endpoint too. And because the SSE path bypasses the cloud gateway's Front Door origin lock (ADR-064), one user could otherwise hold unlimited streams, so there is a cap.

Place an order as Priya, then try to stream it as Rahul:
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
RAHUL=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"rahul@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "Rahul streams Priya's order: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER/events -H "Authorization: Bearer $RAHUL"   # 403
```
```powershell
$ORDER = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY).id
$RAHUL = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"rahul@tadka.test","password":"Password123!"}').accessToken
"Rahul streams Priya's order: " + (Get-StatusCode -Uri "http://localhost:5224/api/v1/orders/$ORDER/events" -Headers @{ Authorization = "Bearer $RAHUL" })   # 403
```
**Captured live: `403`**, before the stream ever opens.

Now open three concurrent streams as Priya (the default cap), then a fourth:
```bash
for i in 1 2 3; do curl -s -N http://localhost:5224/api/v1/orders/$ORDER/events -H "Authorization: Bearer $TOKEN" > /dev/null & done
sleep 2
curl -s -o /dev/null -w "4th concurrent stream: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER/events -H "Authorization: Bearer $TOKEN"   # 429
kill %1 %2 %3
```
```powershell
# curl.exe streams (Invoke-RestMethod would buffer), so start three as background processes:
$procs = 1..3 | ForEach-Object { Start-Process -FilePath curl.exe -ArgumentList "-s","-N","http://localhost:5224/api/v1/orders/$ORDER/events","-H","`"Authorization: Bearer $TOKEN`"" -RedirectStandardOutput "$env:TEMP\sse_$_.txt" -PassThru -WindowStyle Hidden }
Start-Sleep -Seconds 3
"4th concurrent stream: " + (Get-StatusCode -Uri "http://localhost:5224/api/v1/orders/$ORDER/events" -Headers $H)   # 429
$procs | Stop-Process -Force
```
**Captured live: `429`** on the fourth concurrent stream. After closing the first three, a new stream opened normally (still open after 3 seconds, not another `429`): the slot is released in a `finally`, so a dropped client never burns one forever.

### How this is actually implemented
Ownership is the same line as `OrdersController.GetById`, before the stream opens (`if (!User.IsAdmin() && order.CustomerId != User.UserId()) return Forbid();`). The cap is [`SseStreamLimiter`](../../src/Tadka.Api/Infrastructure/Realtime/SseStreamLimiter.cs): a `ConcurrentDictionary<Guid,int>` counting streams per user, `TryAcquire` returns a disposable lease or refuses when the count passes `maxPerUser` (default **3**), and the controller disposes the lease in a `finally` so a cancelled or crashed stream still frees its slot.
```csharp
if (!_limiter.TryAcquire(userId, out var lease)) { Response.StatusCode = 429; ... }
try { ... stream ... } finally { lease?.Dispose(); }
```

### Option space: limiting concurrent streams
| Approach | Scope | Catch |
|---|---|---|
| **In-memory counter per process** (what Tadka does) | One instance | Cheap, no new infra; with N replicas the effective cap is N x 3 |
| Shared counter in Redis | Whole fleet | An extra dependency on the hot path |
| Edge rate limit (WAF, gateway) | Requests per minute | Does not bound *concurrent long-lived* connections |
| Connection limits at the load balancer | Per IP | Users behind one NAT share a budget |

---

## 8. Demo 7: Replica staleness has a signal now (ADR-063), briefly

**What you're proving:** ADR-037's local read model trades freshness for availability, and until this fix nothing measured the trade. `tadka.replica.lag_seconds` closes that gap, riding the same OTEL pipeline Day 13 builds properly.

This is not a full dashboard demo. Day 13 has not happened yet in the taught sequence, and a real dashboard needs that day's Collector, Prometheus and Grafana stack. What you can show without it is that the calculation and the wiring are both unit-tested without Docker or a broker (identical command in either shell):
```
dotnet test tests/Tadka.Api.Tests --filter "FullyQualifiedName~ReplicaLag"
```
**Captured live: 5/5 passed.** With `OTEL_EXPORTER_OTLP_ENDPOINT` set (Day 13's existing gate) the gauge flows to Grafana like every other `tadka.*` metric; unset, it is simply never scraped.

### How this is actually implemented
[`MenuUpdatedConsumer`](../../src/Tadka.Api/Infrastructure/Messaging/MenuUpdatedConsumer.cs) records the Kafka message's own timestamp each time it *actually applies* a snapshot (`Interlocked.Exchange(ref TadkaDiagnostics.LastMenuReplicaAppliedEventUnixMs, ...)`). An `ObservableGauge` in [`Tadka.Telemetry`](../../src/Tadka.Telemetry/TadkaDiagnostics.cs) evaluates `now - baseline` at scrape time. The design choice that matters: a **gauge that keeps counting up**, not a per-message histogram, because if the consumer stalls no new messages arrive to produce a sample, so a histogram would go flat and hide exactly the failure this metric exists to catch.

### Option space: knowing the replica is stale
| Approach | Catch |
|---|---|
| **Observed gauge from the last-applied timestamp** (what Tadka does) | Cannot tell a stalled relay from a stalled consumer |
| Per-message lag histogram | Goes silent when the consumer dies |
| A `/health/replica` JSON endpoint | Does not compose with Grafana and alerting |
| Kafka consumer-group lag | Measures the topic, not what has actually been applied to the table |

No alert is wired on this metric yet: a high value is visible if someone looks, not paged on.

---

## 9. The gateway's 4th route

The gateway now also routes `/api/v1/restaurants/**` to the Restaurant service. Everything else is as on Day 11.
```bash
curl -s -o /dev/null -w "restaurants via gateway: %{http_code}\n" http://localhost:8080/api/v1/restaurants                                                 # 200 -> Restaurant
curl -s -o /dev/null -w "menu via gateway: %{http_code}\n" http://localhost:8080/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu   # 200 -> Restaurant
```
```powershell
"restaurants via gateway: " + (Get-StatusCode -Uri http://localhost:8080/api/v1/restaurants)   # 200 -> Restaurant
"menu via gateway: " + (Get-StatusCode -Uri http://localhost:8080/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu)   # 200 -> Restaurant
```
**Captured live: 200 and 200.** The route is data, not code: [`src/Tadka.Gateway/appsettings.json`](../../src/Tadka.Gateway/appsettings.json) matches `/api/v1/restaurants/{**remainder}`. That completes the canonical **4 services + gateway**. Each service still validates the JWT itself; the gateway is a router, not a trust boundary.

---

## 9.1. Identity and ownership hardening behind the four services (ADR-030, 031, 052, 053, 065 to 067)

**What you're proving:** with four services each verifying tokens and each holding its own data, *how* they verify and *who may touch what* matters more than it did with one. Nothing in this section shares a secret, and every ownership rule lives in the service that owns the data.

Set up once (a fresh order that flows through payment, confirmation and rider assignment, plus Rahul and the rider on that order):
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
until [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN")" = "200" ]; do sleep 2; done   # rider assigned
RAHUL=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"rahul@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
TRACK=$(curl -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN")
RIDER_EMAIL="$(echo "$TRACK" | sed -E 's/.*"agentName":"([^"]+)".*/\1/' | tr 'A-Z' 'a-z').rider@tadka.test"
RIDER=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d "{\"email\":\"$RIDER_EMAIL\",\"password\":\"Password123!\"}" | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
```
```powershell
$ORDER = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -Headers $H -ContentType "application/json" -Body $BODY).id
do { Start-Sleep -Seconds 2 } until ((Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/track" -Headers $H) -eq 200)   # rider assigned
$RAHUL = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"rahul@tadka.test","password":"Password123!"}').accessToken
$track = Invoke-RestMethod -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/track" -Headers $H
$riderEmail = "$($track.agentName.ToLower()).rider@tadka.test"
$RIDER = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body (@{ email = $riderEmail; password = "Password123!" } | ConvertTo-Json)).accessToken
$HR = @{ Authorization = "Bearer $RIDER" }
```

### RS256 + JWKS: no service holds a signing secret (ADR-067)
`Tadka.Api` signs access tokens with an RSA private key that never leaves its process and publishes the public keys. Payment, Delivery and Restaurant each fetch them (cached 5 minutes, `Jwt:JwksCacheMinutes`) and verify by `kid`, so a compromised Delivery can verify tokens but cannot mint one. There is no `Jwt:SigningKey` in any `appsettings.json`.
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

**More than one monolith replica (the `scale-out` profile) needs the same key everywhere.** A key generated in one process is unknown to its siblings, so a token signed by `api-1` would be rejected by `api-2`. The compose `scale-out` profile therefore sets `Jwt__SigningKeyPem` (a dev-only key) on all three replicas: every replica signs with, and publishes, the same key, and the `kid` is derived from the public key so it matches everywhere. In that mode `rotate-signing-key` answers **409**, because rotation means deploying a new key (new key in the secret, old one as `Jwt:PreviousSigningKeyPem`), not calling one replica. The Terraform and Azure stacks generate an RSA key and give it to the monolith only.
> Pinned by `SharedSigningKeyTests` (two replicas, one key: same `kid`, cross-verify, 409 on rotate); the multi-replica compose run itself was not exercised here.

### Access and refresh tokens, and a rate-limited login (ADR-066, ADR-065)
Login returns a 15-minute access token and a 7-day refresh token. `POST /api/v1/auth/refresh` consumes the presented refresh token and returns a new pair; presenting an already-used one revokes the whole family (a theft signal). `POST /api/v1/auth/logout` revokes the caller's family (the current access token still works until it expires). The credential endpoints allow **5 requests per 10 seconds per IP** (`Auth:RateLimit`, counted in Redis so every replica shares one budget) and lock an account for 60 seconds after 5 wrong passwords (`Auth:Lockout`). **A script that logs several users in back to back can hit 429**: space the logins out, or raise `Auth:RateLimit:PermitLimit` for a demo run.

### Ownership, enforced where the data lives (ADR-031)
Payment, Delivery and the order-status endpoint each refuse a valid token that is not the right person's:
```bash
curl -s -o /dev/null -w "Rahul reads Priya's payment: %{http_code}\n" http://localhost:5240/api/v1/payments/$ORDER -H "Authorization: Bearer $RAHUL"   # 403
curl -s -o /dev/null -w "Priya reads her payment: %{http_code}\n" http://localhost:5240/api/v1/payments/$ORDER -H "Authorization: Bearer $TOKEN"            # 200
curl -s -o /dev/null -w "Priya triggers a charge: %{http_code}\n" -X POST http://localhost:5240/api/v1/payments/charge -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "{\"orderId\":\"$ORDER\",\"amount\":1}"   # 403 (Admin only)
curl -s -o /dev/null -w "Rahul tracks Priya's order: %{http_code}\n" http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $RAHUL"   # 403
curl -s -o /dev/null -w "Rahul moves Priya's rider: %{http_code}\n" -X PUT http://localhost:5250/api/v1/deliveries/$ORDER/location -H "Authorization: Bearer $RAHUL" -H "Content-Type: application/json" -d '{"latitude":12.0,"longitude":75.0}'   # 403
curl -s -o /dev/null -w "the rider moves their own dot: %{http_code}\n" -X PUT http://localhost:5250/api/v1/deliveries/$ORDER/location -H "Authorization: Bearer $RIDER" -H "Content-Type: application/json" -d '{"latitude":12.95,"longitude":77.64}'   # 204 (needs Redis)
```
```powershell
"Rahul reads Priya's payment: " + (Get-StatusCode -Uri "http://localhost:5240/api/v1/payments/$ORDER" -Headers @{ Authorization = "Bearer $RAHUL" })   # 403
"Priya reads her payment: " + (Get-StatusCode -Uri "http://localhost:5240/api/v1/payments/$ORDER" -Headers $H)   # 200
"Priya triggers a charge: " + (Get-StatusCode -Uri http://localhost:5240/api/v1/payments/charge -Method Post -Headers $H -Body "{`"orderId`":`"$ORDER`",`"amount`":1}")   # 403 (Admin only)
"Rahul tracks Priya's order: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/track" -Headers @{ Authorization = "Bearer $RAHUL" })   # 403
"Rahul moves Priya's rider: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/location" -Method Put -Headers @{ Authorization = "Bearer $RAHUL" } -Body '{"latitude":12.0,"longitude":75.0}')   # 403
"the rider moves their own dot: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/location" -Method Put -Headers $HR -Body '{"latitude":12.95,"longitude":77.64}')   # 204 (needs Redis)
```
Payment keeps the customer from the `order-placed` event (`OrderPlacedMessage.CustomerId`) and Delivery keeps it from `order-confirmed`, because neither service has an orders table to look it up in. A `RestaurantOwner` may only advance the status of orders at their own restaurant. Each rider record links to a login (`DeliveryAgent.UserId`), which is how Delivery answers "is the caller the rider on this order?".
> Pinned by `AuthorizationTests`, `PaymentServiceTests`, `DeliveryOwnershipTests`, and by `RealJwtAuthorizationTests` / `JwksValidationTests` (real RS256 tokens through each service's real JWT handler: the synthetic test identity never goes through the handler's claim renaming, so only these catch a missing `MapInboundClaims = false`). An order placed before this change has no `CustomerId` on its payment or assignment, so its owner gets 403 too: the safe default when there is no owner to compare against.

### A busy dinner rush no longer drops orders; riders are released
Delivery used to log "left unassigned" when every rider was busy, and the consumer still stamped the Inbox and committed the offset, so nothing retried that order; and nothing ever set a rider back to `Available`, so a fresh database could assign exactly three orders. Now an order that finds nobody free is **parked** in `delivery.pending_assignments`, `PendingAssignmentSweeper` retries it every `Delivery:PendingRetrySeconds` (5), and `Delivered`/`Cancelled` release the rider.
```bash
curl -s -o /dev/null -w "picked up: %{http_code}\n" -X PATCH http://localhost:5250/api/v1/deliveries/$ORDER/status -H "Authorization: Bearer $RIDER" -H "Content-Type: application/json" -d '{"status":"PickedUp"}'    # 204 (skipping this step gives 422)
curl -s -o /dev/null -w "delivered: %{http_code}\n" -X PATCH http://localhost:5250/api/v1/deliveries/$ORDER/status -H "Authorization: Bearer $RIDER" -H "Content-Type: application/json" -d '{"status":"Delivered"}'   # 204, rider Available again
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "SELECT \"Name\",\"Status\" FROM delivery.agents;"
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "SELECT \"OrderId\",\"Attempts\",\"CreatedAt\" FROM delivery.pending_assignments;"
```
```powershell
"picked up: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/status" -Method Patch -Headers $HR -Body '{"status":"PickedUp"}')    # 204
"delivered: " + (Get-StatusCode -Uri "http://localhost:5250/api/v1/deliveries/$ORDER/status" -Method Patch -Headers $HR -Body '{"status":"Delivered"}')   # 204
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "SELECT \"Name\",\"Status\" FROM delivery.agents;"
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "SELECT \"OrderId\",\"Attempts\",\"CreatedAt\" FROM delivery.pending_assignments;"
```
To see the parking itself: place four orders on a fresh stack (the three riders take the first three); the fourth appears in `pending_assignments` with the log line `No available rider for order ... parked`. Deliver one of the first three as above, and within `PendingRetrySeconds` the log shows `Waiting order ... finally got rider ...` and the row is gone. The rider claim is atomic (`UPDATE ... WHERE Status = 'Available'`, run inside the execution strategy so the failover demo's retrying DB strategy can replay it), because a sweeper now assigns at the same time as the Kafka consumer. Assignment is still **first-available**, not nearest.
> Pinned by `PendingAssignmentTests`, `DeliveryServiceTests` and the concurrent-assignment test; not re-run against a live stack.

### PII at rest and card tokens (ADR-052, ADR-053)
`identity.users.Phone` is AES-GCM encrypted in the database (random nonce, so it is not searchable) and decrypted transparently for the owner; the card number becomes a **keyed HMAC** token the instant it reaches Payment (`payment.payments` has `CardToken` and `CardLast4`, no PAN column). The key (`Demo:CardTokenizationKey`) is a secret held only by the Payment service: an unkeyed hash of a card number can be brute-forced from a leaked table, because the BIN and last four digits are already known. `Demo:EncryptPiiAtRest=false` turns field encryption off; flipping it on an existing volume fails with `FormatException`, so reset volumes when you change it.
```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\", \"Phone\" FROM identity.users LIMIT 3;"     # ciphertext, not phone numbers
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"CardToken\",\"CardLast4\" FROM payment.payments WHERE \"CardToken\" IS NOT NULL;"
```

### Swap the issuer for a real identity provider (Keycloak, optional)
Because Payment, Delivery and Restaurant verify through the JWKS contract, pointing them at Keycloak is configuration only. Keycloak listens on **host port 8081** here, because the gateway owns 8080.
```bash
docker compose --profile auth-prod up -d keycloak
curl -s http://localhost:8081/realms/tadka/.well-known/openid-configuration | grep jwks_uri
KC=$(curl -s -X POST http://localhost:8081/realms/tadka/protocol/openid-connect/token -d "client_id=tadka-api" -d "username=priya@tadka.test" -d "password=Password123!" -d "grant_type=password" | sed -E 's/.*"access_token":"([^"]+)".*/\1/')
dotnet run --project src/Tadka.Payment.Api --launch-profile Keycloak     # Delivery and Restaurant have the same profile name
curl -s -o /dev/null -w "Payment with a Keycloak token: %{http_code}\n" http://localhost:5240/api/v1/payments/$ORDER -H "Authorization: Bearer $KC"
docker compose --profile auth-prod down
```
```powershell
docker compose --profile auth-prod up -d keycloak
curl.exe -s http://localhost:8081/realms/tadka/.well-known/openid-configuration
$KC = (Invoke-RestMethod -Uri http://localhost:8081/realms/tadka/protocol/openid-connect/token -Method Post -Body @{ client_id = "tadka-api"; username = "priya@tadka.test"; password = "Password123!"; grant_type = "password" }).access_token
dotnet run --project src/Tadka.Payment.Api --launch-profile Keycloak
"Payment with a Keycloak token: " + (Get-StatusCode -Uri "http://localhost:5240/api/v1/payments/$ORDER" -Headers @{ Authorization = "Bearer $KC" })
docker compose --profile auth-prod down
```
The realm (`infra/keycloak/tadka-realm.json`) also defines the three riders (`suresh.rider@`, `lakshmi.rider@`, `imran.rider@tadka.test`) with role `DeliveryAgent`. Full walkthrough: [`docs/learn/keycloak-integration-showcase.md`](../learn/keycloak-integration-showcase.md); the token lifecycle: [`docs/learn/token-and-refresh-flow.md`](../learn/token-and-refresh-flow.md).

> **Not re-run against a live stack on this branch:** the commands in this section were adapted from the ones run live on the earlier branches (new ports, the gateway, the four-service topology). The behaviour itself is pinned by the test suites: `JwksTests`, `SharedSigningKeyTests`, `RefreshTokenTests`, `RateLimitingTests`, `AuthorizationTests`, `FieldCipherTests` (monolith); `JwksValidationTests`, `PaymentServiceTests`, `CardTokenizerTests` (Payment); `DeliveryOwnershipTests`, `PendingAssignmentTests`, `RealJwtAuthorizationTests` (Delivery); `JwksValidationTests` (Restaurant).

---

## 10. Deferred, not run here: the live Azure cloud walk (ADR-064)

The teaching script's Segment 7 walks a *real*, currently deployed Azure Container Apps stack (a Front Door CDN cache hit, a private Postgres, counting the hops through the platform's own Envoy sidecars, the SSE-bypasses-the-CDN pattern). That is real cloud infrastructure that costs real money and needs the instructor's own Azure credentials and a pre-class `cloud-up.ps1 -Mode basic` run, so it is **not** part of running this runbook, and **nothing in this section was executed live**. If you have that stack up, see `deploy/README.md`, `docs/adrs/064-live-cloud-deployment-azure-container-apps.md`, and `docs/runbooks/azure-getting-started.md` for the one-time setup. Run `cloud-down.ps1` afterwards.

---

## 11. Run the tests
```
dotnet test
```
**177/177** on this branch at the time of writing: monolith 95, Payment 31, Delivery 17, Restaurant 19, and a `Tadka.Gateway.Tests` project (15) that no earlier count in this file included. That includes 16 Kafka authentication tests (4 per service, no broker needed), and the real-Kafka integration tests (Testcontainers) that run against an unauthenticated broker: their fixtures blank `Kafka:SaslUsername`, because the test host runs in the `Development` environment and would otherwise inherit the compose broker's credentials. `day-12` stays fast-forwarded to `main`, so **this number will move again**: `dotnet test` is the source of truth, not a number in a doc. The integration tests use Testcontainers, so Docker must be running.

---

## 12. Wiring Reference & Cross-Stack Architecture

### Where the code lives in Tadka
- **Restaurant extraction (ADR-036):** [`src/Tadka.Restaurant.Api/Controllers/RestaurantsController.cs`](../../src/Tadka.Restaurant.Api/Controllers/RestaurantsController.cs), [`Data/RestaurantDbContext.cs`](../../src/Tadka.Restaurant.Api/Data/RestaurantDbContext.cs) (own Postgres, own migration history).
- **Local read model (ADR-037):** [`LocalReplicaPricingSource.cs`](../../src/Tadka.Api/Infrastructure/RestaurantReadModel/LocalReplicaPricingSource.cs) and `HttpRestaurantPricingSource.cs` (both implement `IRestaurantPricingSource`), chosen in `Program.cs` off `Ordering:RestaurantReadMode`; fed by [`MenuUpdatedConsumer.cs`](../../src/Tadka.Api/Infrastructure/Messaging/MenuUpdatedConsumer.cs).
- **Zero-downtime migration (ADR-038):** [`scripts/backfill-menu-replica.ps1`](../../scripts/backfill-menu-replica.ps1), [`scripts/expand-contract-demo.ps1`](../../scripts/expand-contract-demo.ps1).
- **Gateway route:** [`src/Tadka.Gateway/appsettings.json`](../../src/Tadka.Gateway/appsettings.json).
- **SSE ownership + cap:** [`OrderTrackingController.cs`](../../src/Tadka.Api/Controllers/OrderTrackingController.cs), [`SseStreamLimiter.cs`](../../src/Tadka.Api/Infrastructure/Realtime/SseStreamLimiter.cs).
- **Replica-lag metric (ADR-063):** [`TadkaDiagnostics.cs`](../../src/Tadka.Telemetry/TadkaDiagnostics.cs).

### Cross-Stack Implementation Matrix

| Concern | .NET Core (This Repo) | Java (Spring Boot) | Node.js (TypeScript) | Go |
|---|---|---|---|---|
| **Local read model fed by events** | EF Core upsert in `MenuUpdatedConsumer` | Spring Kafka listener + JPA upsert | kafkajs consumer + Prisma upsert | kafka-go consumer + sqlc |
| **Zero-downtime migration** | Hand-rolled chunked scripts (this repo) | Flyway / Liquibase (expand-contract) | Prisma Migrate / Knex | goose / Atlas |
| **Continuous CDC at scale** | Debezium (Kafka Connect, any stack) | Debezium | Debezium | Debezium |
| **Reverse-proxy gateway, 4th route** | YARP | Spring Cloud Gateway | Express-gateway | Kong / Envoy / Traefik |
| **Cloud deploy shape** | Terraform to ECS or Container Apps (ADR-039/064) | same cloud primitives | same cloud primitives | same cloud primitives |

**Pattern is language-neutral.** Event-carried state transfer and expand-contract migrations are architecture ideas, not .NET ideas: the same shape shows up wherever a service needs its own copy of another service's data on a hot path. Full matrix: `day-12/option-space.md`.

---

## 13. Demo vs. Production: named gaps, not overclaimed features
- **No automatic timeout-reject in Service decision mode.** If Restaurant.Api never responds, the order sits `Confirmed` forever. Named as a revisit in ADR-062.
- **The replica-lag metric has no alert.** A stalled replica is visible if someone looks at the gauge, not paged on (ADR-063).
- **The SSE stream cap is a single in-memory counter per process.** With N replicas the effective per-user cap is N x 3. A shared counter (Redis) is the real answer at scale.
- **PgBouncer is in this branch's compose but not in the cloud deployment yet.** The local demo (Day 11) and the live cloud stack are two stories that have not been reconciled.
- **The giant-`UPDATE` contrast does not hurt at demo scale.** 20,000 rows updates in about half a second; the outage it illustrates needs a table orders of magnitude larger.
- **Kafka is authenticated, not encrypted or isolated, and only locally.** The docker-compose broker requires SASL/SCRAM-SHA-256, which stops anonymous access to every topic. It is `SASL_PLAINTEXT` (production uses `SASL_SSL`), every service and tool shares one `tadka` user with no ACLs (real isolation is a user per service plus topic ACLs), and the password is a demo default committed to `appsettings.Development.json`. **The Azure/cloud Kafka (ADR-064) has no authentication at all**: it is a separate plain container reachable only inside the private Container Apps network. The apps only turn SASL on when `Kafka:SaslUsername` is set, so the cloud configuration, which sets none, keeps working unchanged; securing it needs a custom image carrying `docker/kafka-scram-entrypoint.sh`, a CI build step and Terraform secret plumbing. See ADR-027's security addendum.

## Reset
`docker compose down -v` clears all volumes (including the backfill's synthetic rows).

## Ports
monolith :5224 · payment :5240 · delivery :5250 · restaurant :5260 · gateway :8080 · postgres 5432 / replica 5433 / payment-db 5434 / delivery-db 5435 / restaurant-db 5436 · redis 6379 · kafka 9092 / kafka-ui 8090 · **PgBouncer 6432** (Day 11, carried forward; not a Day-12 teaching beat)

## ✅ Done when
- [ ] Baseline order totals **₹598**; with `SyncHttp` and Restaurant down `POST /orders` fails (**500**); with `LocalReplica` and Restaurant still down it succeeds (**Created, ₹598**).
- [ ] PATCH price 299 to 349 returns **204**; the replica shows **349.00**; a new 2x order totals **₹698**.
- [ ] The backfill completes (**5016 rows, 6 batches**) with replica lag in single-digit milliseconds and no lock.
- [ ] Service mode: order **Cancelled**, payment **Refunded** (`/api/v1/payments/{orderId}`).
- [ ] Dual-write PATCH fills both `Name` and `DisplayName`; `expand-contract-demo.ps1` ends with `remaining NULL DisplayName: 0` (and stops on its own).
- [ ] SSE: a non-owner gets **403**; a 4th concurrent stream for one user gets **429**.
- [ ] The replica-lag tests pass (**5/5**).
- [ ] The gateway routes `/api/v1/restaurants/**` (**200**).
- [ ] Rahul reading Priya's payment or tracking her order gets **403**; Priya gets **200**; a customer POSTing `/api/v1/payments/charge` gets **403**.
- [ ] `/.well-known/jwks.json` lists the public key; `rotate-signing-key` (Admin) adds a new one and the old token still verifies once (**200**).
- [ ] The rider on an order can `PATCH` it `PickedUp` then `Delivered` (**204**, **204**) and is `Available` again; a fourth order on a fresh stack is parked in `pending_assignments` and assigned once a rider frees up.
- [ ] `dotnet test` is green (**177/177** at the time of writing).
- [ ] A Kafka command with no credentials hangs; with `--command-config /etc/kafka/docker/client.properties` it answers.

## Troubleshooting
- **Baseline order is not ₹598:** a prior run already did Demo 2's PATCH and the restaurant-db volume kept ₹349. Run `docker compose down -v` and restart.
- **`Cannot open ...Tadka.Telemetry.dll for writing` when starting the apps:** you started several `dotnet run` at once on a fresh checkout. Run `dotnet build Tadka.slnx` once first, then start them.
- **Menu PATCH returns 401 or 403:** you are using a customer token. Log in as `admin@tadka.test`.
- **Login returns 429 during a demo:** the credential endpoints allow 5 requests per 10 seconds per IP. Space the logins out, or start the monolith with `Auth__RateLimit__PermitLimit=1000`.
- **Every call to Payment, Delivery or Restaurant is 401 with a fresh token:** they fetch the monolith's public keys from `Jwt:JwksBaseUrl` (default `http://localhost:5224`). Check the monolith is up and that URL is reachable; a monolith restart issues new keys, so log in again.
- **Tokens work on one monolith replica but not another (scale-out profile):** the replicas are not sharing a signing key. Set the same `Jwt__SigningKeyPem` on all of them.
- **`GET /payments/{id}` returns a bare `404`:** wrong path on this branch. Use `/api/v1/payments/{orderId}`.
- **`expand-contract-demo.ps1` throws parser errors, or never stops:** you have the pre-fix version. Both bugs are fixed on this branch; pull it.
- **Stuck `Confirmed` in Demo 4:** Restaurant.Api or Kafka is down or not consuming; see `decision-mode-matrix.md`. Fallback: flip `Restaurant__DecisionMode` back to `Inline` for the next order.
- **SSE returns 403 for the order's real owner:** wrong token; re-login and check the `sub` claim matches the order's `customerId`.
- **SSE returns 429 on the first attempt:** streams from an earlier test are still open. Look for stray `curl -N` processes.
- **Demo 4 or 6 looks stuck right after a restart:** cold JIT plus consumer-group join. Wait up to a minute; later requests are fast.
- **Every Kafka command hangs and prints nothing:** you left off the credentials flag. Add `--command-config /etc/kafka/docker/client.properties` (or `--producer.config` / `--consumer.config` for the console tools).
- **A service logs `Disconnected: connection closed by peer` and `1/1 brokers are down` repeatedly:** it is connecting to Kafka without credentials. Check `Kafka:SaslUsername` and `Kafka:SaslPassword` in that service's `appsettings.Development.json` (each of the four has its own).
- **A real-Kafka test in your own test project times out with "Value is null":** the test host inherited the SASL credentials from `appsettings.Development.json`. Blank them in the fixture: `builder.UseSetting("Kafka:SaslUsername", "")`.
- **PgBouncer `SHOW POOLS` fails with "not allowed":** connect as the `tadka` user (set via `ADMIN_USERS` in `docker-compose.yml`), not `postgres`.
