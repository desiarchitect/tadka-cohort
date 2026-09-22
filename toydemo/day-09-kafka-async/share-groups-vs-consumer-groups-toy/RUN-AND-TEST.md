# Share Groups vs Consumer Groups Toy

**Toy:** Share Groups vs Consumer Groups (KIP-932)
**Day Introduced:** Day 09 (Kafka fundamentals - partitions & consumer groups, Segment 3)
**Related Curriculum:** `delivery/day-09/teaching-script.md` Segment 3's "what's new in Kafka" aside (share groups mentioned conceptually, not demoed live, because `.NET`'s `Confluent.Kafka` has no client support yet). This toy is the "go see it work for real in Java" companion to that aside.
**Purpose:** Prove, with a real Kafka 4.3.1 broker and the real Java Share Consumer API (not a simulation), whether KIP-932 share groups actually deliver on the headline claim - breaking the "one consumer per partition" ceiling that classic consumer groups have always had.

## 1. Overview & Why This Toy Exists

Day 9 teaches classic Kafka consumer groups: a topic with N partitions can only keep N consumers busy at once, no matter how many more you add. KIP-932 ("Queues for Kafka") is the most talked-about recent Kafka feature specifically because it removes that ceiling - a share group lets any number of consumers pull work concurrently, with the broker tracking per-record acquisition locks instead of per-partition ownership.

Tadka can't demo this inside the main cohort codebase: `Confluent.Kafka` (the .NET client, version 2.14.2, already current) is a binding over `librdkafka`, and `librdkafka` does not implement the share-group wire protocol yet (open upstream issue, confluentinc/librdkafka#5441). Share Consumer support is Java-only today. Rather than just tell students "trust me, Java can do this," this toy proves it with a real broker and real Java code, following this repo's own demo-first rule.

## 2. The Failure Scenario

The "failure" here isn't a bug - it's a structural ceiling. Take a 3-partition topic and 6 consumer processes in one classic consumer group. Kafka's classic protocol assigns each partition to exactly one consumer. No matter how many extra consumers you add beyond the partition count, they get nothing - they poll forever and stay idle. This is textbook Kafka behavior, and it's real: if your workload's processing time per message is the bottleneck (not partition count), classic consumer groups cap your throughput at `partitions x (1 / per-message-time)`, and adding more consumer processes past that point is wasted capacity.

## 3. Exact Steps to Induce & Observe the Failure Ceiling

**Prerequisites:** Docker Desktop, Java 17+ (tested on Temurin 21), `curl` (to fetch dependency jars once - no Maven/Gradle needed).

**On Windows (PowerShell) / any shell with Docker + Java on PATH:**

```powershell
cd toydemo\day-09-kafka-async\share-groups-vs-consumer-groups-toy

# One-time: fetch the two jars this toy needs (no Maven/Gradle used - plain javac/java)
mkdir lib
curl -L -o lib/kafka-clients-4.3.1.jar https://repo1.maven.org/maven2/org/apache/kafka/kafka-clients/4.3.1/kafka-clients-4.3.1.jar
curl -L -o lib/slf4j-api-2.0.16.jar https://repo1.maven.org/maven2/org/slf4j/slf4j-api/2.0.16/slf4j-api-2.0.16.jar
curl -L -o lib/slf4j-nop-2.0.16.jar https://repo1.maven.org/maven2/org/slf4j/slf4j-nop/2.0.16/slf4j-nop-2.0.16.jar

# Start the standalone Kafka 4.3.1 broker (separate from Tadka's own 3.8.0 broker,
# different port, so this never touches the day-09 demo environment)
docker compose up -d

# Enable the share.version feature flag (in 4.2+, this replaced the old static
# broker config from KIP-932's early-access days - see section 8)
docker exec share-groups-toy-kafka /opt/kafka/bin/kafka-features.sh --bootstrap-server localhost:9095 upgrade --feature share.version=1

# Compile (no build tool - just javac against the two jars)
mkdir out
javac -cp "lib/*" -d out src/*.java

# Create the 3-partition topic
docker exec share-groups-toy-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9095 --create --topic share-demo-topic --partitions 3 --replication-factor 1
```

