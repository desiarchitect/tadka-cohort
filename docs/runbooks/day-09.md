# Day 9: Kafka, Outbox, Inbox, and Saga

Branch: `day-09`. What changed since Day 8: see [`docs/changelog.md`](../changelog.md).

Day 8 used a synchronous HTTP call from the monolith to Payment. Today that call is replaced with Kafka. Order creation writes an `order-placed` row into a transactional Outbox table, committed in the same transaction as the order (ADR-028). A background `OutboxRelay` reads that table and publishes to Kafka. The Payment service consumes `order-placed`, charges the customer, and publishes `payment-results`. The monolith consumes that and updates the order. This is a Saga using choreography (ADR-029): no service tells another what to do, each service reacts to events on its own.

If the Payment service is down, the `order-placed` message just waits in Kafka. Nothing is lost. Consumers are idempotent, so a message delivered twice never charges twice.

New infrastructure: Kafka (port 9092) and Kafka UI (port 8090).

New here? Read [`README.md`](README.md) first. You will run two apps plus Kafka. Kafka is off when `Kafka:BootstrapServers` is unset, so `dotnet test` does not need a broker running.

Every command below is given twice, bash first and PowerShell second. These are not the same commands with `curl` swapped for `curl.exe`. Bash's `VAR=value`, `$(...)`, `| sed`, and `for`/`until` loops do not run in PowerShell at all. Use Git Bash for the bash blocks and Windows PowerShell for the PowerShell blocks. Both were run against this branch before being written here.

## 1. Start everything

Wipe old state and start fresh. Run this at the start of the day, or any time you want a clean slate. It removes old orders, old Kafka topics, and old consumer group offsets, not just stops the containers:

```bash
git checkout day-09
docker compose down -v
docker compose up -d
```
```powershell
git checkout day-09
docker compose down -v
docker compose up -d
```

Wait for Kafka to be healthy before doing anything else. Kafka takes longer to start than Postgres or Redis, and `docker compose ps` alone does not wait for it:

```bash
until docker inspect tadka-kafka --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
```
```powershell
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-kafka --format "{{.State.Health.Status}}") -eq "healthy")
```

Create the three Kafka topics before starting either app. On a fresh broker, no topics exist yet. Each app subscribes to its topic the moment it starts. If one app starts before the other has published anything, you will see this printed once a second: `Confluent.Kafka.ConsumeException: Subscribed topic not available: <topic>: Broker: Unknown topic or partition`. This is not a crash. The app keeps retrying and picks up the topic as soon as it exists. It looks alarming on a first run, so create the topics up front instead:

```bash
for t in order-placed payment-results order-placed.dlq; do
  docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
done
```
```powershell
foreach ($t in "order-placed","payment-results","order-placed.dlq") {
  docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
}
```

Check the topics were created. Open <http://localhost:8090> in a browser. Click the `tadka` cluster, then click **Topics** in the left menu. You should see `order-placed`, `order-placed.dlq`, and `payment-results`, each with 0 messages. Zero is correct at this point, since no order has been placed yet. Click a topic name to see its own tabs: Overview, Messages, Consumers, Settings, Statistics, ACLs. Messages shows what was actually published. Consumers shows that topic's consumer groups.

Start both apps, in two terminals, in either order:

```bash
dotnet run --project src/Tadka.Payment.Api    # :5240, consumes order-placed
dotnet run --project src/Tadka.Api            # :5224, relays the outbox and consumes payment-results
```

Wait 15 to 20 seconds after each app prints "Application started" before running any command against it. Startup includes JIT warmup, an EF migration check, and joining a Kafka consumer group, and none of that is instant. Every restart later in this runbook (sections 3, 5, and 8) needs the same wait.

Once both apps are up, go back to Kafka UI and click **Consumers** in the top-level left menu, not a topic's own Consumers tab: <http://localhost:8090/ui/clusters/tadka/consumer-groups>. You will see two groups, `tadka-monolith` and `tadka-payment`, each with a live Consumer Lag number and a State column that reads STABLE once settled. This is the same information the `kafka-consumer-groups.sh --describe` commands below show, without typing anything. Leave this tab open. It updates on its own, and it is the easiest way to watch lag change in section 3.

This is the order body used through the rest of this runbook, same as Day 7 and Day 8:

