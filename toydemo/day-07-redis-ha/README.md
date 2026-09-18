# Redis in production (Day 7 opener toy)

Not Tadka. Tadka stays one container `tadka-redis` on 6379. This compose is how **replica**, **Cluster**, and **Sentinel HA** are actually set up.

```powershell
docker compose -f toydemo/day-07-redis-ha/docker-compose.yml up -d
# wait ~10s for cluster-init (one-shot). If CLUSTER NODES is empty:
docker start tadka-redis-c-init
```

| Container | Host port | Role |
|---|---|---|
| `tadka-ha-master` | 6380 | toy master |
| `tadka-ha-replica` | 6381 | `REPLICAOF` copy |
| `tadka-ha-sentinel` | 26379 | failover voter |
| `tadka-redis-c1/c2/c3` | 7001-7003 | Cluster, 0 replicas |

Tear down: `docker compose -f toydemo/day-07-redis-ha/docker-compose.yml down -v`

Tadka kill-Redis (classification) uses the **main** compose: `docker compose stop redis`. Do that in the runbook, not here.

---

## 1. Replica - how it is set up, and why it is not HA

Compose command: `redis-server --replicaof redis-master 6379`

```powershell
docker exec tadka-ha-master redis-cli SET demo:ha namaste
docker exec tadka-ha-replica redis-cli GET demo:ha
# namaste

docker exec tadka-ha-replica redis-cli INFO replication
# role:slave   master_host:redis-master

docker stop tadka-ha-master
docker exec tadka-ha-replica redis-cli GET demo:ha
# namaste  - the COPY survived
```

An app still pointing at the master (Tadka points at `localhost:6379`) is down. **Replication is a copy. Failover is a new writer.**

Start the master again before Sentinel: `docker start tadka-ha-master` (wait a few seconds; replica resyncs).

---

## 2. Cluster - how it is set up (sharding, not HA)

Each node: `cluster-enabled yes`, `cluster-config-file`, `cluster-announce-ip`. Then:

```
redis-cli --cluster create redis-c1:6379 redis-c2:6379 redis-c3:6379 --cluster-replicas 0 --cluster-yes
```

`--cluster-replicas 0` is the point: no extra copy per slot. Announce IPs are literals (`172.28.0.21` …) — Redis 7.4 rejects a hostname for `cluster-announce-ip`.

```powershell
docker exec tadka-redis-c1 redis-cli CLUSTER NODES
# three masters, slot ranges, IPs 172.28.0.21-23 (not hostnames — Redis 7 rejects hostname for cluster-announce-ip)

docker exec tadka-redis-c1 redis-cli SET user:1 a
# (error) MOVED <slot> 172.28.0.2x:6379     <- THAT is Cluster
docker exec tadka-redis-c1 redis-cli -c SET user:1 a
docker exec tadka-redis-c1 redis-cli -c GET user:1
# a     (-c follows MOVED; must docker exec, not host redis-cli — MOVED is a Docker IP)

docker stop tadka-redis-c2
docker exec tadka-redis-c1 redis-cli CLUSTER NODES
# one node fail
docker exec tadka-redis-c1 redis-cli -c GET user:1
# may be CLUSTERDOWN / error - those slots had no replica
docker start tadka-redis-c2
```

**Cluster split the keyspace. We did not give each slot a replica, so a dead node is dead keys. Sharding is not HA.**

---

## 3. Sentinel - how HA is actually set up

`sentinel.conf` (this folder):

```
sentinel monitor mymaster redis-master 6379 1
sentinel down-after-milliseconds mymaster 3000
```

`1` is **quorum** (with one sentinel, one vote is enough for class). Production uses 3 sentinels and quorum 2.

The **client** talks to Sentinel (`localhost:26379`), not a hardcoded master host. ElastiCache "primary endpoint" is this, managed.

```powershell
docker start tadka-ha-master
# wait until replica is slave again
docker exec tadka-ha-sentinel redis-cli -p 26379 SENTINEL masters
docker exec tadka-ha-sentinel redis-cli -p 26379 SENTINEL get-master-addr-by-name mymaster
# 172.28.0.10 6379     (static IP — hostname + Docker DNS NXDOMAIN puts Sentinel in TILT)

docker stop tadka-ha-master
Start-Sleep -Seconds 8
docker exec tadka-ha-sentinel redis-cli -p 26379 SENTINEL get-master-addr-by-name mymaster
# 172.28.0.11 6379     - WRITER CHANGED (the replica)
docker exec tadka-ha-replica redis-cli INFO replication
# role:master
```

**Sentinel changed the writer.** Tadka still has `localhost:6379` in config, so Tadka would **not** follow this. That is the last line: HA is Sentinel/Cluster-replicas/**managed**, plus a client that uses that endpoint.

If failover does not happen in 10s: teach the conf on the slide and move on. Do not burn the brownout.

---

## Setup fail vs demo fail

| You see | What it is | Fix |
|---|---|---|
| replica GET nil | replicaof not ready | wait 2s, retry |
| CLUSTERDOWN / not in cluster | `cluster-init` lost the race, or nodes crashed | `docker ps -a`; if c1/c2/c3 Exited, compose is old (hostname announce-ip). Pull this file. Else `docker start tadka-redis-c-init` |
| SENTINEL still names 172.28.0.10 after stop, or `s_down` / TILT in logs | hostname monitor + Docker DNS | this compose uses static `172.28.0.10`. Pull. Do not wait longer — it will not recover |
| cluster-announce-ip FATAL | Redis 7 requires a literal IP | this compose already uses 172.28.0.21-23 |
| port already in use | leftover toy | `docker compose -f toydemo/day-07-redis-ha/docker-compose.yml down -v` |