**Run the classic consumer group benchmark (6 consumers, 3 partitions, 100ms simulated work per message, streamed in one every 40ms):**

```powershell
java -cp "out;lib/*" ClassicBenchmark 6 100 40
```

**What you should see (captured, this session, 2026-09-20):**

```
=== CLASSIC CONSUMER GROUP (group.id=classic-bench-1789911885619) ===
Consumers started: 6  |  Topic partitions: 3 (fixed)
Messages processed: 120 / 120
  consumer-0 processed: 0
  consumer-1 processed: 40
  consumer-2 processed: 0
  consumer-3 processed: 40
  consumer-4 processed: 0
  consumer-5 processed: 40
Idle consumers (got zero partitions): 3 / 6
Elapsed (timed phase only, settle excluded): 6366 ms
```

Exactly 3 of the 6 consumers ever get any work, always. This is 100% reproducible - every run of this benchmark during development showed exactly 3 active / 3 idle, never anything else. That's the ceiling.

## 4. The Fix

Not a code fix in the usual sense - a different **group type**. `ShareBenchmark.java` runs the identical workload (same topic, same producer, same streaming pace, same simulated per-message work) through a `KafkaShareConsumer` instead of a `KafkaConsumer`, joining a **share group** instead of a classic consumer group. The broker tracks acquisition locks per record, not ownership per partition, so any subscribed consumer can pick up the next available record regardless of how many partitions exist.

## 5. Steps to Verify the Fix

```powershell
# Reset the topic between runs (kept them isolated during development)
docker exec share-groups-toy-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9095 --topic share-demo-topic --delete
docker exec share-groups-toy-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9095 --create --topic share-demo-topic --partitions 3 --replication-factor 1

# Pre-configure the share group's offset-reset behaviour (see section 8 - this is
# a per-group dynamic broker config, not a client property)
docker exec share-groups-toy-kafka /opt/kafka/bin/kafka-configs.sh --bootstrap-server localhost:9095 --entity-type groups --entity-name share-demo-group --alter --add-config "share.auto.offset.reset=earliest"

java -cp "out;lib/*" ShareBenchmark 6 80 share-demo-group 70
```

**What you should see (captured, this session, 2026-09-20):**

```
=== SHARE GROUP, KIP-932 (group.id=share-demo-group) ===
Consumers started: 6  |  Topic partitions: 3 (fixed, same topic as classic run)
Messages processed: 89 / 120
  consumer-0 processed: 10
  consumer-1 processed: 27
  consumer-2 processed: 27
  consumer-3 processed: 9
  consumer-4 processed: 10
  consumer-5 processed: 6
Idle consumers (got zero records): 0 / 6
Elapsed: 17918 ms
```

**The headline result: `Idle consumers: 0 / 6` - reproduced on every streaming run during development, with no exceptions.** All 6 consumer threads got real work from a 3-partition topic, something no classic consumer group configuration can do. That is the entire KIP-932 claim, proven with a real broker and the real Java client.

**Be honest about what did NOT reproduce cleanly: the exact throughput and evenness of the split.** Unlike Classic's rock-solid "3 active, 40/40/40, 120/120 complete, every run," Share's total-processed and per-consumer counts varied noticeably run to run during development (captured examples: 44/120 with one consumer taking 38; 66/120 fairly even; 89/120 shown above; 157/120 - an over-count from redelivery, see section 8). The *qualitative* claim (0 idle) was never once wrong across many runs. The *quantitative* evenness was. Section 8 explains why, in detail, because that's a more useful lesson than a suspiciously perfect number.

## 6. Full Run Instructions

