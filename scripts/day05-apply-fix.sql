-- Day 5 Beat 1 FIX: put the ADR-014 indexes back (same as AddPerformanceIndexes),
-- ANALYZE, then print the good plan. Same query as induce-break.

CREATE INDEX IF NOT EXISTS ix_orders_customer_id_created_at
    ON ordering.orders ("CustomerId", "CreatedAt" DESC);

CREATE INDEX IF NOT EXISTS ix_orders_created_at
    ON ordering.orders ("CreatedAt" DESC);

ANALYZE ordering.orders;

-- Proof the fix: Index Scan using ix_orders_customer_id_created_at, no Sort,
-- Execution Time much smaller (ratio is the lesson; ~116 ms → ~5 ms on the capture laptop).
EXPLAIN ANALYZE
SELECT * FROM ordering.orders
WHERE "CustomerId" = '00000000-0000-0000-0000-000000000000'
ORDER BY "CreatedAt" DESC
LIMIT 10;
