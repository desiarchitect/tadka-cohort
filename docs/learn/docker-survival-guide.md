# Docker Survival Guide (For Absolute Beginners)

If you have never used Docker before, **don't panic!** 

For the first five weeks of this cohort, you do not need to be a Docker expert. We are simply using Docker as a convenient way to run our databases (like PostgreSQL and Redis) without having to install them directly onto your laptop.

> [!NOTE]
> **We will cover Docker in-depth in Week 6!** 
> During Phase A4 (Week 6), we will do a deep dive into containerization, `Dockerfile` creation, and multi-stage builds when we prepare our .NET APIs for cloud deployment. Until then, just treat Docker as a magic box that runs our databases.

---

## What is Docker?

In simple terms, Docker allows us to run software in isolated, lightweight environments called **Containers**. 

Instead of you having to figure out how to install PostgreSQL 16 on Windows, Mac, or Linux, we just tell Docker: *"Hey, go download the official PostgreSQL container and run it on port 5432."* 

It guarantees that every single student in the cohort is running the exact same version of the database, completely eliminating the "it works on my machine" problem.

## The 3 Commands You Need to Know

For Weeks 1 through 5, you only need to know how to use **Docker Compose**. Docker Compose is a tool that reads our `docker-compose.yml` file and starts all the necessary containers automatically.

Open your terminal in the `tadka` repository and use these commands:

### 1. Start everything
```bash
docker compose up -d
```
* **What it does:** Downloads the necessary images (like Postgres) and starts the containers in the background (that's what the `-d` or "detached" flag means).

### 2. Check if it's running
```bash
docker compose ps
```
* **What it does:** Lists all the containers currently running. You should see `tadka-postgres` listed with a status of `Up` or `Healthy`.

### 3. Stop and reset everything
```bash
docker compose down -v
```
* **What it does:** Stops the containers and destroys them. The `-v` flag is very important—it deletes the data volumes. If you ever mess up your database and want a completely fresh start, run this command, and then run `docker compose up -d` again.

---

**That's it!** You now know enough Docker to survive the first half of the cohort. Focus on learning the architecture and domain modeling, and we'll circle back to master Docker in Week 6.

---

## Lab environment: don't run everything, every day (RAM tiers)

The stack **grows** as Tadka grows. By Week 7 the full system is **~14 containers** (4 services + gateway + Postgres + replica + 3 service-DBs + Redis + Kafka + Kafka-UI + the observability stack). On a 16 GB laptop, running all of it at once will swap, OOM-kill containers, and eat your session debugging Docker instead of learning architecture. **The rule: run only the subset the day's demo needs.**

### What to run, by day

| Days | What you need up | Rough footprint |
|------|------------------|-----------------|
| **1–5** | `postgres` (+ `replica` on Day 5) | Light — any laptop |
| **6** | `postgres`, `replica`, `redis` | Light |
| **7** | same as Day 6 (Payment is still in-process) | Light |
| **8–9** | `postgres`, `replica`, `redis`, `payment-db`, `kafka` (+`kafka-ui`) + run **2 app processes** | Medium — 16 GB ok if you close other apps |
| **10** | same as Day 9 | Medium |
| **11** | + `delivery-db` + run **3 apps + gateway** | Medium-heavy |
| **12** | as Day 11 (deploy is cloud/black-box, not local) | Medium-heavy |
| **13–14** | + the **observability stack** (otel-collector, prometheus, tempo, loki, grafana) | **Heavy (~14 containers)** — use profiles below |
| **15–16** | teardown/teardown + targeted load tests; run only what a given test exercises | Varies |

> **`kafka-ui`, `tempo`, `loki`, `grafana` are *convenience*, not *correctness*.** If RAM is tight, skip the UIs — the system runs fine without them; you just lose the dashboards.

### Compose profiles (Day-13 onward)

From Day 13 the `docker-compose.yml` groups services with **profiles** so you start only a tier:

```bash
docker compose up -d                      # 'core' only — DBs, Redis, Kafka (no profile = always up)
docker compose --profile observability up -d   # adds OTEL + Prometheus + Tempo + Loki + Grafana
```

- **`core` (no profile, always up):** `postgres`, `replica`, `payment-db`, `delivery-db`, `redis`, `kafka`.
- **`observability` (opt-in):** `otel-collector`, `prometheus`, `tempo`, `loki`, `grafana` — off by default; turn on only for the Day-13/14 observability + chaos labs.

> *(The `profiles:` keys are wired into the compose file when the observability stack lands on Day 13. Until then there's nothing heavy to gate.)*

### If your laptop still can't cope (escape hatch)

The chosen default is **local + profiles** (everything reproducible on your machine). If a 16 GB laptop genuinely can't run the Week-7 heavy tier, the heavy days can be run in a **cloud dev environment** (GitHub Codespaces / Gitpod) using the repo's dev-container config — same commands, the containers just run in the cloud. This is a fallback, not the default (it needs an account/quota); prefer profiles + "run the subset" first.
