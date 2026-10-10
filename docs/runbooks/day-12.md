# Day 12: Runbook: Extract Restaurant (4th service) + zero-downtime migration

**Branch:** `day-12`  ·  **What changed since Day 11:** [`docs/changelog.md`](../changelog.md).

Tadka reaches the canonical **4 services + a gateway**. Restaurant is the *last* and *hardest* extraction because order pricing reads its menu on the critical path, so it earns a **local read model** (event-carried state transfer, ADR-037) and a **zero-downtime data backfill** (ADR-038). Deploy is a black box (`deploy/README.md`, ADR-039/064). This branch is intentionally kept fast-forwarded to `main` (`docs/runbooks/DAY-EVOLUTION.md`), so it also carries the live-tracking SSE hardening (ownership check plus a per-user stream cap) that landed after this branch's own Day-12 work. Both get their own demo below, since neither exists on Day 11.

> Demo password `Password123!`.

Every command below is given twice, bash first and PowerShell second, wherever the two shells differ. They are not the same commands with `curl` swapped for `curl.exe`: bash's `VAR=$(...)`, `sed -E`, and inline `VAR=value command` syntax do not run in plain PowerShell at all. Windows PowerShell 5.1 also cannot read an HTTP status code off a 4xx/5xx response without the call throwing, so one small helper is defined once in section 1 and reused. Every bash and PowerShell block here was run live against this branch (the five services, Postgres, Kafka, Redis and Keycloak all up) before being written down.

> **Using Git Bash on Windows?** Git Bash rewrites any argument that looks like a Unix path, so `docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh ...` fails with `C:/Program Files/Git/opt/kafka/...: no such file`. Run `export MSYS_NO_PATHCONV=1` once per terminal first (WSL, macOS and Linux do not need it). Git Bash's `curl` may also exit with code 23 after printing the right answer when you use `-o /dev/null`; the printed status is still correct.

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

**What this does.** [`docker-compose.yml`](../../docker-compose.yml) defines the infrastructure the services need: the monolith's Postgres (5432) and its read replica (5433), `payment-db` (5434), `delivery-db` (5435), the new `restaurant-db` (5436), Redis, Kafka, Kafka UI and PgBouncer. `up -d` starts them in the background. Postgres, Kafka and the three service databases declare a Docker **healthcheck**, so the two `until` loops simply poll `docker inspect` until Docker reports them `healthy`; starting the apps before that gives connection errors.
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

Why the topics must exist: a consumer subscribes the moment its service starts, and Kafka only creates a topic when someone first publishes to it. `--if-not-exists` makes the loop safe to run again, `--partitions 1 --replication-factor 1` is right for a single local broker, and `--command-config` carries the SASL login described next. The eight names are the cross-service contract in each service's `Messaging.cs` (for example [`Tadka.Api/Infrastructure/Messaging/Messaging.cs`](../../src/Tadka.Api/Infrastructure/Messaging/Messaging.cs)).

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

Five processes, five terminals (identical in either shell). Each one reads its port from its own `Properties/launchSettings.json` (for example [`Tadka.Restaurant.Api`](../../src/Tadka.Restaurant.Api/Properties/launchSettings.json) uses 5260), connects to its own database as set in its `appsettings.Development.json`, and applies its own EF migrations on start-up, which is why each service owns its schema:
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

**What the shared setup does.** `POST /api/v1/auth/login` ([`AuthController`](../../src/Tadka.Api/Auth/AuthController.cs)) returns an RS256 access token for the seeded customer Priya. `$BODY` is an order for 2 x Chicken Biryani at Meghana Foods (the ids come from the seed in [`MenuReplica.cs`](../../src/Tadka.Api/Data/ReadModel/MenuReplica.cs)): the client sends *what it wants*, never a price, and the server prices it (Section 2). Each block below that restarts the monolith logs in again, because its signing keys live in memory.

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
**What this proved.** `Ordering__RestaurantReadMode=SyncHttp` swaps the pricing source to [`HttpRestaurantPricingSource`](../../src/Tadka.Api/Infrastructure/RestaurantReadModel/HttpRestaurantPricingSource.cs), which calls Restaurant over HTTP with a 2 second timeout. With Restaurant stopped the call throws and the whole order fails.

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
**What this proved.** With no override, `Program.cs` registers [`LocalReplicaPricingSource`](../../src/Tadka.Api/Infrastructure/RestaurantReadModel/LocalReplicaPricingSource.cs), which reads `ordering.menu_replica` from Ordering's own database. No network call, so a dead Restaurant is invisible to checkout.

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

**What each command does.** The `PATCH` goes to the **Restaurant** service ([`RestaurantsController.UpdateMenuItem`](../../src/Tadka.Restaurant.Api/Controllers/RestaurantsController.cs)): it changes the price, calls `StageSnapshot` (an Outbox row in the same `SaveChanges`) and clears the menu cache. The `psql` line reads Ordering's own table, never Restaurant's. The last line proves checkout now uses the new price.

