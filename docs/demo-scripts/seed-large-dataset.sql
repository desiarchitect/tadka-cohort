-- Tadka Demo Seed Script: 100,000 Orders for EXPLAIN ANALYZE Demo
-- Run this script against the 'tadka' database using pgAdmin or psql before applying the Week 3 EF Core Migrations.

DO $$ 
DECLARE
    -- The specific customer ID we will use in our EXPLAIN ANALYZE demo query
    demo_customer_id UUID := 'a1b2c3d4-0000-4000-8000-000000000001';
    -- Meghana Foods (seeded in Day 1)
    demo_restaurant_id UUID := 'a1b2c3d4-0001-4000-8000-000000000001'; 
BEGIN
    RAISE NOTICE 'Starting database seed. This may take 5-10 seconds...';

    -- 1. Insert 100,000 dummy orders with random customer IDs
    INSERT INTO ordering.orders (
        "Id", "CustomerId", "RestaurantId", "Status", 
        total_amount, currency, 
        delivery_address_line1, delivery_address_line2, delivery_address_city, delivery_address_pincode, 
        delivery_latitude, delivery_longitude, 
        "CreatedAt"
    )
    SELECT 
        gen_random_uuid(),
        gen_random_uuid(), -- Generate completely random customer UUIDs
        demo_restaurant_id, 
        'Delivered',
        (random() * 1000 + 100)::decimal(10,2), -- Random amount between 100 and 1100
        'INR',
        'Dummy Street ' || g, '', 'Bangalore', '560001', 
        12.9352, 77.6245,
        NOW() - (random() * (interval '180 days')) -- Random date within the last 6 months
    FROM generate_series(1, 100000) AS g;

    -- 2. Insert exactly 10 specific orders for our demo customer
    -- We do this so the EXPLAIN ANALYZE query actually finds records when we run it
    INSERT INTO ordering.orders (
        "Id", "CustomerId", "RestaurantId", "Status", 
        total_amount, currency, 
        delivery_address_line1, delivery_address_line2, delivery_address_city, delivery_address_pincode, 
        delivery_latitude, delivery_longitude, 
        "CreatedAt"
    )
    SELECT 
        gen_random_uuid(),
        demo_customer_id, 
        demo_restaurant_id, 
        'Delivered',
        (random() * 1000 + 100)::decimal(10,2),
        'INR',
        '123 Demo Customer House', '', 'Bangalore', '560001', 
        12.9352, 77.6245,
        NOW() - (random() * (interval '30 days'))
    FROM generate_series(1, 10);

    RAISE NOTICE 'Database seed completed successfully! 100,010 orders inserted.';
END $$;
