-- ==========================================
-- D5-10: Conceptual Sharding Demo
-- ==========================================
-- NOTE: True sharding involves multiple physical servers (or extensions like Citus).
-- This script uses schemas to conceptually simulate how an Application-Level router 
-- would split data across two distinct "shards" (East and West).

-- 1. Create two identical schemas representing two different physical database shards
CREATE SCHEMA IF NOT EXISTS shard_east;
CREATE SCHEMA IF NOT EXISTS shard_west;

CREATE TABLE IF NOT EXISTS shard_east.orders (
    id UUID PRIMARY KEY,
    customer_id UUID NOT NULL,
    total_amount DECIMAL(10,2) NOT NULL
);

CREATE TABLE IF NOT EXISTS shard_west.orders (
    id UUID PRIMARY KEY,
    customer_id UUID NOT NULL,
    total_amount DECIMAL(10,2) NOT NULL
);

-- 2. Simulate the C# Application-Level Router logic
-- In C#, we would hash the CustomerId. If even, save to Db1 (East). If odd, save to Db2 (West).
DO $$ 
DECLARE
    cust1 UUID := 'a1b2c3d4-0000-4000-8000-000000000001'; -- "East" customer
    cust2 UUID := 'f9e8d7c6-0000-4000-8000-000000000002'; -- "West" customer
BEGIN
    -- Application routes cust1 to East Shard
    INSERT INTO shard_east.orders (id, customer_id, total_amount) VALUES (gen_random_uuid(), cust1, 100.00);
    INSERT INTO shard_east.orders (id, customer_id, total_amount) VALUES (gen_random_uuid(), cust1, 150.00);
    
    -- Application routes cust2 to West Shard
    INSERT INTO shard_west.orders (id, customer_id, total_amount) VALUES (gen_random_uuid(), cust2, 200.00);
END $$;

-- 3. DEMO 1: Targeted Query (Fast)
-- The API knows Cust1 belongs to the East Shard, so it only queries Db1.
SELECT * FROM shard_east.orders WHERE customer_id = 'a1b2c3d4-0000-4000-8000-000000000001';

-- 4. DEMO 2: The Cross-Shard Query (The Nightmare)
-- "Give me the top 10 most expensive orders across all users."
-- Because the data is split across servers, the application (or a proxy) must query ALL shards, 
-- pull the data into memory, and manually sort/filter it.
-- This is simulated here with a UNION ALL:
EXPLAIN ANALYZE
SELECT * FROM (
    SELECT * FROM shard_east.orders
    UNION ALL
    SELECT * FROM shard_west.orders
) all_shards
ORDER BY total_amount DESC
LIMIT 10;
-- Notice the massive overhead: Postgres has to Append the datasets together and perform a memory Sort.
-- This perfectly illustrates WHY sharding is the absolute last resort in our Scaling Tree!
