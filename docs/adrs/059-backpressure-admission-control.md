# ADR-059: Backpressure (admission control)

**Status:** Accepted  
**Decision:** Semaphore on concurrent requests (`Backpressure:MaxConcurrent`). 0 = off. Over limit → 429 + Retry-After.  
**Why:** Protects thread pool / DB connections better than unbounded accept.  
