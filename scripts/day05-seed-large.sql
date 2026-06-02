-- Day 5 — bloat the orders table so EXPLAIN ANALYZE and the dinner-rush load test actually bite.
-- ~200k orders spread across 50 synthetic customers (≈4k orders each) and the 3 seeded restaurants,
-- with created_at spread over ~180 days. CustomerId has no cross-schema FK (ADR-008), so any UUID
-- is valid. Run against the PRIMARY (port 5432); it streams to the replica via WAL.
--
--   psql "Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local" -f scripts/day05-seed-large.sql
--
-- The order-history EXPLAIN demo then uses customer '00000000-0000-0000-0000-000000000000'.

INSERT INTO ordering.orders (
    "CustomerId", "RestaurantId", "Status", total_amount, currency,
    delivery_address_line1, delivery_address_line2, delivery_address_city, delivery_address_pincode,
    delivery_latitude, delivery_longitude, "CreatedAt")
SELECT
    ('00000000-0000-0000-0000-' || lpad((g % 50)::text, 12, '0'))::uuid,
    (ARRAY[
        'a1b2c3d4-0001-4000-8000-000000000001'::uuid,
        'a1b2c3d4-0002-4000-8000-000000000002'::uuid,
        'a1b2c3d4-0003-4000-8000-000000000003'::uuid
    ])[1 + (g % 3)],
    'Created',
    (100 + floor(random() * 900))::numeric(10, 2),
    'INR',
    'Flat 402', 'HSR Layout', 'Bangalore', '560102',
    12.9141, 77.6411,
    NOW() - ((g % 180) || ' days')::interval
FROM generate_series(1, 200000) AS g;

ANALYZE ordering.orders;