### What if a snapshot cannot be applied?
A consumer must never skip a message it failed to apply: committing the next offset would silently commit past it, and the replica would stay stale until that restaurant changed again. [`MenuUpdatedConsumer`](../../src/Tadka.Api/Infrastructure/Messaging/MenuUpdatedConsumer.cs) (`HandlePoisonAsync`) seeks back to the failed offset, retries it 3 times, then publishes it to `menu-updated.dlq` with the original payload and commits. Send it a deliberately broken message and read it back from the dead-letter topic (the console tools need the SASL config, as above):
```bash
echo '{ this is not valid json' | docker exec -i tadka-kafka /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server localhost:9092 --producer.config /etc/kafka/docker/client.properties --topic menu-updated
sleep 8
docker exec tadka-kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --consumer.config /etc/kafka/docker/client.properties --topic menu-updated.dlq --from-beginning --max-messages 1 --timeout-ms 20000
```
```powershell
'{ this is not valid json' | docker exec -i tadka-kafka /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server localhost:9092 --producer.config /etc/kafka/docker/client.properties --topic menu-updated
Start-Sleep -Seconds 8
docker exec tadka-kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --consumer.config /etc/kafka/docker/client.properties --topic menu-updated.dlq --from-beginning --max-messages 1 --timeout-ms 20000
```
**Captured live:** the message arrives on `menu-updated.dlq` as `{"OriginalTopic":"menu-updated","OriginalPayload":"{ this is not valid json","Error":"...","Attempts":3,...}`. (Windows PowerShell 5.1 prepends a byte-order mark to piped text, so its copy shows up as `\uFEFF{ ...` and fails the same way. It also prints the tool's final "Processed a total of 1 messages" line as a red error because that line goes to stderr; it is not a failure.) The test `MenuUpdatedConsumerKafkaTests` pins the other half: a good snapshot sent *after* the poison one is still applied.

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

**What the flags do.** `-SeedExtra 5000` inserts 5,000 synthetic menu items into `restaurant-db` first so the copy is sizeable; `-ChunkSize 1000` is rows per batch and per transaction; `-ThrottleMs 50` is the pause between batches. Each line it prints is one batch: how many rows, the running total, the replica lag read from `pg_stat_replication`, and the **high-water mark** (the last id copied).

**Captured live, twice:** `5016 row(s)` backfilled in **`6 batches over 7s`**, replica lag staying at **`1-10 ms`** throughout, the table never locked. (5016 = the 16 real seeded items plus 5000 synthetic ones.) Strategy in class, not script: the LLD lives in the repo, not on the slide.

**Several workers.** Start the same script once per worker, each with the same `-Workers` and its own `-Worker` index. Worker *i* only reads rows where `abs(hashtext("Id"::text)::bigint) % Workers = i`, so the slices are disjoint and together cover the table (no coordination is needed except agreeing on N):
```bash
powershell.exe -NoProfile -File ./scripts/backfill-menu-replica.ps1 -Workers 2 -Worker 0 -ChunkSize 1000 -ThrottleMs 50 &
powershell.exe -NoProfile -File ./scripts/backfill-menu-replica.ps1 -Workers 2 -Worker 1 -ChunkSize 1000 -ThrottleMs 50
wait
```
```powershell
$j = Start-Job { Set-Location $using:PWD; .\scripts\backfill-menu-replica.ps1 -Workers 2 -Worker 1 -ChunkSize 1000 -ThrottleMs 50 }
.\scripts\backfill-menu-replica.ps1 -Workers 2 -Worker 0 -ChunkSize 1000 -ThrottleMs 50
Receive-Job -Wait $j
```
**Captured live:** worker 0 copied 2,481 rows and worker 1 copied 2,535, which add up to exactly 5,016, and `ordering.menu_replica` held 5,016 rows afterwards. Compare the counts yourself (this is the "verify" step before switching reads):
```bash
docker exec tadka-restaurant-db psql -U tadka -d tadka_restaurant -t -A -c 'select count(*) from restaurant.menu_items;'
docker exec tadka-postgres psql -U tadka -d tadka -t -A -c 'select count(*) from ordering.menu_replica;'
```
```powershell
docker exec tadka-restaurant-db psql -U tadka -d tadka_restaurant -t -A -c 'select count(*) from restaurant.menu_items;'
docker exec tadka-postgres psql -U tadka -d tadka -t -A -c 'select count(*) from ordering.menu_replica;'
```

**Resuming after a crash.** Every batch prints its high-water mark. If a run dies, pass the last one you saw as `-StartAfterId` and it carries on from there (the upserts are idempotent, so overlap is harmless):
```
./scripts/backfill-menu-replica.ps1 -StartAfterId 99601cfb-695c-4f6a-9f25-719f73c4d2fc
```
**Captured live:** resuming from the third batch's mark copied the remaining 2,016 rows.

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

**What each step does.** `Restaurant__AcceptMode=Reject` makes Restaurant answer `Rejected` ([`OrderConfirmedConsumer`](../../src/Tadka.Restaurant.Api/Messaging/OrderConfirmedConsumer.cs)); `Restaurant__DecisionMode=Service` tells the monolith not to decide itself ([`RestaurantAcceptanceOptions`](../../src/Tadka.Api/Domain/Restaurants/RestaurantAcceptanceOptions.cs)). The polling loop re-reads the order from Ordering and the payment from Payment every 2 seconds until the saga has finished or the timeout passes.

**Captured live:** order `Cancelled` and payment **`Refunded`** with a fresh gateway reference (`FAKEREF-…`), about **39 seconds** after services had just restarted. Give it real time: this is a 3-hop Kafka chain (`order-confirmed` → `restaurant-response` → `refund-requested` → `payment-refunded`), longer than Demo 1's single hop or Day 11's 2-hop refund.
Stuck `Confirmed`? Restaurant.Api or Kafka is down: see [decision-mode-matrix.md](decision-mode-matrix.md).

**See the saga as data.** Start the monolith with `Saga__Mode=Orchestration` as well (`Restaurant__DecisionMode=Service Saga__Mode=Orchestration dotnet run --project src/Tadka.Api`, or `$env:Saga__Mode = "Orchestration"` in PowerShell), place another order, and the refund steps are recorded in `ordering.saga_instances` ([`RefundSagaOrchestrator`](../../src/Tadka.Api/Infrastructure/Messaging/RefundSagaOrchestrator.cs)). The writes and events are identical in both modes; only the bookkeeping differs:
```bash
docker exec tadka-postgres psql -U tadka -d tadka -c 'SELECT "SagaType","CurrentStep","Status","Detail" FROM ordering.saga_instances ORDER BY "StartedAt" DESC LIMIT 3;'
```
```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \`"SagaType\`",\`"CurrentStep\`",\`"Status\`",\`"Detail\`" FROM ordering.saga_instances ORDER BY \`"StartedAt\`" DESC LIMIT 3;"
```
**Captured live:** `refund-compensation | refund-requested | Completed | Restaurant rejected; starting compensation`. The step moves `cancel-order` to `request-refund` to `refund-requested` while `Status` goes `Running` to `Completed`.

### How this is actually implemented
**The rider goes back too.** The monolith sent `order-confirmed` to Delivery as well, so a rider was assigned before the restaurant said no. When `payment-refunded` arrives, Delivery's [`PaymentRefundedConsumer`](../../src/Tadka.Delivery.Api/Messaging/PaymentRefundedConsumer.cs) calls [`DeliveryService.CancelOrderAsync`](../../src/Tadka.Delivery.Api/DeliveryService.cs): it writes the order into `delivery.cancelled_orders` (so an `order-confirmed` that is still waiting in Kafka cannot hand the order a rider later), drops it from `pending_assignments`, and sets the assignment to `Cancelled`, which frees the rider. Check the rider count once the order shows `Cancelled` (reset the riders first with the SQL in Section 9.1 if earlier demos used them up); `$ORDER` is the rejected order's id:
```bash
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c 'SELECT "Status", count(*) FROM delivery.agents GROUP BY 1;' -c "SELECT \"Status\" FROM delivery.assignments WHERE \"OrderId\" = '$ORDER';"
```
```powershell
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "SELECT \`"Status\`", count(*) FROM delivery.agents GROUP BY 1;" -c "SELECT \`"Status\`" FROM delivery.assignments WHERE \`"OrderId\`" = '$ORDER';"
```
**Captured live:** with warm services the order read `Cancelled` about 6 seconds after placing; at that point all three riders were `Available` and the assignment read `Cancelled`. Pinned by `CancelledOrderTests` and `PaymentRefundedConsumerKafkaTests`.

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

Dual-write is **on by default** (`Demo:DualWriteDisplayName`, default `true`), because reads already prefer `DisplayName`: if a rename wrote only `Name`, the API would keep showing the old name. The `=true` below just makes it explicit. To watch what it protects you from, start Restaurant with `Demo__DualWriteDisplayName=false`, rename an item, and compare the two columns: `Name` changes, `DisplayName` stays behind (this variation is covered by `ExpandContractDualWriteTests`, not re-run live). To see the drift for yourself, start Restaurant with it off, rename an item and read both columns back (bash shown; in PowerShell set `$env:Demo__DualWriteDisplayName = "false"` first and use the backtick-backslash quoting from Demo 2):
```bash
Demo__DualWriteDisplayName=false dotnet run --project src/Tadka.Restaurant.Api
# then, as admin:
curl -s -o /dev/null -w "PATCH name (dual-write OFF): %{http_code}\n" -X PATCH http://localhost:5260/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu/b1b2c3d4-0001-4000-8000-000000000001 -H "Authorization: Bearer $ADMIN" -H "Content-Type: application/json" -d '{"name":"Offline Rename"}'
docker exec tadka-restaurant-db psql -U tadka -d tadka_restaurant -c 'SELECT "Name","DisplayName" FROM restaurant.menu_items WHERE "Id"='"'"'b1b2c3d4-0001-4000-8000-000000000001'"'"';'
```
**Captured live:** `Name` became `Offline Rename` while `DisplayName` stayed empty (for a row that had already been backfilled it would keep the *old* name, and because reads prefer `DisplayName`, the API would keep showing the old name). That is the divergence the next step prevents. Restart Restaurant with dual-write on and change a menu item's name:
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
**Captured live:** the rows with a NULL `DisplayName` (5,015 on this run: the 5,016 rows from Demo 3 minus the one just dual-written) were backfilled in **26 batches** (25 x 200, then 15), ending with `remaining NULL DisplayName: 0`. Running the script again right away finds nothing to do and reports `rows with NULL DisplayName: 0`, which is what "idempotent and resumable" looks like.

And the contrast, one giant unthrottled `UPDATE`:
```bash
powershell.exe -NoProfile -File ./scripts/expand-contract-demo.ps1 -BreakGiantUpdate
```
```powershell
.\scripts\expand-contract-demo.ps1 -BreakGiantUpdate
```
**Captured live, and be honest about it:** on 20,000 rows the giant `UPDATE` finished in about **500 ms** (519 ms and 486 ms on two runs). That is instant on a laptop. The script's own message says it "locks and blows replica lag on a 50M-row hot table", which is the *shape* of the failure, not something this small demo reproduces. Say so if a student asks why nothing broke.

The `-BreakGiantUpdate` run leaves 20,000 synthetic `LoadTest` rows in `restaurant-db`. Remove them before repeating Demo 3 (otherwise the next backfill copies them too):
```bash
docker exec tadka-restaurant-db psql -U tadka -d tadka_restaurant -c "DELETE FROM restaurant.menu_items WHERE \"Category\" = 'LoadTest';"
```
```powershell
docker exec tadka-restaurant-db psql -U tadka -d tadka_restaurant -c "DELETE FROM restaurant.menu_items WHERE \`"Category\`" = 'LoadTest';"
```

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
**What each command does.** `GET /api/v1/orders/{id}/events` ([`OrderTrackingController.GetEvents`](../../src/Tadka.Api/Controllers/OrderTrackingController.cs)) is a Server-Sent Events stream: the connection stays open and the server writes an event whenever the order changes. `-N` tells curl not to buffer, so you see events as they arrive. The three background streams occupy Priya's three slots, so the fourth request is refused.

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
`--filter "FullyQualifiedName~ReplicaLag"` runs only the tests whose name contains `ReplicaLag` ([`ReplicaLagTests`](../../tests/Tadka.Api.Tests/Telemetry/ReplicaLagTests.cs) for the arithmetic, [`ReplicaLagGaugeWiringTests`](../../tests/Tadka.Api.Tests/Telemetry/ReplicaLagGaugeWiringTests.cs) for the wiring).

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
The first call lists restaurants, the second reads one restaurant's menu; neither carries a token because reads are public. The gateway matched the path against the `restaurants` route and forwarded to `localhost:5260`.

**Captured live: 200 and 200.** The route is data, not code: [`src/Tadka.Gateway/appsettings.json`](../../src/Tadka.Gateway/appsettings.json) matches `/api/v1/restaurants/{**remainder}`. That completes the canonical **4 services + gateway**. Each service still validates the JWT itself; the gateway is a router, not a trust boundary.

---

## 9.1. Identity and ownership hardening behind the four services (ADR-030, 031, 052, 053, 065 to 067)

**What you're proving:** with four services each verifying tokens and each holding its own data, *how* they verify and *who may touch what* matters more than it did with one. Nothing in this section shares a secret, and every ownership rule lives in the service that owns the data.

**First, free the riders.** There are only three riders, and every confirmed order from the earlier demos took one, and a rider comes back only when their delivery is marked `Delivered` or `Cancelled` (Section 9.1 below) or when the refund of a rejected order settles (Demo 4). Orders from Demos 1 to 3 were accepted and never delivered, so if you ran the demos in order their riders are still busy and the next order would simply be parked. This resets the demo database so the set-up below can assign a rider ([`DeliveryDbContext`](../../src/Tadka.Delivery.Api/Data/DeliveryDbContext.cs) seeds the three riders; `delivery.agents.Status` is their availability):
```bash
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c 'UPDATE delivery.agents SET "Status" = '"'"'Available'"'"';' -c 'DELETE FROM delivery.pending_assignments;'
```
```powershell
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "UPDATE delivery.agents SET \`"Status\`" = 'Available'; DELETE FROM delivery.pending_assignments;"
```

**Then** set up once (a fresh order that flows through payment, confirmation and rider assignment, plus Rahul and the rider on that order). The rider's email is the rider's name in lower case plus `.rider@tadka.test` ([`AuthSeeder`](../../src/Tadka.Api/Auth/AuthSeeder.cs) seeds the three rider logins), and the credential endpoints allow only 5 requests per 10 seconds per IP, so if a script of yours logs several people in back to back, pause between them:
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
curl -s -o /dev/null -w "old token after one rotation: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN"                 # 200
curl -s -X POST http://localhost:5224/api/v1/auth/rotate-signing-key -H "Authorization: Bearer $ADMIN" > /dev/null                                                  # a second rotation
curl -s -o /dev/null -w "old token after a second rotation: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN"            # 401
```
```powershell
Invoke-RestMethod -Uri http://localhost:5224/.well-known/jwks.json
$ADMIN = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"admin@tadka.test","password":"Password123!"}').accessToken
Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/rotate-signing-key -Method Post -Headers @{ Authorization = "Bearer $ADMIN" }
"old token after one rotation: " + (Get-StatusCode -Uri "http://localhost:5224/api/v1/orders/$ORDER" -Headers $H)   # 200
Invoke-RestMethod -Uri http://localhost:5224/api/v1/auth/rotate-signing-key -Method Post -Headers @{ Authorization = "Bearer $ADMIN" } | Out-Null   # a second rotation
"old token after a second rotation: " + (Get-StatusCode -Uri "http://localhost:5224/api/v1/orders/$ORDER" -Headers $H)   # 401
```
**What this shows.** The JWKS document lists the public keys ([`Jwks.cs`](../../src/Tadka.Api/Auth/Jwks.cs)); every token's header names the `kid` of the key that signed it. [`SigningKeyStore`](../../src/Tadka.Api/Auth/SigningKeyStore.cs) keeps the current key plus **one** previous, so a token signed before ONE rotation still verifies (200), and after a second rotation the key that signed it has been dropped (401). We check this on the monolith itself, which always uses its live key set. Payment, Delivery and Restaurant behave the same way but **cache** the fetched keys for 5 minutes (`Jwt:JwksCacheMinutes`, [`JwksClient`](../../src/Tadka.Delivery.Api/Auth/JwksClient.cs)), so a retired key can keep verifying at a downstream service for up to five minutes. That is the trade of not calling the monolith on every request. A monolith restart generates new keys, so every token signed before the restart is rejected until the client refreshes.
> **Captured live:** `200` after one rotation, `401` after two, on the monolith. Calling Delivery's `/track` with the same old token straight after the second rotation still returned `200` because of that cache.

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
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "SELECT \`"Name\`",\`"Status\`" FROM delivery.agents;"
docker exec tadka-delivery-db psql -U tadka -d tadka_delivery -c "SELECT \`"OrderId\`",\`"Attempts\`",\`"CreatedAt\`" FROM delivery.pending_assignments;"
```
**What each command does.** The rider's token (`$RIDER`) is the only one `PATCH .../status` and `PUT .../location` accept besides Admin ([`Tadka.Delivery.Api/Program.cs`](../../src/Tadka.Delivery.Api/Program.cs), `IsAssignedRider`). Marking `Delivered` is what frees the rider ([`DeliveryService.ChangeStatusAsync`](../../src/Tadka.Delivery.Api/DeliveryService.cs)); the two `psql` lines show the riders' availability and any parked orders.
> **Captured live:** `skip pickup` (sending `Delivered` first) returns `422`; `PickedUp` then `Delivered` return `204, 204`; the rider is `Available` again and `pending_assignments` is empty.

