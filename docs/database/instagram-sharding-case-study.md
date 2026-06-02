# System Design Case Study: Instagram's Application-Level Sharding

When teaching Database Scaling, Sharding is universally listed as the "Last Resort". However, when a company like Instagram hits billions of rows, they have no choice but to shard.

Interestingly, Instagram did **not** use massive enterprise sharding tools like Citus. They built a brilliant Application-Level sharding engine natively on top of PostgreSQL.

## The Challenge
If you shard a database (split the data across 100 different physical servers), you instantly hit two massive roadblocks:
1. **Routing:** How does the API know which server has the data? A central "lookup table" becomes a massive bottleneck.
2. **Primary Keys:** You can no longer use `SERIAL` (Auto-Increment 1, 2, 3...) because two different servers will both generate `ID 1`, causing collisions. 

You *could* use `UUID`s, but random strings heavily fragment PostgreSQL B-Tree indexes, making Inserts excruciatingly slow at a massive scale.

## The Solution: The "Insta-flake" ID
Instagram solved both problems by ditching UUIDs and creating a custom **64-bit integer** that is generated locally by a PL/pgSQL function on the exact database shard where the data is being inserted.

They split the 64-bit integer into 3 logical components:

| Component | Bits | Description |
| :--- | :--- | :--- |
| **Timestamp** | 41 bits | Custom epoch time in milliseconds. This guarantees the IDs are naturally chronological/sortable, which keeps B-Tree indexes fast and healthy! |
| **Logical Shard ID** | 13 bits | The exact ID of the schema/server where this data lives (Allows up to 8,192 shards). |
| **Sequence** | 10 bits | A local auto-incrementing number (Allows 1024 unique IDs per millisecond, per shard). |

## The Magic Routing
Because the **Shard ID** is physically baked right into the middle of the integer, the C#/Python API doesn't need a lookup table! 

When a user requests `Photo ID 138374928`, the API intercepts the request, runs a rapid bitwise operation (`ID >> 10 & 8191`), instantly extracts the Shard ID, and routes the SQL query directly to that exact physical server!
