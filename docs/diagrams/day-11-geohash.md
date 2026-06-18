# Day 11 — Geohashing & Nearest-Rider Search

Redis-GEO for live rider location. See ADR-034 (polyglot persistence: Redis-geo + Postgres history).

---

## 1. The cell grid + neighbour search

```mermaid
flowchart TB
  subgraph grid [City grid - each cell is a geohash prefix]
    direction TB
    nw[NW cell] --- n[N cell] --- ne[NE cell]
    w[W cell] --- center["CENTER cell - the order's location"] --- e[E cell]
    sw[SW cell] --- s[S cell] --- se[SE cell]
  end
  center --> q["GEOSEARCH: scan center + 8 neighbours<br/>then filter to true radius"]
  q --> riders[Candidate riders, sorted by distance]
```

A geohash encodes lat/long into a short string where a shared prefix means physical proximity.

---

## 2. Edge effect — why neighbours matter

```mermaid
flowchart LR
  r1["Rider A - bottom edge of CENTER cell"] -. 50 m apart .- r2["Rider B - top edge of SOUTH cell"]
  note["Two riders 50 m apart can land in DIFFERENT cells.<br/>Search only center → miss B.<br/>Fix: center + 8 neighbours, then filter by real distance."]
  r1 -.- note
```

---

## 3. Write vs read profile

| Side | Pattern | Scale note |
|------|---------|------------|
| **Writes** | latest-wins `GEOADD` every 5–10s | ~30–60K location writes/s at Swiggy scale — kept off OLTP DB |
| **Reads** | center cell + neighbours + radius filter | cheap prefix lookup, not full scan |

At extreme scale: **H3/S2** hierarchical cells. Hot cell (surge) → cap + widen radius + shard the city.