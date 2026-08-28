# ADR-010: API Versioning via URL Path

**Date:** 2026-06-01
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** How do we version the public HTTP contract **before** the first client depends on it?

**Options:**
1. No versioning (`/api`). Pray.
2. URL path (`/api/v1`).
3. Header / media-type (`Accept: application/vnd.tadka.v1+json`).
4. Query (`?version=1`).

**Choice:** Option 2. All routes under `/api/v1`. Additive changes (new optional field, new endpoint) do **not** bump. Breaking changes (rename, remove, change meaning) ship as `/api/v2`. Previous major lives ~6 months with `Deprecation` / `Sunset` headers.

**Why:** Phones cannot be force-upgraded. Versioning is cheapest before the first consumer, not after. The version is in logs, in curl, in the browser. Header versioning is theoretically cleaner REST and worse at 3am.

**Trade-off:** The URI names a representation, not only a resource. Two live majors means two test surfaces for the overlap window. That cost must be budgeted before promising v2.

**Failure mode:** Teams bump `/v2` for additive fields (sprawl), or sneak a breaking change into v1. A version that never sunsets is a permanent tax — `/v1` `/v2` `/v3` all live, nobody knows who still calls v1.

**Revisit when:** Tadka is a public third-party API product (then content-negotiation may win); or the dual-running bill exceeds URL simplicity.
