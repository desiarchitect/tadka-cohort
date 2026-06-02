-- Day 5 Demo Script: Sequential Scan vs Index Scan

-- 1. First, check the execution plan BEFORE applying the EF Core migration
-- (Or run this after dropping the indexes)
EXPLAIN ANALYZE 
SELECT * FROM ordering.orders 
WHERE "CustomerId" = 'a1b2c3d4-0000-4000-8000-000000000001';

-- EXPECTED RESULT (Without Index):
-- Seq Scan on orders  (cost=0.00..2084.00 rows=10 width=180) (actual time=0.015..12.345 rows=10 loops=1)
-- Filter: (customer_id = 'a1b2c3d4-0000-4000-8000-000000000001'::uuid)
-- Rows Removed by Filter: 99990
-- Planning Time: 0.123 ms
-- Execution Time: 12.380 ms

-- ---------------------------------------------------------

-- 2. Check the execution plan for the composite index (Pagination)
EXPLAIN ANALYZE 
SELECT * FROM ordering.orders 
WHERE "CustomerId" = 'a1b2c3d4-0000-4000-8000-000000000001' 
ORDER BY "CreatedAt" DESC 
LIMIT 10;

-- EXPECTED RESULT (Without Index):
-- Limit  (cost=2084.15..2084.17 rows=10 width=180) (actual time=13.001..13.004 rows=10 loops=1)
--   ->  Sort  (cost=2084.15..2084.17 rows=10 width=180) (actual time=13.000..13.002 rows=10 loops=1)
--         Sort Key: created_at DESC
--         Sort Method: top-N heapsort  Memory: 27kB
--         ->  Seq Scan on orders  (cost=0.00..2084.00 rows=10 width=180) (actual time=0.015..12.345 rows=10 loops=1)

-- ---------------------------------------------------------

-- 3. Now, generate and apply the EF Core migration:
-- dotnet ef migrations add AddPerformanceIndexes
-- dotnet ef database update

-- 4. Run the exact same query again AFTER the indexes are applied
EXPLAIN ANALYZE 
SELECT * FROM ordering.orders 
WHERE "CustomerId" = 'a1b2c3d4-0000-4000-8000-000000000001';

-- EXPECTED RESULT (With Index):
-- Index Scan using "IX_orders_customer_id" on orders  (cost=0.29..8.31 rows=10 width=180) (actual time=0.015..0.020 rows=10 loops=1)
-- Index Cond: (customer_id = 'a1b2c3d4-0000-4000-8000-000000000001'::uuid)
-- Planning Time: 0.123 ms
-- Execution Time: 0.040 ms  <-- Notice the massive drop in Execution Time!

-- ---------------------------------------------------------

-- 5. Run the pagination query AFTER the composite index is applied
EXPLAIN ANALYZE 
SELECT * FROM ordering.orders 
WHERE "CustomerId" = 'a1b2c3d4-0000-4000-8000-000000000001' 
ORDER BY "CreatedAt" DESC 
LIMIT 10;

-- EXPECTED RESULT (With Composite Index):
-- Limit  (cost=0.29..8.31 rows=10 width=180) (actual time=0.015..0.020 rows=10 loops=1)
--   ->  Index Scan using "IX_orders_customer_id_created_at" on orders  (cost=0.29..8.31 rows=10 width=180)
-- Notice there is NO 'Sort' node anymore! The index provides the data pre-sorted.