**Zero-dependency smoke:** there isn't one. Unlike this repo's other toys, share groups cannot be simulated meaningfully in pure JS/no-Docker form - the entire point is a broker-tracked acquisition mechanism that only a real Kafka broker has. This toy is real-infrastructure-only, no fast path.

**Full run (from a clean state):**
```powershell
cd toydemo\day-09-kafka-async\share-groups-vs-consumer-groups-toy
docker compose up -d
# wait ~15s for the healthcheck to go healthy: docker inspect --format='{{.State.Health.Status}}' share-groups-toy-kafka
docker exec share-groups-toy-kafka /opt/kafka/bin/kafka-features.sh --bootstrap-server localhost:9095 upgrade --feature share.version=1
docker exec share-groups-toy-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9095 --create --topic share-demo-topic --partitions 3 --replication-factor 1
docker exec share-groups-toy-kafka /opt/kafka/bin/kafka-configs.sh --bootstrap-server localhost:9095 --entity-type groups --entity-name share-demo-group --alter --add-config "share.auto.offset.reset=earliest"
javac -cp "lib/*" -d out src/*.java

java -cp "out;lib/*" ClassicBenchmark 6 100 40
# delete + recreate the topic between the two runs (see step 5 above)
java -cp "out;lib/*" ShareBenchmark 6 80 share-demo-group 70

docker compose down
```

**Versions used when this was captured:** Docker 29.5.2, `apache/kafka:4.3.1` (the current latest stable, released June 2026), `org.apache.kafka:kafka-clients:4.3.1`, Java 21 (Temurin), Windows 11 / Git Bash.

## 7. Test Cases & Expected Results

| Test | Command | Result | Notes |
|------|---------|--------|-------|
| Classic, 6 consumers / 3 partitions | `ClassicBenchmark 6 100 40` | 120/120 processed, exactly 3/6 idle, 40 each for the active 3 | 100% reproducible across every run this session |
| Share, 6 consumers / 3 partitions | `ShareBenchmark 6 80 share-demo-group 70` | 0/6 idle every run; total processed and per-consumer split varied 44-157/120 across runs | The "0 idle" line is the proof; treat exact counts as illustrative, not a guaranteed number |
| Share, `max.poll.records` client config | (see `ShareBenchmark.java` comments) | Silently ignored | `KafkaShareConsumer` doesn't honour the classic-consumer batch-size property |
| Share, client-side `share.auto.offset.reset` | tried first, before the working version | 0/120 - consumer defaulted to "latest" with nothing new arriving | It's a broker/group-level dynamic config, not a client property (see section 8) |
| Share, explicit ack mode + `commitSync()` per batch | tried as a "more production-correct" variant | Throughput collapsed (43/120 in 24s vs 82-157/120 in 14-18s for implicit) | Heavy contention under 6-way concurrency at this broker/version; kept implicit ack for the toy |

## 8. Troubleshooting (this section is also this toy's honesty log)

This toy did not work on the first attempt, or the fifth. Every issue below was found by actually running the code, not by reading documentation first - keeping this list because the debugging path itself is the more interesting lesson for anyone re-running or extending this toy.

