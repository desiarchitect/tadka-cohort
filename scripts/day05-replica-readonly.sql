-- Day 5 Beat 3: prove the replica will not take writes.
-- Run on tadka-postgres-replica. Must fail with:
--   ERROR: cannot execute INSERT in a read-only transaction
-- If you see column "id" does not exist, you used unquoted id via docker exec -c
-- (Windows strips "Id"). This file keeps the quotes.
-- Columns are EF PascalCase. Address fields are NOT NULL but Postgres
-- rejects the write before it checks constraints.

INSERT INTO restaurant.restaurants ("Id", "Name")
VALUES (gen_random_uuid(), 'should-fail');
