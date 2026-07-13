# ADR-058: Feature flags (percentage rollout)

**Status:** Accepted  
**Context:** Deploy unfinished or risky paths without a full cutover; canary companion to ADR-061.  
**Decision:** Stable SHA256 hash of `flag|userKey` into bucket 0–99; enable if bucket &lt; percent. Percent from Redis `Flags:{name}` or config `Flags:{name}`. Admin PUT `/api/v1/flags/{name}`.  
**Consequences:** Same user stays in cohort when % rises; no sticky sessions required.  
