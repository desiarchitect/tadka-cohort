---
name: resilience-review
description: Audit the selected code for timeouts, retries, circuit breakers, fallbacks and queue behaviour
agent: ask
---

Review the resilience of this code: ${selection}

Use these Tadka rules as the standard (see `src/Tadka.Payment.Api/Resilience/PaymentResiliencePipeline.cs` for the reference implementation):

1. **Timeout.** Every outbound call has an explicit timeout (the payment gateway uses 2 s per attempt). A default `HttpClient` waits 100 seconds.
2. **Retry.** Exponential backoff with jitter, a small cap (2), and **only for transport failures** such as timeouts and unavailable services. A business answer (a 400, a card decline) is never retried.
3. **Circuit breaker.** Failure ratio around 0.5, a **minimum number of calls** (5) before it can open, a sampling window (30 s) and an open time (60 s). A decline must not count as a failure.
4. **Bulkhead.** A cap on concurrent calls so one slow dependency cannot use every thread.
5. **Fallback (ADR-044).** Redis and the read replica are performance dependencies: fall back to the database. Postgres and money are correctness dependencies: fail honestly and never serve stale money.
6. **Queues.** For a Kafka consumer: is it idempotent (inbox)? What happens when it falls behind? Backlog grows silently, so say how it would be noticed (consumer lag).

Answer as a table: **Dependency**, **What can fail**, **Timeout**, **Retry**, **Breaker**, **Fallback**, **Gap**. Then show corrected code only for the gaps.
