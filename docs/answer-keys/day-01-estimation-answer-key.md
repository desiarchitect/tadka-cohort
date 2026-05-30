# D1-10: Back-of-Envelope Estimation — Answer Key

> **Instructor only.** Walk through this on screen during Hour 3. Students fill in their own worksheets first, then we compare.

## Assumptions

| Parameter | Value | Reasoning |
|-----------|:-----:|-----------|
| Target city | Bangalore | MVP, single city |
| Target daily orders | 1,00,000 (1 lakh) | Product brief Year 1 target |
| Avg orders per user per month | 12 (3/week) | Product brief says 3-5 times/week, use conservative |
| DAU | ~1,00,000 | ~1 lakh users placing 1 order/day on average |
| MAU | ~2,50,000 | Not all MAU are DAU. DAU/MAU ratio ~0.4 for food apps |
| Peak-to-average ratio | 3x | Dinner rush 7-10 PM. Swiggy sees 2.5-3.5x |
| Read-to-write ratio | 10:1 | Users browse 10 restaurants/menus before ordering |

---

## Traffic Estimation

### Orders (Write Traffic)

```
Daily orders:           1,00,000
Seconds in a day:       86,400

Avg orders/second:      1,00,000 / 86,400 = ~1.15 orders/sec
Peak orders/second:     1.15 × 3 = ~3.5 orders/sec
```

**Talking point:** "3.5 orders per second at peak. That's it. Your laptop can handle this. A single .NET API with connection pooling handles thousands of requests per second. This is why we start with a monolith."

### API Requests (Read + Write Traffic)

```
Reads per order placed:  ~10 requests
  - 2-3 restaurant listing/search requests
  - 2-3 menu browsing requests
  - 1 cart interaction
  - 1 order placement
  - 2-3 order tracking poll requests

Total daily API requests:  1,00,000 orders × 10 requests = 10,00,000 (10 lakh) requests
Plus non-ordering traffic:  ~5,00,000 (users who browse but don't order)

Total daily requests:  ~15,00,000 (15 lakh)

Avg QPS:  15,00,000 / 86,400 = ~17 QPS
Peak QPS: 17 × 3 = ~51 QPS
```

**Talking point:** "51 QPS at peak. A typical .NET API handles 5,000-10,000 QPS easily. We're at 1% of capacity. This is why premature optimization is the root of all evil."

---

## Storage Estimation

### Per-Record Sizes

| Record Type | Size | Breakdown |
|------------|:----:|-----------|
| User profile | ~500 bytes | name (50), email (50), phone (15), password hash (60), 2 addresses (200), preferences (50), timestamps (75) |
| Restaurant | ~2 KB | name (100), address (200), cuisine tags (100), hours (200), ratings (50), geo coords (16), images refs (500), metadata (834) |
| Menu item | ~500 bytes | name (100), description (200), price (8), category (50), image ref (100), availability (1), metadata (41) |
| Order | ~2 KB | user ref (36), restaurant ref (36), 3-5 items (500), status (20), timestamps × 8 states (200), address (200), payment ref (36), delivery ref (36), total/tax/fees (50), special instructions (200), metadata (~686) |
| Payment record | ~500 bytes | order ref (36), amount (8), method (20), status (20), gateway ref (50), timestamps (75), metadata (~291) |
| Delivery tracking | ~1 KB | order ref (36), driver ref (36), 10 GPS updates (200), status history (200), timestamps (200), metadata (~328) |
| Rating/Review | ~500 bytes | user ref (36), restaurant ref (36), order ref (36), food rating (1), delivery rating (1), review text (300), timestamp (40), metadata (~50) |

### Total Storage Growth

| Data | Count | Size Each | Daily Growth | Yearly Growth |
|------|:-----:|:---------:|:------------:|:-------------:|
| Orders | 1,00,000/day | 2 KB | **200 MB** | **73 GB** |
| Payments | 1,00,000/day | 500 bytes | **50 MB** | **18 GB** |
| Delivery tracking | 1,00,000/day | 1 KB | **100 MB** | **36.5 GB** |
| Ratings | ~30,000/day (30% rate) | 500 bytes | **15 MB** | **5.5 GB** |
| **Daily total** | | | **~365 MB** | **~133 GB** |

### One-Time / Slow-Growing Data

| Data | Count | Size Each | Total |
|------|:-----:|:---------:|:-----:|
| Users | 2,50,000 MAU Year 1 | 500 bytes | **125 MB** |
| Restaurants | 5,000 by Year 1 end | 2 KB | **10 MB** |
| Menu items | 5,000 × 30 items avg | 500 bytes | **75 MB** |
| **Static total** | | | **~210 MB** |

### Image Storage (NOT in PostgreSQL)

| Type | Count | Avg Size | Total |
|------|:-----:|:--------:|:-----:|
| Restaurant photos | 5,000 × 5 photos | 500 KB | **12.5 GB** |
| Menu item photos | 1,50,000 items × 1 photo | 200 KB | **30 GB** |
| **Image total** | | | **~42.5 GB** |

**Talking point:** "Images go to object storage (S3) and a CDN. Never store images in PostgreSQL. That 42 GB of images would kill your database backup time and query performance."

### Year 1 Total Database Size