```bash
RID=a1b2c3d4-0001-4000-8000-000000000001; ITEM=b1b2c3d4-0001-4000-8000-000000000001; CID=c1b2c3d4-0001-4000-8000-000000000001
BODY='{"customerId":"'$CID'","restaurantId":"'$RID'","items":[{"menuItemId":"'$ITEM'","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}'
```
```powershell
$RID = "a1b2c3d4-0001-4000-8000-000000000001"
$ITEM = "b1b2c3d4-0001-4000-8000-000000000001"
$CID = "c1b2c3d4-0001-4000-8000-000000000001"
$BODY = @{
  customerId = $CID
  restaurantId = $RID
  items = @(@{ menuItemId = $ITEM; quantity = 1 })
  deliveryAddress = @{ line1="x"; line2="y"; city="Bangalore"; pincode="560066"; latitude=12.9; longitude=77.7 }
} | ConvertTo-Json -Depth 5
```

In every bash block below that uses `curl ... | sed ...` to place an order and read its id, the PowerShell equivalent is:
```powershell
$order = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -ContentType "application/json" -Body $BODY).id
```
And wherever a bash block checks status with `curl .../orders/$ORDER | sed ...`:
```powershell
(Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders/$order").status
```
`Invoke-RestMethod` parses JSON directly, so it is simpler here than the bash curl-plus-sed pattern.

## 2. What we are showing: an order settles across Kafka

With both services and Kafka running, placing an order still feels instant. `POST /orders` returns in milliseconds, same as Day 7 and Day 8. Everything after that response now travels through Kafka instead of an in-process queue or a direct HTTP call.

```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 2
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Confirmed
curl -s http://localhost:5240/payments/$ORDER                                                        # status: Completed
```
```powershell
$order = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -ContentType "application/json" -Body $BODY).id
Start-Sleep -Seconds 2
(Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders/$order").status         # Confirmed
Invoke-RestMethod -Uri "http://localhost:5240/payments/$order"                       # status: Completed
```

The `sleep 2` is there because this is now a multi-hop flow. The order commits, the outbox relay notices and publishes it, Payment consumes it and charges, then the monolith consumes the result. Each hop adds a small delay a direct call did not have. Measured live on this branch, in steady state: `POST /orders` returns in about 150 to 180 ms, and the order settles to Confirmed roughly 600 ms to 1.1 s after that. The 2-second sleep is a comfortable margin, and you will usually see Confirmed sooner.

Full flow: `POST /orders` commits an order row and an outbox row in one transaction. `OutboxRelay` reads the outbox row and publishes it to Kafka's `order-placed` topic. Payment consumes it, charges, and publishes to `payment-results`. The monolith consumes that and confirms the order.

You can see this directly in the database. The `Topic` column tells you which outbox row this is, and `ProcessedAt IS NOT NULL` (shown as `sent`) tells you whether the relay has pushed it to Kafka yet:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Topic\", \"ProcessedAt\" IS NOT NULL AS sent FROM ordering.outbox_messages ORDER BY \"CreatedAt\" DESC LIMIT 3;"
```
```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \`"Topic\`", \`"ProcessedAt\`" IS NOT NULL AS sent FROM ordering.outbox_messages ORDER BY \`"CreatedAt\`" DESC LIMIT 3;"
```

