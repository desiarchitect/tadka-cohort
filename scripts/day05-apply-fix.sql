-- Day 5 — apply the fix: re-create the order performance indexes (ADR-014). This mirrors the
-- AddPerformanceIndexes EF migration; the toggle exists so the break is repeatable live without
-- re-running migrations. Re-run the EXPLAIN from day05-induce-break.sql afterwards.

CREATE INDEX IF NOT EXISTS ix_orders_customer_id_created_at
    ON ordering.orders ("CustomerId", "CreatedAt" DESC);

CREATE INDEX IF NOT EXISTS ix_orders_created_at
    ON ordering.orders ("CreatedAt" DESC);

ANALYZE ordering.orders;

-- Diagnose again (expect Index Scan using ix_orders_customer_id_created_at, no Sort node):
--   EXPLAIN ANALYZE
--   SELECT * FROM ordering.orders
--   WHERE "CustomerId" = '00000000-0000-0000-0000-000000000000'
--   ORDER BY "CreatedAt" DESC LIMIT 10;
