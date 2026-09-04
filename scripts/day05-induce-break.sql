-- Day 5 Beat 1 BREAK: drop the order-history indexes, then print the bad plan.
-- Prerequisite: ~200k rows from scripts/day05-seed-large.sql (16 rows will look fast either way).
-- Reverse with scripts/day05-apply-fix.sql.

DROP INDEX IF EXISTS ordering.ix_orders_customer_id_created_at;
DROP INDEX IF EXISTS ordering.ix_orders_created_at;

-- Proof the break: this is GET /orders?customerId=… ORDER BY created_at DESC LIMIT 10
-- for the 200k seed customer (not Priya).
-- Look for: Seq Scan or Parallel Seq Scan, a Sort node, Execution Time tens-hundreds of ms.
EXPLAIN ANALYZE
SELECT * FROM ordering.orders
WHERE "CustomerId" = '00000000-0000-0000-0000-000000000000'
ORDER BY "CreatedAt" DESC
LIMIT 10;