To see the parking itself: place four orders on a fresh stack (the three riders take the first three); the fourth appears in `pending_assignments` with the log line `No available rider for order ... parked`. Deliver one of the first three as above, and within `PendingRetrySeconds` the log shows `Waiting order ... finally got rider ...` and the row is gone. **Captured live:** four orders left three riders `OnDelivery` and the fourth in `pending_assignments`; after one `Delivered` the Delivery log printed `Waiting order ... finally got rider Suresh.` and that row left the table. The rider claim is atomic (`UPDATE ... WHERE Status = 'Available'`, run inside the execution strategy so the failover demo's retrying DB strategy can replay it), because a sweeper now assigns at the same time as the Kafka consumer. Assignment is still **first-available**, not nearest.
> Pinned by `PendingAssignmentTests`, `DeliveryServiceTests` and the concurrent-assignment test.

### PII at rest and card tokens (ADR-052, ADR-053)
`identity.users.Phone` is AES-GCM encrypted in the database (random nonce, so it is not searchable) and decrypted transparently for the owner; the card number becomes a **keyed HMAC** token the instant it reaches Payment (`payment.payments` has `CardToken` and `CardLast4`, no PAN column). The key (`Demo:CardTokenizationKey`) is a secret held only by the Payment service: an unkeyed hash of a card number can be brute-forced from a leaked table, because the BIN and last four digits are already known. `Demo:EncryptPiiAtRest=false` turns field encryption off; flipping it on an existing volume fails with `FormatException`, so reset volumes when you change it.
The phone column holds ciphertext ([`FieldCipher`](../../src/Tadka.Api/Infrastructure/Security/FieldCipher.cs): nonce + tag + ciphertext, base64). Orders placed through the order flow carry no card number, so to see a card token, charge one as Admin (`POST /api/v1/payments/charge` is Admin-only, [`Tadka.Payment.Api/Program.cs`](../../src/Tadka.Payment.Api/Program.cs)); [`CardTokenizer`](../../src/Tadka.Payment.Api/Infrastructure/CardTokenizer.cs) turns the number into a keyed token before anything is stored:
```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\", \"Phone\" FROM identity.users LIMIT 3;"     # ciphertext, not phone numbers
OID=$(python -c "import uuid;print(uuid.uuid4())")                                                                        # any new order id
curl -s -X POST http://localhost:5240/api/v1/payments/charge -H "Authorization: Bearer $ADMIN" -H "Content-Type: application/json" -d "{\"orderId\":\"$OID\",\"amount\":299,\"currency\":\"INR\",\"cardNumber\":\"4111 1111 1111 1111\"}"
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"CardToken\",\"CardLast4\" FROM payment.payments WHERE \"CardToken\" IS NOT NULL;"
```
```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \`"Name\`", \`"Phone\`" FROM identity.users LIMIT 3;"
$charge = @{ orderId = [guid]::NewGuid(); amount = 299; currency = "INR"; cardNumber = "4111 1111 1111 1111" } | ConvertTo-Json
Invoke-RestMethod -Uri http://localhost:5240/api/v1/payments/charge -Method Post -Headers @{ Authorization = "Bearer $ADMIN" } -ContentType "application/json" -Body $charge
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \`"CardToken\`",\`"CardLast4\`" FROM payment.payments WHERE \`"CardToken\`" IS NOT NULL;"
```
**Captured live:** phones are base64 ciphertext; the card `4111 1111 1111 1111` is stored as `TOK-B98C07776E30E28A` with last four `1111` (the same card always gives the same token, a different card gives a different one, and there is no column that holds the card number).

### Swap the issuer for a real identity provider (Keycloak, optional)
Because Payment, Delivery and Restaurant verify through the JWKS contract, pointing them at Keycloak is configuration only. Keycloak listens on **host port 8081** here, because the gateway owns 8080.
**What the steps do.** The `auth-prod` profile starts one extra container, Keycloak, with the realm imported from [`infra/keycloak/tadka-realm.json`](../../infra/keycloak/tadka-realm.json) (first start takes about a minute). `.well-known/openid-configuration` is the standard discovery document; its `jwks_uri` is where the public keys live. The token request logs Priya in at Keycloak instead of at the monolith. Then Payment is restarted with the `Keycloak` launch profile ([`Properties/launchSettings.json`](../../src/Tadka.Payment.Api/Properties/launchSettings.json) sets `ASPNETCORE_ENVIRONMENT=Keycloak`, which layers [`appsettings.Keycloak.json`](../../src/Tadka.Payment.Api/appsettings.Keycloak.json) on top): only the issuer, audience and JWKS address change, no code. **Stop the normal Payment first** (Ctrl+C in its terminal), or both fight for port 5240. Use the order id of one of Priya's orders that Payment has charged.
```bash
docker compose --profile auth-prod up -d keycloak
curl -s http://localhost:8081/realms/tadka/.well-known/openid-configuration | grep jwks_uri
KC=$(curl -s -X POST http://localhost:8081/realms/tadka/protocol/openid-connect/token -d "client_id=tadka-api" -d "username=priya@tadka.test" -d "password=Password123!" -d "grant_type=password" | sed -E 's/.*"access_token":"([^"]+)".*/\1/')
dotnet run --project src/Tadka.Payment.Api --launch-profile Keycloak     # Delivery and Restaurant have the same profile name
curl -s -o /dev/null -w "Payment with a Keycloak token: %{http_code}\n" http://localhost:5240/api/v1/payments/$ORDER -H "Authorization: Bearer $KC"
docker compose --profile auth-prod stop keycloak
```
```powershell
docker compose --profile auth-prod up -d keycloak
curl.exe -s http://localhost:8081/realms/tadka/.well-known/openid-configuration | Select-String -Pattern '"jwks_uri":"[^"]*"' -AllMatches | ForEach-Object { $_.Matches.Value }
$KC = (Invoke-RestMethod -Uri http://localhost:8081/realms/tadka/protocol/openid-connect/token -Method Post -Body @{ client_id = "tadka-api"; username = "priya@tadka.test"; password = "Password123!"; grant_type = "password" }).access_token
dotnet run --project src/Tadka.Payment.Api --launch-profile Keycloak
"Payment with a Keycloak token: " + (Get-StatusCode -Uri "http://localhost:5240/api/v1/payments/$ORDER" -Headers @{ Authorization = "Bearer $KC" })
docker compose --profile auth-prod stop keycloak
```
> **Do not use `docker compose --profile auth-prod down` to stop Keycloak.** `down` removes *every* container of the project, so it would take Postgres, Kafka and Redis down with it. `stop keycloak` stops only Keycloak.

**Captured live:** a Keycloak-issued token for Priya returned `200` from Payment on the `Keycloak` profile, and a token from the monolith returned `401` there (its issuer is no longer trusted). Put the normal Payment back afterwards.
The realm (`infra/keycloak/tadka-realm.json`) also defines the three riders (`suresh.rider@`, `lakshmi.rider@`, `imran.rider@tadka.test`) with role `DeliveryAgent`. Full walkthrough: [`docs/learn/keycloak-integration-showcase.md`](../learn/keycloak-integration-showcase.md); the token lifecycle: [`docs/learn/token-and-refresh-flow.md`](../learn/token-and-refresh-flow.md).

> **Run live on this branch** in both shells, section by section, with the five services, Postgres, Redis, Kafka and Keycloak up. The behaviour is also pinned by the test suites: `JwksTests`, `SharedSigningKeyTests`, `RefreshTokenTests`, `RateLimitingTests`, `AuthorizationTests`, `FieldCipherTests` (monolith); `JwksValidationTests`, `PaymentServiceTests`, `CardTokenizerTests` (Payment); `DeliveryOwnershipTests`, `PendingAssignmentTests`, `RealJwtAuthorizationTests` (Delivery); `JwksValidationTests` (Restaurant).

---

## 10. The live Azure walk: commands to run on Day 12 (ADR-064)

The last ten minutes of Day 12 show the same system running on **real Azure**: one public entry, services you cannot reach from the internet, a private database, and a bill running on the meter. This section is the list of commands, in the order you run them. It is for the **instructor only**. Students see the result, not the Terraform.

- **It costs real money.** Planning estimate for a 4-hour `basic` session: about Rs 50 to 150 (an estimate, not a measured bill; the real figure is on Cost Management a day later, see 10.4).
- **You need your own Azure account**, logged in with `az login`. First time ever? Do [`azure-getting-started.md`](azure-getting-started.md) first, then come back here.
- The `cloud-*.ps1` scripts are **PowerShell**. From Git Bash, run them as `powershell.exe -NoProfile -File ./scripts/<name>.ps1 <arguments>`. The `az`, `terraform` and `curl` commands below work in both shells, and each block shows both.

### 10.1 Which variant do you have?

| Your subscription | `cloud-up` flag | What you can show |
|---|---|---|
| Pay-as-you-go | none (Front Door is on) | all five beats below, including the CDN cache hit |
| **Free Trial or Student** | **`-NoFrontDoor`** | beats 2 to 5. Beat 1 (the CDN hit) cannot be shown: Azure refuses Front Door on these subscriptions (`Free Trial and Student account is forbidden for Azure Frontdoor resources`). The gateway URL is the public entry point. |

### 10.2 Before class: start 45 minutes ahead

**Step 1. Check who you are and the alert email.** The email must be a real address with an `@`; `cloud-up` stops at once if it is not. A window that was open when you ran `setx` keeps the old value, so use a new window.
```powershell
az account show --query "{subscription:name, user:user.name}" -o table
$env:TADKA_ALERT_EMAIL
```
```bash
az account show --query "{subscription:name, user:user.name}" -o table
echo "$TADKA_ALERT_EMAIL"
```

**Step 2. Bring the session up.** From the repository root. Pass the email explicitly so a stale environment variable cannot interfere. `-AutoDownAfterHours` registers a backstop that deletes everything at that time if you forget; pick a time well after class ends.
```powershell
./scripts/cloud-up.ps1 -Mode basic -AlertEmail you@example.com -AutoDownAfterHours 6
./scripts/cloud-up.ps1 -Mode basic -AlertEmail you@example.com -AutoDownAfterHours 6 -NoFrontDoor   # Free Trial or Student
```
```bash
powershell.exe -NoProfile -File ./scripts/cloud-up.ps1 -Mode basic -AlertEmail you@example.com -AutoDownAfterHours 6 -NoFrontDoor
```
Leave the window alone until it prints **`SMOKE OK`** and **`Tadka is live (basic)`**. Allow 15 to 25 minutes from nothing (the planning figure; the Container Apps environment alone took 4 minutes 49 seconds when measured). A re-run that only has to finish a few resources took about 2 minutes. If it stops partway, read the red error above `terraform apply failed`, fix that, and **run the same command again**; it carries on from where it stopped (see Troubleshooting in [`cloud-deploy.md`](cloud-deploy.md)).

**Step 3. Check that everything works.** Open a **new** PowerShell window in the **same clone** you ran `cloud-up` from (it reads the gateway address from that clone's Terraform state). About a minute.
```powershell
./scripts/cloud-check.ps1
```
```bash
powershell.exe -NoProfile -File ./scripts/cloud-check.ps1
```
Every line should be `PASS`. On a `-NoFrontDoor` session the Front Door line is `SKIP`, and the log lines may `SKIP` if Azure's log command fails; neither is a problem. It exits with code 1 if anything `FAIL`ed.

**Step 4. Free the riders.** Only **three riders** are seeded, and an order keeps its rider until it is delivered. `cloud-up`'s smoke test and every demo order you place leave one rider busy, so after a few runs new orders wait for a rider and the live demo stalls. This delivers every open order of the demo customer and puts the riders back. Run it after any re-run of `cloud-up`, and again just before class:
```powershell
./scripts/cloud-check.ps1 -FreeRiders
```
```bash
powershell.exe -NoProfile -File ./scripts/cloud-check.ps1 -FreeRiders
```
A second run prints `nothing to free`.

**Step 5. Prepare a token and an order** for the live-tracking beat, so you are not typing during class. This order keeps one rider busy until you run Step 4 again.
```powershell
$gw = terraform -chdir=deploy/azure output -raw gateway_url
$fd = terraform -chdir=deploy/azure output -raw front_door_url      # empty on a -NoFrontDoor session
$BODY = '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":2}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}'
$TOKEN = (Invoke-RestMethod "$gw/api/v1/auth/login" -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}').accessToken
$ORDER = (Invoke-RestMethod "$gw/api/v1/orders" -Method Post -Headers @{Authorization="Bearer $TOKEN"} -ContentType "application/json" -Body $BODY).id
$ORDER
```
```bash
gw=$(terraform -chdir=deploy/azure output -raw gateway_url)
fd=$(terraform -chdir=deploy/azure output -raw front_door_url)     # empty on a -NoFrontDoor session
BODY='{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":2}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}'
TOKEN=$(curl -s -X POST $gw/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
ORDER=$(curl -s -X POST $gw/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
echo "$ORDER"
```

**Step 6. Open three tabs:** the public URL in a browser (`$fd`, or `$gw` on a `-NoFrontDoor` session), the Azure portal on the resource group `rg-tadka-session`, and [`docs/diagrams/day-12-azure-deployment.md`](../diagrams/day-12-azure-deployment.md). Note the time: the bill starts now.

### 10.3 In class: the five beats

**Beat 1. The CDN hit (2 minutes). Front Door sessions only.** The first request is a cache miss and the second is served by the edge, so it never reaches Tadka. This is Day 6's menu cache, moved out of your laptop onto a server near the user. On a `-NoFrontDoor` session skip this beat in one sentence and go to beat 2.
```powershell
curl.exe -sI "$fd/api/v1/restaurants" | findstr /i x-cache
curl.exe -sI "$fd/api/v1/restaurants" | findstr /i x-cache
```
```bash
curl -sI "$fd/api/v1/restaurants" | grep -i x-cache
curl -sI "$fd/api/v1/restaurants" | grep -i x-cache
```
Expect `TCP_MISS`, then `TCP_HIT`. **Not run live yet** (it needs Front Door, which a Free Trial cannot create).

**Beat 2. Match every box to the Compose file you know (2 minutes).** In the portal, open the resource group and point at each box. These commands show the same thing in the terminal:
```powershell
az resource list -g rg-tadka-session -o table
$pg = terraform -chdir=deploy/azure output -raw postgres_server
az postgres flexible-server show -g rg-tadka-session -n $pg --query "{server:name, publicAccess:network.publicNetworkAccess, sku:sku.name, version:version}" -o json
az postgres flexible-server db list -g rg-tadka-session -s $pg --query "[].name" -o tsv
```
```bash
az resource list -g rg-tadka-session -o table
pg=$(terraform -chdir=deploy/azure output -raw postgres_server)
az postgres flexible-server show -g rg-tadka-session -n $pg --query "{server:name, publicAccess:network.publicNetworkAccess, sku:sku.name, version:version}" -o json
az postgres flexible-server db list -g rg-tadka-session -s $pg --query "[].name" -o tsv
```
What to point at:
- The four local database containers are **one** Flexible Server with four databases: `tadka`, `tadka_payment`, `tadka_delivery`, `tadka_restaurant` (plus Azure's own system databases in the list). Four servers would be four bills; the isolation here is logical, not physical.
- **`publicAccess: Disabled`.** The database has no public address. It sits in a private subnet and only the apps can reach it (Day 10: the data tier is never public).
- `kafka` and `redis` are container apps. `Tadka.Gateway` is the `gateway` app. The four services are four more apps. There is **no separate load balancer box**: the Container Apps ingress is the load balancer.
- The region is Central India. Phone numbers and addresses are personal data (Day 10), so keeping them in India is the safe default.

Ask the room: "where is the load balancer in this list?" It exists, but you cannot see it. On AWS it would be a separate ALB with its own bill.

**Beat 3. Count the hops (2 minutes).** Draw it on the board before showing anything, because response headers will not show it: the response carries only `server: Kestrel`.

| Path | Boxes | Hops |
|---|---|---|
| With Front Door | client, Front Door, Envoy (the Container Apps ingress), gateway, **Envoy**, restaurant, Postgres | 7 boxes, **6 hops** |
| `-NoFrontDoor` | client, Envoy, gateway, **Envoy**, restaurant, Postgres | 6 boxes, **5 hops** |

A hop is an arrow, not a box. People count the boxes they can see and miss the second Envoy: on Container Apps a service-to-service call also goes through the platform's proxy. Every hop adds a little latency, which is the reason the order's hot path never calls Restaurant (the local read model from Demo 1). You can show what the first hop alone costs:
```powershell
curl.exe -s -o NUL -w "dns %{time_namelookup}s  connect %{time_connect}s  tls %{time_appconnect}s  first-byte %{time_starttransfer}s  total %{time_total}s\n" "$gw/api/v1/restaurants"
```
```bash
curl -s -o /dev/null -w "dns %{time_namelookup}s  connect %{time_connect}s  tls %{time_appconnect}s  first-byte %{time_starttransfer}s  total %{time_total}s\n" "$gw/api/v1/restaurants"
```
Measured from a laptop in India, twice: total 0.09 s and 0.13 s for a warm read, of which the connection and TLS setup are most. Your number will differ.

**Beat 4. Only the gateway is public, and live tracking goes around the CDN (3 minutes).**

4a. Which apps have a public address:
```powershell
az containerapp list -g rg-tadka-session --query "[].{app:name, public:properties.configuration.ingress.external}" -o table
```
```bash
az containerapp list -g rg-tadka-session --query "[].{app:name, public:properties.configuration.ingress.external}" -o table
```
Only `gateway` says `True`. The other seven say `False`.

4b. Payment has no public door, and even through the gateway it asks for its own token (the gateway is not the trust boundary, Day 11). Expect `401`:
```powershell
curl.exe -s -o NUL -w "%{http_code}\n" "$gw/api/v1/payments/charge" -X POST -H "Content-Type: application/json" -d "{}"
```
```bash
curl -s -o /dev/null -w "%{http_code}\n" "$gw/api/v1/payments/charge" -X POST -H "Content-Type: application/json" -d '{}'
```

4c. Live tracking. The first events arrive at once (the order's current status), then the cursor waits with the connection still open. Press Ctrl+C to stop.
```powershell
curl.exe -N -H "Authorization: Bearer $TOKEN" "$gw/api/v1/orders/$ORDER/events"
```
```bash
curl -N -H "Authorization: Bearer $TOKEN" "$gw/api/v1/orders/$ORDER/events"
```
On a Front Door session this goes to the **gateway** URL on purpose, not the Front Door URL. A CDN is built for short responses that can be cached, and it cuts a response that stays open for minutes, so the realtime path skips it. A common pattern in large apps: a separate hostname for realtime, outside the CDN. On a Front Door session the gateway URL answers `403` to everything except health checks and this stream (the origin lock); **not run live yet**.

**Beat 5. The bill (1 minute).** No command. Ask the room to guess what today's class will cost, and write two or three guesses on the board. All of this is on the meter right now. The real figure is on Cost Management about a day later, and the Day 16 session compares it with their guesses.

### 10.4 After class

```powershell
./scripts/cloud-down.ps1
```
```bash
powershell.exe -NoProfile -File ./scripts/cloud-down.ps1
```
Wait for **`Resource group rg-tadka-session is GONE`**. Measured: **24.5 minutes**, so do not close the window early. If `terraform destroy` fails, run it again with `-Force`, which deletes the resource group directly. Then confirm it yourself, because this bills by the hour:
```powershell
az group exists --name rg-tadka-session
```
```bash
az group exists --name rg-tadka-session
```
It must print `false`; also glance at the portal. The next day, open Cost Management for that group and write the real figure into [`docs/cost-model.md`](../cost-model.md).

### 10.5 If the cloud is not up when class starts

Do not debug Azure in front of the room. Show the diagram ([`day-12-azure-deployment.md`](../diagrams/day-12-azure-deployment.md)) and the decision in ADR-064, say the live run is on the next session, and run `cloud-down.ps1` afterwards so a half-built environment does not bill overnight.

### 10.6 What has and has not been run live

**Run live in PowerShell** on an Azure Free Trial subscription with `-NoFrontDoor`: `cloud-up`, `cloud-check` (31 or 32 PASS, depending on whether the log checks could read the logs), `cloud-check -FreeRiders`, the token and order setup, beat 2's `az` commands, beat 3's timing command, beats 4a to 4c, and `cloud-down` (24.5 minutes).

**Run live in Git Bash:** the token and order setup, the Payment `401`, the SSE stream, and `cloud-check` through `powershell.exe -NoProfile -File`. The `az`, `terraform` and `cloud-up` / `cloud-down` commands in the bash blocks were not run from Git Bash; they are the same programs and arguments as the PowerShell versions.

**Not run live yet:** everything that needs Front Door (beat 1, the origin lock, the WAF rate limit), `-Mode ha`, and whether Application Insights receives telemetry from every service.
---

## 11. Run the tests
```
dotnet test
```
Run from the repository root, `dotnet test` finds [`Tadka.slnx`](../../Tadka.slnx) and runs the five test projects under [`tests/`](../../tests): the monolith's, Payment's, Delivery's, Restaurant's and the gateway's. Many of them start real Postgres and Kafka containers with Testcontainers, so Docker must be running, and they use their own throwaway containers (they do not touch the stack you started above). To run just one area, add a filter, for example `dotnet test tests/Tadka.Restaurant.Api.Tests` or `dotnet test --filter "FullyQualifiedName~ExpandContract"`.
**183/183** on this branch at the time of writing: monolith 95, Payment 31, Delivery 23, Restaurant 19, and a `Tadka.Gateway.Tests` project (15) that no earlier count in this file included. That includes 16 Kafka authentication tests (4 per service, no broker needed), and the real-Kafka integration tests (Testcontainers) that run against an unauthenticated broker: their fixtures blank `Kafka:SaslUsername`, because the test host runs in the `Development` environment and would otherwise inherit the compose broker's credentials. `day-12` stays fast-forwarded to `main`, so **this number will move again**: `dotnet test` is the source of truth, not a number in a doc. The integration tests use Testcontainers, so Docker must be running.

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
- **In Service decision mode the rider is held until the refund settles.** Ordering publishes `order-confirmed` to Restaurant and Delivery at the same moment, so Delivery assigns a rider before the restaurant answers. If the answer is `Rejected`, the rider is released when `payment-refunded` arrives ([`PaymentRefundedConsumer`](../../src/Tadka.Delivery.Api/Messaging/PaymentRefundedConsumer.cs) calls `DeliveryService.CancelOrderAsync`), a few seconds later (the length of the refund chain), not at the moment of rejection. A production system would hold the rider back until the restaurant says `Accepted`.
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
- [ ] The backfill completes (**5016 rows, 6 batches**) with replica lag in single-digit milliseconds and no lock; two workers (`-Workers 2`) copy 2,481 + 2,535 rows, and the replica count equals the source count.
- [ ] A broken `menu-updated` message appears on `menu-updated.dlq` after 3 attempts.
- [ ] Service mode: order **Cancelled**, payment **Refunded** (`/api/v1/payments/{orderId}`).
- [ ] Dual-write PATCH fills both `Name` and `DisplayName`; `expand-contract-demo.ps1` ends with `remaining NULL DisplayName: 0` (and stops on its own).
- [ ] SSE: a non-owner gets **403**; a 4th concurrent stream for one user gets **429**.
- [ ] The replica-lag tests pass (**5/5**).
- [ ] The gateway routes `/api/v1/restaurants/**` (**200**).
- [ ] Rahul reading Priya's payment or tracking her order gets **403**; Priya gets **200**; a customer POSTing `/api/v1/payments/charge` gets **403**.
- [ ] `/.well-known/jwks.json` lists the public key; `rotate-signing-key` (Admin) adds a new one and the old token still verifies once (**200**).
- [ ] The rider on an order can `PATCH` it `PickedUp` then `Delivered` (**204**, **204**) and is `Available` again; a fourth order on a fresh stack is parked in `pending_assignments` and assigned once a rider frees up.
- [ ] `dotnet test` is green (**183/183** at the time of writing).
- [ ] A Kafka command with no credentials hangs; with `--command-config /etc/kafka/docker/client.properties` it answers.

## Troubleshooting
- **Docker commands fail with `C:/Program Files/Git/...: no such file` in Git Bash:** run `export MSYS_NO_PATHCONV=1` first (see the note at the top).
- **Section 9.1 hangs waiting for a rider:** all three riders are busy from earlier demos. Run the "free the riders" step at the start of 9.1.
- **`docker compose down` took my databases and Kafka away:** `down` removes every container of the project, including with `--profile`. Use `docker compose stop <service>` to stop one.
- **PowerShell prints `syntax error` or `unterminated quoted identifier` from `psql`:** a quoted column name lost its quotes on the way to the native program. Write it as `\`"Name\`"` inside a double-quoted PowerShell string, as in Demo 2.
- **The `Keycloak` launch profile fails with "address already in use":** the normal Payment is still running on 5240. Stop it first.
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
