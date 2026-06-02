# Connection Pooling Guide (Phase A1.2)

By default, PostgreSQL allows a maximum of 100 concurrent connections. If you run 5 instances of the Tadka API behind a load balancer, and each instance maintains a pool of 30 connections, you will instantly crash the database (`FATAL: sorry, too many clients already`).

To solve this, we use **PgBouncer**, a lightweight connection pooler.

## How It Works
Instead of connecting directly to PostgreSQL (port 5432), your API connects to PgBouncer (port 6432). 

PgBouncer acts as a multiplexer. It might accept 1,000 incoming connections from your APIs, but it only opens 20 actual, persistent connections to PostgreSQL. It rapidly cycles the incoming API requests through those 20 real database connections.

## The 3 Pooling Modes

1. **Session Pooling:** The connection is assigned to the API client for the entire duration of the client's session. (Defeats the purpose for stateless REST APIs).
2. **Transaction Pooling (Tadka's Choice):** The connection is assigned to the API client only for the duration of a single database transaction. When the transaction commits, the connection is instantly returned to the pool for another client to use. **This is the industry standard for high-volume APIs.**
3. **Statement Pooling:** The connection is returned to the pool after every single SQL statement (even if it breaks multi-statement transactions). Rarely used.

## Integrating PgBouncer with EF Core

When using PgBouncer in `Transaction` mode, you **must disable .NET's internal connection pooler**. If you don't, .NET will try to reuse session state that PgBouncer has already wiped, leading to bizarre EF Core exceptions.

### The Standard Connection String
```text
Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local
```
*(.NET maintains its own internal pool of connections to Postgres)*

### The PgBouncer Connection String
```text
Host=localhost;Port=6432;Database=tadka;Username=tadka;Password=tadka_local;Pooling=false;
```
*(Notice we changed the port to **6432** and explicitly set **Pooling=false** so .NET hands over all pooling responsibilities to PgBouncer)*
