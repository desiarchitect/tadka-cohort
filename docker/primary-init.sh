#!/bin/bash
# Runs once, on a fresh primary cluster (docker-entrypoint-initdb.d). Creates the replication
# role the read replica uses to stream, and allows it through pg_hba. See ADR-016 (Day 5).
set -e

REPL_PASSWORD="${REPLICATOR_PASSWORD:-replicator_pass}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
  CREATE ROLE replicator WITH REPLICATION LOGIN PASSWORD '${REPL_PASSWORD}';
EOSQL

# Let the replicator connect for replication from any container on the compose network.
echo "host replication replicator all scram-sha-256" >> "$PGDATA/pg_hba.conf"
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -c "SELECT pg_reload_conf();"

echo "[primary-init] replication role created and pg_hba updated."