```
Transactional data:  ~133 GB
Static data:         ~210 MB
Indexes (estimate):  ~30% overhead = ~40 GB
Total:               ~175 GB
```

**Talking point:** "175 GB in PostgreSQL after a full year at 1 lakh orders per day. A single PostgreSQL instance with 500 GB SSD handles this without breaking a sweat. You don't need sharding. You don't need partitioning. You need good indexes."

---

## Bandwidth Estimation

```
Average API response size:  ~5 KB (JSON)
  - Restaurant listing: ~10 KB (list of 20 restaurants)
  - Menu listing: ~15 KB (30 items with details)
  - Order status: ~1 KB
  - Weighted average: ~5 KB

Peak bandwidth (API):
  51 QPS × 5 KB = 255 KB/s ≈ 2 Mbps

Peak bandwidth (images, via CDN):
  ~500 concurrent users loading restaurant pages
  × 3 images per page × 200 KB per image
  = 300 MB total, served from CDN edge cache
  CDN handles this, not your API server.

Monthly data transfer (API only):
  15,00,000 requests/day × 5 KB × 30 days = ~2.25 TB/month
```

**Talking point:** "2 Mbps peak API bandwidth. A basic AWS instance gets you 5 Gbps. Bandwidth is never your bottleneck at this scale. Images are, and that's why CDNs exist."

---

## Database Capacity Check

| Question | Answer |
|----------|--------|
| Can one PostgreSQL handle 51 QPS? | **Easily.** PostgreSQL handles 10,000+ simple QPS on modest hardware. We're at 0.5% capacity. |
| At what QPS does one instance struggle? | **~5,000-10,000 QPS** for mixed read/write workloads with complex joins. With read replicas, you can push reads to 50K+ QPS. |
| When would you need read replicas? | **~1,000 read QPS** or when your read latency P99 exceeds SLO. For Tadka, probably around 5-10 lakh daily orders. We add it in Week 2 anyway because it's a teaching moment. |
| When would you need sharding? | **~50,000 write QPS** or when your dataset exceeds what one server's RAM can index. For Tadka, not for years. Maybe never at Bangalore-only scale. |
| What's the bottleneck first? | **Connection count**, then **disk I/O**, then CPU. PostgreSQL defaults to 100 max connections. With connection pooling (PgBouncer), you push that to thousands. Disk I/O matters when your working set exceeds RAM. CPU matters during complex aggregations. |

---

## Summary

| Metric | Estimate | Reality Check |
|--------|:--------:|---------------|
| Daily orders | 1,00,000 | Swiggy does ~20 lakh across India |
| Avg orders/sec | 1.15 | Trivial for any modern API |
| Peak orders/sec | 3.5 | Your laptop handles this |
| Peak API QPS | ~51 | 1% of PostgreSQL capacity |
| Daily storage growth | ~365 MB | ~133 GB/year, comfortable on a 500 GB SSD |
| Year 1 DB size | ~175 GB | No sharding needed |
| Peak bandwidth | ~2 Mbps | AWS minimum is 5 Gbps |
| Images | ~42.5 GB | CDN, not database |

---

## The Punchline

> "All of this fits on one PostgreSQL instance. One .NET API. One server.
>
> So why are we spending 8 weeks building a distributed system?
>
> Because you need to know HOW to scale when the time comes. And the time always comes. Swiggy went from 1 lakh to 20 lakh orders. Zomato from Bangalore to 500 cities. The skill isn't knowing the numbers. The skill is knowing which number tells you it's time to change your architecture.
>
> That number, for most companies, is when your P99 latency starts missing SLOs. Not when a blog post says you should."

---

## Common Student Mistakes

1. **Forgetting peak-to-average.** They calculate 1.15 QPS and think they're done. Ask: "What happens at 8 PM when everyone in Koramangala orders dinner simultaneously?"

2. **Using total MAU instead of DAU.** They divide 2.5 lakh by 86,400 and get confused. Remind them: "Not every user orders every day. DAU is what matters for peak calculations."

3. **Including images in database storage.** They add 42 GB of images to the PostgreSQL estimate. Ask: "Would you store a JPEG in a SQL column? Where should images live?"

4. **Oversizing estimates by 100x.** They assume every user makes 10 API calls per second (like they're running a load test). Remind them: "A user opens the app, scrolls for 30 seconds, places one order, and closes the app. That's maybe 10 requests over 2 minutes."

5. **Jumping to sharding.** They see 1 lakh orders and immediately propose 10 database shards. Ask: "What's 51 QPS divided by 10 shards? 5 QPS per shard. You just created 10x the operational complexity for zero benefit."

## Estimation Cheat Sheet (for interviews)

| Rough Conversion | Value |
|------------------|:-----:|
| Seconds in a day | ~86,400 (~10^5) |
| Seconds in a month | ~2.6 million (~2.5 × 10^6) |
| Seconds in a year | ~31.5 million (~3 × 10^7) |
| 1 KB | ~1,000 bytes |
| 1 MB | ~10^6 bytes |
| 1 GB | ~10^9 bytes |
| 1 TB | ~10^12 bytes |
| 99.9% uptime | ~8.76 hours/year downtime |
| 99.99% uptime | ~52 minutes/year downtime |
| 99.999% uptime | ~5 minutes/year downtime |
