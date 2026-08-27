-- Day 5, Beat 4 — Partitioning: pruning (the win) vs partitioning by a mutable key (the trap).
-- Run against the PRIMARY after scripts/day05-seed-large.sql (200k orders, ~180 days spread).
--
--   docker cp scripts/day05-partition-demo.sql tadka-postgres:/tmp/partition-demo.sql
--   docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/partition-demo.sql
--
-- Everything here is a SEPARATE table from ordering.orders — nothing about the running app
-- changes. This is "would partitioning help, and where does it bite" as a standalone experiment,
-- the same way 03-table-partitioning.sql was, but at REAL scale (200k rows, not 3) so the numbers
-- are worth capturing, and with the mutable-key anti-pattern this Beat adds.

-- ============================================================
-- PART A — Partition by CREATED_AT (immutable once written): pruning
-- ============================================================

DROP TABLE IF EXISTS ordering.orders_by_month;
CREATE TABLE ordering.orders_by_month (
    id UUID NOT NULL,
    customer_id UUID NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    total_amount NUMERIC(10,2) NOT NULL,
    PRIMARY KEY (id, created_at)
) PARTITION BY RANGE (created_at);

-- 7 monthly partitions comfortably covers "now - 180 days" .. "now".
DO $$
DECLARE
    month_start date := date_trunc('month', now() - interval '7 months');
    i int;
BEGIN
    FOR i IN 0..7 LOOP
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS ordering.orders_by_month_%s PARTITION OF ordering.orders_by_month
                 FOR VALUES FROM (%L) TO (%L)',
            to_char(month_start + (i || ' months')::interval, 'YYYY_MM'),
            month_start + (i || ' months')::interval,
            month_start + ((i + 1) || ' months')::interval
        );
    END LOOP;
END $$;

INSERT INTO ordering.orders_by_month (id, customer_id, created_at, total_amount)
SELECT gen_random_uuid(), "CustomerId", "CreatedAt", total_amount
FROM ordering.orders;

ANALYZE ordering.orders_by_month;

\echo '--- A1: no date filter -> scans EVERY partition (no pruning possible) ---'
EXPLAIN ANALYZE
SELECT count(*) FROM ordering.orders_by_month WHERE customer_id = '00000000-0000-0000-0000-000000000001';

\echo '--- A2: date filter matching ONE month -> only that partition is touched (pruning) ---'
EXPLAIN ANALYZE
SELECT count(*) FROM ordering.orders_by_month
WHERE customer_id = '00000000-0000-0000-0000-000000000001'
  AND created_at >= date_trunc('month', now())
  AND created_at <  date_trunc('month', now()) + interval '1 month';

-- ============================================================
-- PART B — Partition by STATUS (mutable): the anti-pattern
-- ============================================================
-- created_at never changes after insert, so a row is written once and stays in its partition
-- forever — pruning is free. status changes constantly (Created -> ... -> Delivered). Postgres
-- DOES support this (row movement, since PG11) but every transition that crosses a partition
-- boundary is executed as a DELETE from the old partition + INSERT into the new one, not a
-- normal in-place UPDATE. That is strictly more expensive, and it silently breaks anything that
-- assumes an UPDATE is an UPDATE (e.g. logical-replication row identity, some trigger patterns).

DROP TABLE IF EXISTS ordering.orders_by_status;
CREATE TABLE ordering.orders_by_status (
    id UUID NOT NULL,
    customer_id UUID NOT NULL,
    status VARCHAR(20) NOT NULL,
    total_amount NUMERIC(10,2) NOT NULL,
    PRIMARY KEY (id, status)
) PARTITION BY LIST (status);

CREATE TABLE ordering.orders_by_status_created      PARTITION OF ordering.orders_by_status FOR VALUES IN ('Created');
CREATE TABLE ordering.orders_by_status_confirmed     PARTITION OF ordering.orders_by_status FOR VALUES IN ('Confirmed');
CREATE TABLE ordering.orders_by_status_preparing     PARTITION OF ordering.orders_by_status FOR VALUES IN ('Preparing');
CREATE TABLE ordering.orders_by_status_delivered     PARTITION OF ordering.orders_by_status FOR VALUES IN ('Delivered');
CREATE TABLE ordering.orders_by_status_cancelled     PARTITION OF ordering.orders_by_status FOR VALUES IN ('Cancelled');

-- 20k rows, in two 10k halves so B1 and B2 each get their own untouched 5,000-row batch to
-- update — an apples-to-apples comparison, not one batch mutated twice.
INSERT INTO ordering.orders_by_status (id, customer_id, status, total_amount)
SELECT gen_random_uuid(), "CustomerId", 'Created', total_amount
FROM ordering.orders
LIMIT 20000;

ANALYZE ordering.orders_by_status;

-- Wall-clock \timing on a 5,000-row update is too noisy on a small local container (cache
-- warmth between runs swamps the signal). Buffers + WAL from EXPLAIN ANALYZE are deterministic
-- regardless of cache state and measure the actual work done — read that instead of the clock.

\echo '--- B1: 5,000-row BULK update, SAME partition (total_amount only, status unchanged) ---'
EXPLAIN (ANALYZE, BUFFERS, WAL)
UPDATE ordering.orders_by_status
SET total_amount = total_amount + 1
WHERE id IN (SELECT id FROM ordering.orders_by_status_created ORDER BY id LIMIT 5000);

\echo '--- B2: 5,000-row BULK update, CROSS partition (status Created -> Delivered = row MOVES) ---'
-- A different, still-untouched 5,000-row batch from the same source partition (offset past B1's
-- rows), so this measures the SAME kind of work at the SAME scale — only the destination differs.
EXPLAIN (ANALYZE, BUFFERS, WAL)
UPDATE ordering.orders_by_status
SET status = 'Delivered'
WHERE id IN (SELECT id FROM ordering.orders_by_status_created ORDER BY id OFFSET 5000 LIMIT 5000);
-- Compare "Buffers:" (B2 touches the target partition's heap AND its index in addition to the
-- source partition's, where B1 only ever touches orders_by_status_created) and "WAL:" (B2's
-- DELETE-from-old + INSERT-into-new are two full WAL records per row, vs B1's one UPDATE record).

-- Cleanup: these are throwaway experiment tables, not part of the running app's schema.
-- DROP TABLE ordering.orders_by_month;
-- DROP TABLE ordering.orders_by_status;
