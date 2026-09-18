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

## 1. Replica - a copy, not HA

**Story:** write on master, read on replica, **kill the writer**. Data on the replica survives. An app aimed at the master is **dead**. `docker start` is **reset**, not the fix (Sentinel is §3).

Compose: `redis-server --replicaof 172.28.0.10 6379`

```powershell
# DOING: write on the only writer.
docker exec tadka-ha-master redis-cli SET demo:ha namaste

# DOING: read the copy. PROVES: replication. Expect: namaste. NOT: HA.
docker exec tadka-ha-replica redis-cli GET demo:ha

# DOING: ask who this process is. Expect: role:slave
docker exec tadka-ha-replica redis-cli INFO replication

# DOING: FAIL THE WRITER.
docker stop tadka-ha-master

# DOING: read after master death. PROVES: copy survived. Expect: namaste.
# THE FAIL: app still pointing at master (host 6380) is down. Copy != failover.
docker exec tadka-ha-replica redis-cli GET demo:ha

# DOING: RESET. NOT Sentinel. NOT the product fix.
docker start tadka-ha-master
```

---

## 2. Cluster - sharding, not HA

**Story:** `MOVED` is Cluster working. Kill one node with **0 replicas** → those slots are **gone**. `docker start c2` is reset, not HA.

```powershell
# DOING: list masters + slot ranges. Expect: three masters, 172.28.0.21-23.
docker exec tadka-redis-c1 redis-cli CLUSTER NODES

# DOING: write without following redirects.
# PROVES: sharding. Expect: MOVED. NOT a bug.
docker exec tadka-redis-c1 redis-cli SET user:1 a

# DOING: cluster-aware client (-c). Expect: OK then a. docker exec only (MOVED is a Docker IP).
docker exec tadka-redis-c1 redis-cli -c SET user:1 a
docker exec tadka-redis-c1 redis-cli -c GET user:1

# DOING: FAIL ONE SHARD.
docker stop tadka-redis-c2

# DOING: read after shard death. PROVES: 0 replicas. Expect: CLUSTERDOWN / error. THAT is the fail.
docker exec tadka-redis-c1 redis-cli -c GET user:1

# DOING: RESET. NOT --cluster-replicas 1.
docker start tadka-redis-c2
```

---

## 3. Sentinel - the actual failover (the fix)

**Story:** same `docker stop` as §1, but Sentinel **changes the writer**. That is HA. `docker start` is reset. Tadka would still not follow (hardcoded host).

`sentinel.conf`: `sentinel monitor mymaster 172.28.0.10 6379 1` (static IP). Quorum `1` is class-only; production uses 3 sentinels / quorum 2.

```powershell
# If you just finished §1, master is already up. If not:
docker start tadka-ha-master

# DOING: ask who the writer is. Expect: 172.28.0.10 6379
docker exec tadka-ha-sentinel redis-cli -p 26379 SENTINEL get-master-addr-by-name mymaster

# DOING: the SAME fail as §1.
docker stop tadka-ha-master
Start-Sleep -Seconds 8

# DOING: ask again. PROVES: THE FIX — writer CHANGED. Expect: 172.28.0.11 6379
# NOT: "GET still namaste" (that was §1). Here the *role* moved.
docker exec tadka-ha-sentinel redis-cli -p 26379 SENTINEL get-master-addr-by-name mymaster

# DOING: confirm promotion. Expect: role:master
docker exec tadka-ha-replica redis-cli INFO replication

# DOING: RESET.
docker start tadka-ha-master
```

| After `stop` master | §1 replica | §3 Sentinel |
|---|---|---|
| Data | Copy still `GET`s | Copy still `GET`s |
| Writer | Dead host | **Address changes** |
| Hardcoded app | **Dead** | Dead unless it talks to Sentinel |

---

## Setup fail vs demo fail

| You see | What it is | Fix |
|---|---|---|
| replica GET nil | replicaof not ready | wait 2s, retry |
| CLUSTERDOWN / not in cluster | `cluster-init` lost the race, or nodes crashed | `docker ps -a`; if c1/c2/c3 Exited, compose is old (hostname announce-ip). Pull this file. Else `docker start tadka-redis-c-init` |
| SENTINEL still names 172.28.0.10 after stop, or `s_down` / TILT in logs | hostname monitor + Docker DNS | this compose uses static `172.28.0.10`. Pull. Do not wait longer — it will not recover |
| cluster-announce-ip FATAL | Redis 7 requires a literal IP | this compose already uses 172.28.0.21-23 |
| port already in use | leftover toy | `docker compose -f toydemo/day-07-redis-ha/docker-compose.yml down -v` |
