# Back-of-Envelope Estimation — Tadka

> Estimation is not about getting the exact number. It's about getting within an order of magnitude.
> If your answer is 10 req/s and the real answer is 15 req/s, you're golden.
> If your answer is 10 req/s and the real answer is 10,000 req/s, you have a problem.

## Assumptions

| Parameter | Your Estimate | Unit |
|-----------|:------------:|------|
| Target city | | (e.g., Bangalore) |
| Target daily orders | | orders/day |
| Average orders per user per month | | |
| DAU (Daily Active Users) | | |
| MAU (Monthly Active Users) | | |
| Peak-to-average ratio | | x |
| Read-to-write ratio | | : |

---

## Traffic Estimation

### Orders
- Daily orders: ___
- Average orders/second: ___ (daily orders / 86,400 seconds)
- Peak orders/second: ___ (avg × peak ratio)

### API Requests
- Reads per order (browsing, menu views, tracking): ___ requests
- Total daily API requests: ___ (daily orders × reads per order + writes)
- Average QPS: ___
- Peak QPS: ___

---

## Storage Estimation

### Per-Record Sizes (estimate in bytes)

| Record Type | Estimated Size | Notes |
|------------|:--------------:|-------|
| User profile | ___ bytes | name, email, phone, address, preferences |
| Restaurant | ___ bytes | name, address, cuisine, hours, ratings |
| Menu item | ___ bytes | name, description, price, image ref, category |
| Order | ___ bytes | user, restaurant, items, status, timestamps, address |
| Payment | ___ bytes | order ref, amount, method, status, timestamps |
| Delivery tracking | ___ bytes | order ref, driver, GPS updates, status |
| Rating/Review | ___ bytes | user, restaurant, order ref, stars, text |

### Total Storage

| Data | Calculation | Daily | Yearly |
|------|------------|:-----:|:------:|
| Orders | ___ orders × ___ bytes | | |
| Users | ___ users × ___ bytes | (one-time) | |
| Restaurants | ___ restaurants × ___ bytes | (one-time) | |
| Menu items | ___ items × ___ bytes | (one-time) | |
| Reviews | ___ reviews × ___ bytes | | |
| **Total** | | | |

### Image Storage

| Type | Count | Avg Size | Total |
|------|:-----:|:--------:|:-----:|
| Restaurant photos | | ___ MB | |
| Menu item photos | | ___ KB | |
| **Total** | | | |

---

## Bandwidth Estimation

| Metric | Calculation | Result |
|--------|-----------|:------:|
| Average API response | | ___ KB |
| Peak bandwidth (egress) | peak QPS × avg response | ___ MB/s |
| Monthly data transfer | | ___ GB |

---

## Database Capacity Check

| Question | Your Answer |
|----------|------------|
| Can a single PostgreSQL handle this QPS? | |
| At what QPS does one instance struggle? | |
| When would you need read replicas? | |
| When would you need sharding? | |
| What's the bottleneck first: CPU, RAM, or disk? | |

---

## Summary

| Metric | Estimate |
|--------|:--------:|
| Daily orders | |
| Peak orders/second | |
| Peak API QPS | |
| Daily storage growth | |
| Year 1 total storage | |
| Can one DB handle it? | |
