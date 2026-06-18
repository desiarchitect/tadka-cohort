#!/bin/bash
# Read-replica boot (ADR-016, Day 5). On first boot the data dir is empty, so we clone the
# primary with a streaming base backup (-R writes standby.signal + primary_conninfo). On later
# boots the clone already exists, so we just start and the standby resumes streaming.
set -e

DATADIR="/var/lib/postgresql/data"

if [ ! -s "$DATADIR/PG_VERSION" ]; then
  echo "[replica] empty data dir — cloning from primary via pg_basebackup..."
  until gosu postgres pg_basebackup -h postgres -p 5432 -U replicator -D "$DATADIR" -Fp -Xs -R -P; do
    echo "[replica] primary not ready yet, retrying in 2s..."
    sleep 2
  done
  echo "[replica] base backup complete."
fi

# pg_basebackup runs as root above writes files owned by postgres already via gosu; make sure
# the whole data dir is owned correctly and locked down before starting.
chown -R postgres:postgres "$DATADIR"
chmod 0700 "$DATADIR"

echo "[replica] starting hot standby..."
exec gosu postgres postgres