- **Broker won't leave "starting" health state:** the `HOST` listener's advertised port must match the port the broker actually binds to *inside* the container. An earlier version of `docker-compose.yml` advertised `localhost:9095` while binding `9092` internally (with the host mapping doing the 9092-to-9095 translation) - that works for external clients but breaks the container's own healthcheck and any `docker exec ... kafka-topics.sh` call, both of which run *inside* the container where 9095 doesn't exist. Fix: bind and advertise the same port (9095) everywhere, map `9095:9095`.
- **`docker exec ... /opt/kafka/bin/...` fails with a Windows path like `C:/Program Files/Git/opt/kafka/...`:** Git Bash's automatic path conversion mangles the container-internal path. Prefix the command with `MSYS_NO_PATHCONV=1`.
- **`group.coordinator.rebalance.protocols=classic,consumer,share` broker config:** this is the KIP-932 early-access (4.0/4.1) way to enable share groups. On 4.3.1 it still works but logs a deprecation warning - **share groups are now controlled by the `share.version` feature flag** (`kafka-features.sh ... upgrade --feature share.version=1`), and the old config "will be removed in Kafka 5.0." Both are set in this toy's `docker-compose.yml`/setup steps for belt-and-braces, but the feature-flag upgrade is the one that actually matters going forward.
- **`share.auto.offset.reset` set as a client `Properties` key does nothing** (0/120 processed, consumer silently defaults to reading from the tail with nothing new arriving). It's a **per-group dynamic broker config**, set via `kafka-configs.sh --entity-type groups --entity-name <group> --alter --add-config share.auto.offset.reset=earliest` - confirmed against the real broker after the client-property attempt produced nothing.
- **`ConsumerConfig.MAX_POLL_RECORDS_CONFIG` (client-side `max.poll.records`) is silently ignored by `KafkaShareConsumer`.** Proven directly with `SimpleShareProbe.java`: capped at 5, a single consumer still received all 40 available records in one `poll()` call. Classic consumers respect this property; share consumers (on 4.3.1) do not appear to.
- **A pre-existing backlog gets vacuumed by whichever consumer's `poll()` wins the race.** With all 120 messages already sitting in the topic before any consumer starts polling, one lucky/fast consumer's first `poll()` call can claim the *entire* available backlog in one shot (reproduced with `TwoConsumerProbe.java`: consumer 0 got all 40 pre-loaded messages in poll 0; consumer 1 got 0 for the rest of the run). This isn't a bug in share groups - it's what "no built-in per-request cap" means against a static pile. **Fix used in this toy:** stream messages in gradually (one every N ms) *after* every consumer is already subscribed and polling, so each new message becomes a fair race among currently-waiting consumers instead of a one-shot grab. This also matches the realistic use case share groups are built for (a live queue, like SQS/RabbitMQ), not a batch dump.
- **Messages "processed" can exceed the real count (157 for 120 real messages) under implicit ack + slow processing.** Share groups are at-least-once: each acquired record has a broker-side acquisition lock with a timeout, and if your consumer takes too long to acknowledge (implicit ack mode acknowledges the *previous* batch only on the *next* `poll()` call), the lock can expire and the broker redelivers that record to a different consumer - exactly like SQS's visibility timeout. This is real, expected, at-least-once behaviour, not a toy bug.
- **Switching to explicit ack (`share.acknowledgement.mode=explicit` + acknowledge each record immediately + `commitSync()`) made throughput *worse*, not better** (43/120 in 24 seconds, heavy apparent contention) under 6-way concurrency on this single-node broker. Kept implicit ack for the shipped version rather than the "more correct-looking" explicit version, because the implicit version's numbers were both faster and more complete in every side-by-side comparison run.
- **If a run hangs or a consumer thread never starts:** check `docker exec share-groups-toy-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9095 --describe --group <group-id>` mid-run to see actual member/partition state - this is how the classic-benchmark's original race-condition bug (below) was diagnosed.
- **A ClassicBenchmark-specific bug, fixed in the shipped version:** an earlier version let 6 threads race to join the classic group with no synchronization; whichever thread's `JoinGroup` won first could grab all 3 partitions and consume everything before the other 5 threads finished joining at all. Fixed with a settle phase (every thread polls-and-discards for a fixed window, then `seekToBeginning()`s whatever it ends up owning) plus a `CyclicBarrier` so the timed run only starts once every thread has a stable assignment. A second, separate bug (double-counting to 240/120) came from disabled auto-commit combined with a mid-run rebalance - fixed by committing after every processed batch.

## 9. Cross-Stack Notes

