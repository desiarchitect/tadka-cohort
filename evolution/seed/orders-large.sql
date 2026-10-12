-- Tadka evolution harness: the SAME large order table on every checkpoint (day-03 ... day-16).
--
-- Why: with the 3-restaurant demo seed, every checkpoint is fast and the comparison proves nothing.
-- Real systems are slow because the table is big. This inserts ~200k orders, 20k of them owned by the
-- seeded customer Priya (c1b2c3d4-0001-...), whose history the `history` scenario reads. Priya's id is
-- used because from Day 10 a customer token only ever sees its own orders (ADR-031), so the same query
-- is valid before and after auth.
--
-- The orders table's column names changed on Day 5, when snake_case names were introduced for the owned
-- value objects (Day 3-4: "TotalAmount_Amount", "DeliveryAddress_Line1"; Day 5+: total_amount,
-- delivery_address_line1). This script detects which shape it is talking to, so one file serves
-- every checkpoint. It is idempotent: it does nothing if the table already holds >= 150k orders.
--
-- Run against the PRIMARY after the monolith has started once (its startup applies the migrations):
--   docker exec -i tadka-postgres psql -U tadka -d tadka -v ON_ERROR_STOP=1 < evolution/seed/orders-large.sql

DO $$
DECLARE
    snake      boolean;
    existing   bigint;
    missing    text;
    priya      uuid := 'c1b2c3d4-0001-4000-8000-000000000001';
    total_rows int  := 200000;
    priya_rows int  := 20000;
BEGIN
    SELECT count(*) INTO existing FROM ordering.orders;
    IF existing >= 150000 THEN
        RAISE NOTICE 'orders-large: ordering.orders already has % rows, skipping', existing;
        RETURN;
    END IF;

    SELECT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'ordering' AND table_name = 'orders' AND column_name = 'total_amount'
    ) INTO snake;

    -- Fail loudly (instead of with a cryptic NOT NULL error) if a later branch added a required column
    -- this script does not know about.
    SELECT string_agg(column_name, ', ') INTO missing
    FROM information_schema.columns
    WHERE table_schema = 'ordering' AND table_name = 'orders'
      AND is_nullable = 'NO' AND column_default IS NULL
      AND column_name NOT IN (
          'Id', 'CustomerId', 'RestaurantId', 'Status', 'CreatedAt',
          'total_amount', 'currency', 'delivery_address_line1', 'delivery_address_line2', 'delivery_address_city',
          'delivery_address_pincode', 'delivery_latitude', 'delivery_longitude',
          'TotalAmount_Amount', 'TotalAmount_Currency', 'DeliveryAddress_Line1', 'DeliveryAddress_Line2', 'DeliveryAddress_City',
          'DeliveryAddress_Pincode', 'DeliveryAddress_Latitude', 'DeliveryAddress_Longitude');
    IF missing IS NOT NULL THEN
        RAISE EXCEPTION 'orders-large: ordering.orders has required columns this seed does not fill: %', missing;
    END IF;

    IF snake THEN
        INSERT INTO ordering.orders (
            "Id", "CustomerId", "RestaurantId", "Status", "CreatedAt",
            total_amount, currency,
            delivery_address_line1, delivery_address_line2, delivery_address_city, delivery_address_pincode,
            delivery_latitude, delivery_longitude)
        SELECT
            gen_random_uuid(),
            CASE WHEN g <= priya_rows THEN priya
                 ELSE ('00000000-0000-0000-0000-' || lpad((g % 50)::text, 12, '0'))::uuid END,
            (ARRAY['a1b2c3d4-0001-4000-8000-000000000001'::uuid,
                   'a1b2c3d4-0002-4000-8000-000000000002'::uuid,
                   'a1b2c3d4-0003-4000-8000-000000000003'::uuid])[1 + (g % 3)],
            'Delivered',
            NOW() - ((g % 180) || ' days')::interval - ((g % 1440) || ' minutes')::interval,
            (100 + floor(random() * 900))::numeric(10, 2), 'INR',
            'Flat 402', 'HSR Layout', 'Bangalore', '560102', 12.9141, 77.6411
        FROM generate_series(1, total_rows) AS g;
    ELSE
        INSERT INTO ordering.orders (
            "Id", "CustomerId", "RestaurantId", "Status", "CreatedAt",
            "TotalAmount_Amount", "TotalAmount_Currency",
            "DeliveryAddress_Line1", "DeliveryAddress_Line2", "DeliveryAddress_City", "DeliveryAddress_Pincode",
            "DeliveryAddress_Latitude", "DeliveryAddress_Longitude")
        SELECT
            gen_random_uuid(),
            CASE WHEN g <= priya_rows THEN priya
                 ELSE ('00000000-0000-0000-0000-' || lpad((g % 50)::text, 12, '0'))::uuid END,
            (ARRAY['a1b2c3d4-0001-4000-8000-000000000001'::uuid,
                   'a1b2c3d4-0002-4000-8000-000000000002'::uuid,
                   'a1b2c3d4-0003-4000-8000-000000000003'::uuid])[1 + (g % 3)],
            'Delivered',
            NOW() - ((g % 180) || ' days')::interval - ((g % 1440) || ' minutes')::interval,
            (100 + floor(random() * 900))::numeric(10, 2), 'INR',
            'Flat 402', 'HSR Layout', 'Bangalore', '560102', 12.9141, 77.6411
        FROM generate_series(1, total_rows) AS g;
    END IF;

    RAISE NOTICE 'orders-large: inserted % orders (% for Priya), column style: %',
        total_rows, priya_rows, CASE WHEN snake THEN 'snake_case (Day 5+)' ELSE 'EF default (Day 3-4)' END;
END $$;

ANALYZE ordering.orders;
