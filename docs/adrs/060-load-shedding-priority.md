# ADR-060: Load shedding by priority

**Status:** Accepted  
**Decision:** When `LoadShed:Enabled`, non-critical paths return 503; critical (auth, orders, payments, deliveries, health) continue.  
**Why:** Survive spikes by protecting revenue path.  
