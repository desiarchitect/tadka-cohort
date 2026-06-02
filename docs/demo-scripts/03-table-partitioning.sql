-- ==========================================
-- D5-9: Table Partitioning Demo (PostgreSQL)
-- ==========================================

-- 1. Create the parent partitioned table
-- Notice we must include the partition key (created_at) in the Primary Key!
CREATE TABLE IF NOT EXISTS ordering.orders_partitioned (
    id UUID NOT NULL DEFAULT gen_random_uuid(),
    customer_id UUID NOT NULL,
    restaurant_id UUID NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Created',
    total_amount DECIMAL(10,2) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (id, created_at)
) PARTITION BY RANGE (created_at);

-- 2. Create the child partitions by month
CREATE TABLE IF NOT EXISTS ordering.orders_2025_01 PARTITION OF ordering.orders_partitioned
    FOR VALUES FROM ('2025-01-01') TO ('2025-02-01');

CREATE TABLE IF NOT EXISTS ordering.orders_2025_02 PARTITION OF ordering.orders_partitioned
    FOR VALUES FROM ('2025-02-01') TO ('2025-03-01');

CREATE TABLE IF NOT EXISTS ordering.orders_2025_03 PARTITION OF ordering.orders_partitioned
    FOR VALUES FROM ('2025-03-01') TO ('2025-04-01');

-- 3. Insert some dummy data spanning different months
INSERT INTO ordering.orders_partitioned (customer_id, restaurant_id, total_amount, created_at)
VALUES 
    (gen_random_uuid(), gen_random_uuid(), 500, '2025-01-15 10:00:00'),
    (gen_random_uuid(), gen_random_uuid(), 300, '2025-02-14 19:00:00'),
    (gen_random_uuid(), gen_random_uuid(), 750, '2025-03-10 12:30:00');

-- 4. DEMO 1: A query without the partition key (The Trap)
-- This forces Postgres to scan ALL partitions because it doesn't know where the data is.
EXPLAIN ANALYZE
SELECT * FROM ordering.orders_partitioned 
WHERE customer_id = 'a1b2c3d4-0000-4000-8000-000000000001'; 
-- Notice in the output: It scans orders_2025_01, orders_2025_02, AND orders_2025_03.

-- 5. DEMO 2: A query with the partition key (Partition Pruning)
-- This is where the magic happens.
EXPLAIN ANALYZE
SELECT * FROM ordering.orders_partitioned 
WHERE customer_id = 'a1b2c3d4-0000-4000-8000-000000000001'
AND created_at >= '2025-02-01' AND created_at < '2025-03-01';
-- Notice in the output: It ONLY scans orders_2025_02! The other partitions are completely ignored.