Every `psql` command in this runbook needs double-quoted column names, because the schema is case-sensitive EF Core generated SQL. In bash, `\"` works fine. In PowerShell, plain `\"` gets stripped before it reaches `docker.exe`, and the query fails with "column does not exist". The sequence that survives is a backslash followed by a backtick-quote, written above as `` \`" ``. Every PowerShell `psql` command in this runbook uses it.

`sent=true` on your order's row proves the outbox actually reached Kafka, not just that the order committed. If you see `sent=false` right after placing the order, wait a few seconds. The relay polls on an interval, so this is not instant. If it stays `false` for more than a few seconds, the relay is not running or Kafka is not reachable.

One more detail worth knowing (ADR-028): the relay claims a batch of unsent rows with `SELECT ... FOR UPDATE SKIP LOCKED`, and holds that row lock open for as long as the Kafka publish takes inside the same transaction. A slow or unreachable broker holds the lock, not just delays the publish. The producer sets `MessageTimeoutMs` and `RequestTimeoutMs` to 10 seconds to bound this window, instead of `librdkafka`'s default of 300 seconds. This is a configured value, not something measured live, but it explains why a Kafka outage should hold locks for single-digit seconds, not five minutes.

## 3. The headline: a stopped consumer does not lose the message

This is the beat the whole day is built around.

On Day 8, if Payment was down when an order was placed, the synchronous HTTP call failed and the charge was gone for good. The work item was taken off an in-memory queue and there was no way to get it back.

Today, stop Payment and place an order. It will not be lost. It will wait.

Stop the Payment service (Ctrl+C in its terminal, or kill whatever is listening on port 5240). Then place an order the same way as before:

```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 3
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Created, not lost
```
```powershell
$order = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -ContentType "application/json" -Body $BODY).id
Start-Sleep -Seconds 3
(Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders/$order").status   # Created, not lost
```

The order stays `Created`. Not an error, not a dropped request, and never a 500. `POST /orders` still returned in milliseconds, because the monolith only had to write the order row and the outbox row in one local transaction. It never needed Payment to be reachable. The order is pending because nobody has consumed its `order-placed` event yet.

Check the consumer group lag directly. The message is sitting in Kafka, unconsumed. This command has no bash-specific syntax, so it is the same in either shell:

```
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group tadka-payment
# order-placed  LAG 1
```

`LOG-END-OFFSET` is how many messages have ever been published to that partition. `CURRENT-OFFSET` is how far the `tadka-payment` group has actually committed past. `LAG` is the difference. LAG 1 here means one message is published but not yet consumed. In a real system this is the number an on-call engineer watches, since it tells you how much work is queued up and unprocessed.

Now restart Payment. It reconnects to Kafka, rejoins the `tadka-payment` group, and resumes from its last committed offset. It does not need to be told what it missed, since the broker already knows:

```
dotnet run --project src/Tadka.Payment.Api
```

Wait 15 to 20 seconds after "Application started" before checking anything. A consumer group rebalance (the group going from one member to zero to one member again) takes several seconds on top of the app's own startup. Checking too early just shows a stale, still-settling state, then check the order status and the lag:

```bash
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Confirmed
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group tadka-payment   # LAG 0
```
```powershell
(Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders/$order").status   # Confirmed
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group tadka-payment   # LAG 0
```

If you still see `Created`, or the describe output says the group is rebalancing, wait another 5 to 10 seconds and check again. That is not a failure. It settled within 20 seconds every time this was tested live.

What happened: pending with LAG 1 while Payment was down, then Confirmed with LAG 0 after restart. On Day 8 the same outage lost the charge and stranded the order permanently. That is the entire value of putting Kafka and the Outbox between the two services. Nothing about the order-placement code changed, only the transport did, and the outage became recoverable instead of destructive.

## 4. A message can arrive twice, and that is safe

Kafka guarantees at-least-once delivery, which means a message can be delivered more than once. A consumer crash between processing a message and committing its offset is the classic case. For a payment, charging twice is a real-money bug, not a cosmetic one.

The rule from today onward: a consumer checks the inbox, does the side effect, stamps the inbox, then commits the Kafka offset, in that order. Never stamp the inbox before doing the work, because a crash in between would mark the message done with no charge and no confirmation. Both Payment's charge path and the monolith's `payment-results` path follow this rule.

Day 12 extracts Restaurant and adds more topics. The inbox order above does not change. See [`DAY-EVOLUTION.md`](DAY-EVOLUTION.md) for the full branch map.

A redelivered `order-placed` message does not double-charge. The Inbox table skips a message id it has already seen, and a unique index on `payment.payments(order_id)` is the hard guard underneath it. Even if the inbox check were somehow bypassed, the database itself would reject a second row for the same order:

```bash
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\", count(*) FROM payment.payments GROUP BY \"OrderId\" HAVING count(*) > 1;"   # 0 rows
```
```powershell
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \`"OrderId\`", count(*) FROM payment.payments GROUP BY \`"OrderId\`" HAVING count(*) > 1;"   # 0 rows
```

Zero rows means `GROUP BY OrderId` never found a duplicate, across every order placed so far. This is the same query you would run against production traffic, and it does not require you to stage a redelivery yourself.

A deterministic version of this proof lives in `Tadka.Payment.Api.Tests`: charging the same order twice produces one payment row with the same reference.

## 5. A declined payment cancels the order

Sections 2 and 3 both end in Confirmed. But payments fail for real reasons, a declined card or an expired one, and there is no shared database transaction between Order and Payment to roll back automatically anymore. Payment publishes a Failed result, and the monolith reacts to that event by cancelling the order. This is a Saga: no central coordinator, each side reacts to what the other publishes.

Restart Payment with the fake gateway set to decline every charge. Wait the usual 15 to 20 seconds for the consumer group to rejoin before doing anything else:

```bash
Payment__Gateway__Behavior=Failing dotnet run --project src/Tadka.Payment.Api
```
```powershell
$env:Payment__Gateway__Behavior="Failing"; dotnet run --project src/Tadka.Payment.Api
```

Place an order against the declining gateway:

```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 10
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Cancelled
```
```powershell
$order = (Invoke-RestMethod -Uri http://localhost:5224/api/v1/orders -Method Post -ContentType "application/json" -Body $BODY).id
Start-Sleep -Seconds 10
(Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders/$order").status   # Cancelled
```

If it still shows `Created`, wait a few more seconds and check again, same settling behaviour as every earlier section.

`Cancelled` here is correct, expected behaviour, not an error. Day 7 cancelled the order in-process. Day 8 cancelled it through a synchronous HTTP response. Today it cancels through an asynchronous event the monolith consumes on its own. The rule, a decline cancels the order, has not changed across three rewrites. Only the mechanism carrying the news has.

Reset the gateway to normal before continuing. Every section from here on assumes a healthy gateway that confirms orders. Skip this step and later orders keep coming back Cancelled, which looks like a new bug when it is really just this switch left on. Stop Payment and restart it plain, no `Behavior` variable set, same wait as always:

```
dotnet run --project src/Tadka.Payment.Api
```

## 6. The service boundary still holds, and the tests still pass

The Payment extraction from Day 8 did not just survive today's rewrite, it is what made today's rewrite a drop-in change instead of a redesign. Ordering never had a compile-time reference to Payment, so swapping the transport underneath did not touch Ordering's code shape at all.

Confirm the boundary is still clean. Exclude `Migrations/` and comment lines, or you will see false hits from frozen pre-extraction EF migration snapshots and one explanatory comment, neither of which is a real dependency:

```bash
grep -rn "FakePaymentGateway\|PaymentDbContext\|Domain.Payments\|IPaymentClient" src/Tadka.Api --exclude-dir=Migrations | grep -v '^\S*:\s*//'    # nothing
```
`grep` is not a native PowerShell command. `-Exclude` on `Get-ChildItem` only filters filenames, not directories, so excluding `Migrations/` needs a path filter instead:
```powershell
Get-ChildItem -Path src\Tadka.Api -Recurse -Include *.cs | Where-Object { $_.FullName -notmatch '\\Migrations\\' } | Select-String -Pattern "FakePaymentGateway|PaymentDbContext|Domain.Payments|IPaymentClient" | Where-Object { $_.Line -notmatch '^\s*//' }   # nothing
```

No hits means Ordering has zero compile-time knowledge of Payment's gateway, database, or the `IPaymentClient` interface Day 8 introduced. That interface is gone entirely, replaced by publishing an event and reacting to another one.

Run the full test suite:

```
dotnet test    # 34/34: monolith 25 (3 of those architecture/boundary), Payment 9 (5 of those PoisonMessageTracker). Kafka is off in tests.
```

The count grew from Day 8's 25/25 to 34/34. The architecture/boundary tests now assert against Kafka-based messaging instead of `IPaymentClient`, and the new `PoisonMessageTracker` tests (section 7) are unit tests with no live Kafka dependency. `dotnet test` must stay green with no broker running, which is why Kafka is off by default in the test environment.

## 7. A message that cannot be processed at all

This section is optional and can be skipped in a live class. Section 3 is the one that must not be cut. This is weekday lab material, also covered in `break-kit-day-09.md` Beat 6.

Sections 3 and 4 cover a consumer that was down, and a message delivered twice. Neither covers a third case: a message the consumer picks up but cannot process at all, malformed JSON, or a business exception on every attempt.

`Consumer.Consume()` moves the fetch position forward on every call, whether or not you committed. A plain manual-commit loop that logs an error and moves on does not retry that message. It silently and permanently skips it the moment a later message's offset commits. This was checked live on this codebase: a poison message failed once, a healthy order placed right after committed an offset past both messages, and the group's lag went straight back to 0, with the poisoned order stuck at Created forever and nothing logging that it had been abandoned.

Inject a malformed message directly onto the `order-placed` topic. These `.ps1` scripts run through PowerShell either way. From a PowerShell prompt, run them directly. From Git Bash, run them through `powershell.exe -File`. Neither path needs PowerShell 7:

```bash
powershell.exe -File scripts/inject-poison.ps1 -Mode Malformed
```
```powershell
.\scripts\inject-poison.ps1 -Mode Malformed
```

Watch the lag while the consumer retries. A small function saves retyping the describe command:

```bash
LAG() { docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group "$1"; }
LAG tadka-payment | grep order-placed
```
```powershell
function LAG($group) { docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group $group }
LAG tadka-payment | Select-String order-placed
```

LAG 1 here does not mean the same thing it meant in section 3. There it meant nobody was consuming yet. Here it means the consumer is stuck retrying the same offset.

The fix is `PoisonMessageTracker` (ADR-051). On a handler exception, the consumer calls `Seek()` back to the failed offset, forcing real redelivery instead of the silent skip described above, up to 3 attempts, with a fixed 300 ms gap between each. Three attempts land in roughly 900 ms total, a number derived from that configured constant, not a live measurement. On the third failure, it publishes the original, unmodified payload plus the error text to `order-placed.dlq`, then commits the original offset. That commit is what finally drops the lag back to 0, but 0 now means quarantined, not processed.

Read the DLQ to confirm the poisoned payload landed there intact:

```
docker exec tadka-kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic order-placed.dlq --from-beginning --timeout-ms 5000
```

Payment's log is the other half of the proof. It should show "will retry" twice, then "failed 3x, routing to DLQ, partition unblocked". Place a healthy order right after, using the same order-placement pattern from section 2, and confirm it settles normally. This is what proves the partition itself is unblocked, not just that the poison message stopped erroring.

There is a second, sharper failure mode. A DLQ only catches messages that throw an exception. A message that deserializes fine but silently drops a renamed field throws nothing and never reaches the DLQ:

```bash
powershell.exe -File scripts/inject-poison.ps1 -Mode SafeExtraField          # an extra unknown field, processes fine
powershell.exe -File scripts/inject-poison.ps1 -Mode MissingRequiredField    # Currency renamed to CurrencyCode, also processes fine, no error, no DLQ entry
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\", currency FROM payment.payments ORDER BY \"CreatedAt\" DESC LIMIT 1;"   # currency = INR
```
```powershell
.\scripts\inject-poison.ps1 -Mode SafeExtraField
.\scripts\inject-poison.ps1 -Mode MissingRequiredField
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \`"OrderId\`", currency FROM payment.payments ORDER BY \`"CreatedAt\`" DESC LIMIT 1;"   # currency = INR
```

`SafeExtraField` processing cleanly is expected. `System.Text.Json` ignores unmapped fields by default, which is correct forward-compatible behaviour for an additive change. `MissingRequiredField` processing just as cleanly is the actual lesson. The missing constructor parameter bound to null, and a Postgres column default filled it in on insert. The payment succeeded with a currency nobody specified, and nothing surfaced that anything was wrong: not the consumer, not the database, not a log line. Never rename or remove a field on a shared event, only ever add one. This is a code-review rule, since no runtime mechanism in this runbook, DLQ included, can catch a rename for you.

Once the root cause is fixed, replay the DLQ. Every quarantined message gets republished to `order-placed` and reprocessed from scratch:

```bash
powershell.exe -File scripts/replay-dlq.ps1
```
```powershell
.\scripts\replay-dlq.ps1
```

A message that is still genuinely broken fails its 3 attempts again and lands back on the DLQ. That is correct behaviour, not a bug in the replay script.

## 8. Replay the entire topic from the start

Section 4 proved the inbox stops one redelivered message from double-charging. This section is the stress-test version of the same claim: replay the entire topic from offset 0, every message ever published, and confirm the payments table does not grow by a single row.

Stop Payment, then reset the group's offset back to the beginning of the topic. A still-active consumer group registration refuses an offset reset, and it takes longer than you would guess to go inactive. This is the client library's session timeout, not the broker being slow. `Confluent.Kafka`'s default session timeout is 45 seconds. Checked live on this branch, the reset command failed on retries at 10 seconds and 22 seconds elapsed, before finally succeeding around a 60 to 70 second total wait. Poll instead of guessing a fixed sleep.

One more thing worth knowing, also checked live: `kafka-consumer-groups.sh --reset-offsets` always exits 0, even when it prints an error and does nothing. A loop that only checks the exit code reports success on the first try, wrongly. Check the actual output text instead:

```bash
until docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --group tadka-payment --topic order-placed --reset-offsets --to-earliest --execute 2>&1 | grep -q "^tadka-payment"; do
  echo "group still active, retrying in 5s..."; sleep 5
done
```
```powershell
do {
  $out = docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --group tadka-payment --topic order-placed --reset-offsets --to-earliest --execute 2>&1 | Out-String
  $ok = ($out -match "tadka-payment\s+order-placed")
  if (-not $ok) { Write-Host "group still active, retrying in 5s..."; Start-Sleep -Seconds 5 }
} until ($ok)
```

Note the payment count before restarting, to compare once the replay finishes:

```
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT count(*) FROM payment.payments;"
```

Restart Payment. With its offset reset to earliest, it re-consumes every `order-placed` message ever published, from the first order of the day. Wait the usual 15 to 20 seconds for it to fully come up and finish reprocessing before checking anything:

```
dotnet run --project src/Tadka.Payment.Api
```

Check for duplicates the same way as section 4, across the entire replayed history this time:

```bash
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\", count(*) FROM payment.payments GROUP BY \"OrderId\" HAVING count(*) > 1;"   # 0 rows
```
```powershell
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \`"OrderId\`", count(*) FROM payment.payments GROUP BY \`"OrderId\`" HAVING count(*) > 1;"   # 0 rows
```

The payment count is unchanged after a full replay, and zero orders show more than one payment row. The Inbox dedups every already-processed message, no matter how far back the replay goes. This is the strongest version of the idempotency claim in this runbook: reprocessing the entire history from scratch produces the exact same state as processing it once.

## 9. Where the code lives, and how other stacks would do this

Monolith: `Infrastructure/Messaging/*` for the `order-placed` producer and the `payment-results` consumer, `Data/Outbox/*` and `OutboxRelay` for the claim-and-publish loop with `FOR UPDATE SKIP LOCKED`. Payment service: `Messaging/*` for the `order-placed` consumer and `payment-results` producer, `PoisonMessageTracker.cs` and `OrderPlacedConsumer.cs` for the retry and DLQ logic from section 7. Both services' `inbox_messages` tables are what sections 4 and 8 exercise. Full reasoning: ADR-027 (Kafka as the backbone), ADR-028 (Outbox and Inbox), ADR-029 (Saga choreography), ADR-050 (envelope versioning), ADR-051 (DLQ).

If you built this in Java or Node instead of .NET, the pattern is identical: at-least-once delivery, an idempotent consumer, a durable outbox, choreographed compensation. Only the tooling changes. Full detail in [`docs/learn/cross-stack-async-messaging.md`](../learn/cross-stack-async-messaging.md).

| Concern | .NET (this repo) | Java (Spring Kafka) | Node (kafkajs) | Go |
|---|---|---|---|---|
| Manual offset commit | Explicit, commit only after the handler succeeds | Defaults to `AckMode.BATCH` with auto-commit on, set `AckMode.MANUAL` or inherit the same trap | `autoCommit` defaults to true on a timer, same trap, set it false and commit after `eachMessage` | Hand-rolled offset commit, no framework default to fight |
| Transactional outbox | Hand-rolled table and relay, `FOR UPDATE SKIP LOCKED` for multi-pod safety | Debezium reads the Postgres WAL directly, or Spring Modulith's Event Publication Registry gives this near-free | No framework gives this for free, hand-rolled the same shape | Hand-rolled, Debezium is stack-agnostic since it reads the WAL |
| DLQ, poison messages | Hand-rolled `PoisonMessageTracker` | Built in: `DeadLetterPublishingRecoverer` and `SeekToCurrentErrorHandler` | No built-in helper, same hand-rolled shape | Hand-rolled around `Seek` |
| Saga, choreography | `PaymentResultsConsumer` reacts to `payment-results` | `@KafkaListener` reacting to the same topic | `eachMessage` callback | A consumer loop reacting to the topic |

What does not change across any stack: the partition key discipline, using `orderId` so one order's events stay ordered, is a Kafka protocol decision, not a library one. The multi-instance danger for an outbox relay, where several pods claim the same unclaimed rows, is the same in every language. The fix is `SKIP LOCKED`, leader election, or CDC, an architectural choice independent of the runtime.

## Done when

- [ ] `docker compose ps` shows `tadka-kafka` healthy. Kafka UI at :8090 shows `order-placed` and `payment-results`.
- [ ] Happy path: `POST /orders` returns Created in milliseconds, then settles to Confirmed. Payment shows Completed. The outbox row shows `sent=true`.
- [ ] Catch-up: Payment down, order stays pending with lag above 0. Restart, order confirms with lag 0.
- [ ] No order has more than one payment row. A Failing gateway cancels the order.
- [ ] The boundary check over the monolith finds no payment internals and no `IPaymentClient`.
- [ ] `dotnet test` passes 34/34.
- [ ] `inject-poison.ps1 -Mode Malformed`: two retries, then routed to `order-placed.dlq`. A healthy order right after settles normally.
- [ ] `inject-poison.ps1 -Mode MissingRequiredField`: no error, no DLQ entry, the payment's currency silently defaults to INR.
- [ ] Full offset reset and replay: payment count unchanged, zero duplicate order ids.

## If something goes wrong

- **`tadka-kafka` name conflict, or it is stuck "starting":** run `docker rm -f tadka-kafka tadka-kafka-ui`, then `docker compose up -d kafka kafka-ui`.
- **`ConsumeException: Subscribed topic not available` repeating in the log on first startup:** expected on a fresh broker if you skipped the topic pre-create step in section 1. It self-heals the moment the topic is created. Not a crash, the consumer keeps polling. Pre-create the topics next time.
- **Order never confirms:** is Payment up, and is `tadka-kafka` healthy? Check the monolith log for "OutboxRelay published" and the Payment log for "OrderPlacedConsumer subscribed". Confirm `Kafka:BootstrapServers=localhost:9092` is set in both apps' `appsettings.Development.json`. If you just started or restarted either app, also just wait, see the next item.
- **Order or lag looks stuck right after starting or restarting an app:** this is usually impatience, not a bug. A restart needs a genuine 15 to 20 seconds before its output means anything. The describe command printing that the group is rebalancing confirms it is still settling, not broken.
- **`inject-poison.ps1` (or any `.ps1` script here) says command not found, or pwsh not recognized:** in PowerShell, run it as `.\scripts\inject-poison.ps1 ...`. From Git Bash, run it as `powershell.exe -File scripts/inject-poison.ps1 ...`. Neither path needs PowerShell 7.
- **`inject-poison.ps1` seems to do nothing:** it publishes directly to the raw topic, bypassing the API, so nothing shows up through `POST /orders`. Watch the Payment service log and the DLQ topic directly.
- **The section 6 boundary check shows hits you were told to expect none of:** make sure your command excludes `Migrations/` and comment-only lines. Old EF migration snapshots from before Payment was extracted still mention `Domain.Payments.Payment`, and one source comment mentions `PaymentDbContext` by name. Neither is a real dependency.
- **`--reset-offsets` fails with "group is still active":** this is normal. The consumer group registration takes a genuinely long time to expire after you stop the process. `Confluent.Kafka`'s default session timeout is 45 seconds, and a live run on this branch needed 60 to 70 seconds total. Use the polling loop in section 8 instead of a single fixed sleep.
- **Outbox row stuck at `sent=false` for more than a few seconds:** check the monolith log for `OutboxRelay` errors. Either the relay is not running, or the row's claim lock is being held open by a slow Kafka publish. Confirm `tadka-kafka` is actually healthy, not just running.
- **Reset everything:** the fresh-start block at the top of section 1 does exactly this. Run it any time you want a clean slate, not just at the start of the day.

Next, Day 10: authentication and role-based access across services, the data-privacy and PII thread, and extracting the Delivery service.
