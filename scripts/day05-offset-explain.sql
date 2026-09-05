-- Day 5 Beat 6: OFFSET walks thousands of rows then throws them away.
-- Run on PRIMARY (tadka-postgres). 200k seed customer, not Priya.
-- Look for: actual rows in the thousands, Execution Time tens–hundreds of ms.

EXPLAIN ANALYZE
SELECT * FROM ordering.orders
WHERE "CustomerId" = '00000000-0000-0000-0000-000000000000'
ORDER BY "CreatedAt" DESC
LIMIT 5 OFFSET 3995;