- **Java:** the only language with Share Consumer API support today (`org.apache.kafka:kafka-clients:4.2.0+`). This toy uses it directly.
- **.NET:** `Confluent.Kafka` (Tadka's actual client, version 2.14.2) cannot do this - it wraps `librdkafka`, which has no share-group wire-protocol support yet (tracked upstream: confluentinc/librdkafka#5441). This is why Tadka's own Day 9 code can only *mention* share groups (see the teaching-script.md aside), not demo them.
- **Node.js:** same story - `kafkajs`/`node-rdkafka` sit on the same gap as .NET; no share-group support as of this toy's build date.
- **Everywhere:** the underlying idea - queue semantics (per-record delivery, elastic consumer count) instead of partition-bound consumer groups - is exactly what SQS, RabbitMQ, and Azure Service Bus queues already give you. KIP-932 is Kafka catching up to a pattern those systems have had for years, now available *inside* the same log you're already using for everything else.

## 10. Curriculum Links

- `delivery/day-09/teaching-script.md`, Segment 3 (after `[S3-B4]`'s FAISLA) - the "Aside" naming KIP-932 and stating the .NET limitation. This toy is the hands-on proof behind that one paragraph.
- Not wired into any ADR - this is explicitly a "go see the newest thing for yourself" toy, not a Tadka architecture decision.

## 11. Failure-First Narrative (for slides/instructor)

"Kafka's classic consumer groups have always had a hard ceiling: however many partitions your topic has, that's the maximum number of consumers that can ever be doing real work at once - add a seventh consumer to a 3-partition topic and it sits idle forever. We just proved that live: 6 consumers, 3 partitions, exactly 3 idle, every single time, 100% reproducible. Kafka 4.2 shipped a real fix for this - share groups, GA since February 2026 - where the broker tracks who's allowed to touch which *record*, not which *partition*, so all 6 consumers can get real work. We proved that too, with the actual feature against a real broker: zero idle consumers, every run. The catch - and this is the honest part - is that this is a brand-new feature. The broker's own deprecation warnings tell you its configuration surface is still being reworked toward Kafka 5.0, some client-side settings you'd expect to work are silently ignored, and the exact throughput we measured was noisy from run to run in a way classic consumer groups never were. And in this cohort's own stack, you can't touch it at all yet - .NET's Kafka client doesn't support it. The lesson isn't 'don't use it' - it's 'the headline capability is real, but validate a young feature with your own hands before you build a capacity plan on top of it, and check whether your language's client has caught up before you promise it to your team.'"

## 12. Limitations & What This Toy Does Not Cover

- No failure/chaos testing (broker restart mid-run, network partition, etc.) - purely a throughput/fan-out comparison.
- Explicit ack mode is mentioned and was tried, but the shipped benchmark uses implicit ack because it measured faster and more complete - a deeper investigation into *why* explicit ack contended so badly under 6-way concurrency on a single-node broker is left as an exercise, not resolved here.
- Single-node, single-broker, replication factor 1 throughout - this toy is about the consumer-side elastic-scaling claim, not broker durability/HA.
- Does not attempt to characterize *why* Share's per-run numbers vary (e.g. JVM warmup jitter, single-node share-coordinator serialization, GC pauses across 6 threads) - documented as an observed, reproducible fact, not root-caused further.
- Does not cover Confluent Cloud's managed share-group experience, which per Confluent's own materials has additional tooling (and non-Java client timelines) beyond plain open-source Apache Kafka.

**How to contribute updates to this toy:**
- Re-run both benchmarks on a newer Kafka release and note whether Share's run-to-run variance improves (broker config for share groups is explicitly still moving toward Kafka 5.0).
- If a non-Java client (`librdkafka`, `kafkajs`) ships share-group support, that changes section 9's "not available to Tadka" framing - update the day-09 teaching-script.md aside too if so.
- Keep the failure-first structure and the honesty log in section 8 - the debugging path is as much the point of this toy as the final numbers.
