# ADR-002: Start with a Monolith

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** How does Tadka start: one deployable, or many services on day one?

**Options:**
1. Microservices from day one (one process per domain, many databases).
2. Modular monolith (one deploy, separate assemblies and hard module walls).
3. Single API + one Postgres, domain folders as future seams.

**Choice:** Option 3, with option 2's *discipline* (folders, schemas, no cross-domain shortcuts). One `Tadka.Api`, one `TadkaDbContext`, one compose file, one pipeline.

**Why:** Team of a few engineers, write load about 11 orders/sec, no measured bottleneck. Five services, a broker, and a gateway before the first order is over-engineering. Swiggy-class companies started as one app.

**Trade-off:** One deploy: a restaurant bug can take down place-order. One connection pool and one backup for everything. Independent scale of one domain waits. Extraction later is a move, not a rewrite, only if we keep the seams.

**Failure mode:** Folder boundaries blur, everything imports everything, and extraction becomes a rewrite. Or traffic 10×s and we extract too late. Health checks that ignore the database make the dashboard green while orders fail.

**Revisit when:** Team is clearly past ~8 people with ownership fights; a load test names one domain as the bottleneck; or Payment needs physical isolation (fault / PCI), not just a folder.
