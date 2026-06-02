-- Day 5 — induce the seq-scan break: drop the order performance indexes (ADR-014) so the
-- order-history query falls back to a sequential scan. Pair with a large orders table
-- (scripts/day05-seed-large.sql) so the scan is visibly slow under EXPLAIN ANALYZE and k6.
-- Reverse with scripts/day05-apply-fix.sql.

DROP INDEX IF EXISTS ordering.ix_orders_customer_id_created_at;
DROP INDEX IF EXISTS ordering.ix_orders_created_at;

-- Diagnose (expect Seq Scan + a Sort node):
--   EXPLAIN ANALYZE
--   SELECT * FROM ordering.orders
--   WHERE "CustomerId" = '00000000-0000-0000-0000-000000000000'
--   ORDER BY "CreatedAt" DESC LIMIT 10;
