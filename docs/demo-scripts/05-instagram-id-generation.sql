-- ==========================================
-- D5-10: Instagram Sharding (Insta-flake)
-- ==========================================

-- 1. Create the PL/pgSQL function to generate the 64-bit ID.
-- This function runs entirely on the physical database shard.
CREATE OR REPLACE FUNCTION generate_instagram_id(OUT result bigint) AS $$
DECLARE
    our_epoch bigint := 1314220021721; -- Instagram's custom epoch (ms)
    seq_id bigint;
    now_millis bigint;
    shard_id int := 5; -- For this demo, let's pretend this physical server is Shard #5
BEGIN
    -- Get current time in milliseconds
    SELECT FLOOR(EXTRACT(EPOCH FROM clock_timestamp()) * 1000) INTO now_millis;
    
    -- Increment the local sequence (max 1024 per ms)
    -- NOTE: In reality, you'd create a sequence `CREATE SEQUENCE insta_sequence;`
    -- and call `nextval('insta_sequence') % 1024`. We mock it here as '42'.
    seq_id := 42; 

    -- Shift bits into place and combine using bitwise OR (|)
    result := (now_millis - our_epoch) << 23;
    result := result | (shard_id << 10);
    result := result | (seq_id);
END;
$$ LANGUAGE PLPGSQL;

-- ==========================================
-- DEMO INSTRUCTIONS
-- ==========================================

-- 2. Generate the ID!
-- When the API inserts a row, it calls this function to get the PK.
SELECT generate_instagram_id() AS generated_id;

-- (Copy the 'generated_id' from the output above, e.g., 3949823948239082)

-- 3. The API Routing Magic
-- In C# or Python, when a user requests this photo ID, we extract the shard ID.
-- We bit-shift RIGHT by 10 (to drop the sequence bits), 
-- then bitwise AND with 8191 (to extract only the 13 shard bits).
-- Replace '3949823948239082' with your actual generated ID!

SELECT (generate_instagram_id() >> 10) & 8191 AS extracted_shard_id;

-- You will see it magically extracts '5', proving that the API instantly knows 
-- this data lives on Server #5 without ever checking a routing table!
