#!/usr/bin/env bash
# Bootstraps SASL/SCRAM-SHA-256 auth on the single-node KRaft broker (ADR-027/day-09 SASL fix).
#
# The vanilla apache/kafka image's entrypoint chain is run -> configure -> launch, where `launch`
# calls the Java class `kafka.docker.KafkaDockerWrapper setup` to BOTH translate KAFKA_* env vars
# into /opt/kafka/config/server.properties AND format storage, as one atomic step, then execs
# kafka-server-start. There's no hook to pass --add-scram into that step.
#
# So this script replicates run+configure (the parts we need), calls the same KafkaDockerWrapper
# setup command `launch` would have called, then runs `kafka-storage.sh format --add-scram
# ... --ignore-formatted` AFTERWARDS to seed the SCRAM user into the now-formatted metadata log
# (KIP-900 supports re-running format with --add-scram on top of an already-formatted cluster
# specifically for this), then writes a client.properties file every `docker exec` CLI command
# can point --command-config at, then starts the broker itself.
set -euo pipefail

# A restarted container (Docker Desktop restart, `docker compose stop` then `up`) keeps its /tmp, so the
# storage formatted by the previous run is still there and KafkaDockerWrapper's own format step below
# would stop with "already formatted". Start every run from empty storage. This container has no
# persistent volume by design, so nothing is lost that a restart did not already lose.
rm -rf /tmp/kafka-logs

. /etc/kafka/docker/bash-config
. /etc/kafka/docker/configureDefaults
. /etc/kafka/docker/configure

/opt/kafka/bin/kafka-run-class.sh kafka.docker.KafkaDockerWrapper setup \
  --default-configs-dir /etc/kafka/docker \
  --mounted-configs-dir /mnt/shared/config \
  --final-configs-dir /opt/kafka/config

SERVER_PROPERTIES=/opt/kafka/config/server.properties
SASL_PASSWORD="${KAFKA_SASL_PASSWORD:-tadka_kafka_local}"

# KafkaDockerWrapper's own setup step above already formatted storage (without a SCRAM user) as
# a side effect of writing server.properties — --add-scram only takes effect on a genuinely FRESH
# format (KIP-900); --ignore-formatted on an already-formatted log just skips formatting entirely,
# it does not layer --add-scram on top. So wipe that first format and redo it ourselves, this time
# with --add-scram, before the broker ever starts. Safe: this container has no persistent volume,
# so nothing survives a restart anyway.
rm -rf /tmp/kafka-logs

/opt/kafka/bin/kafka-storage.sh format \
  --cluster-id "${CLUSTER_ID}" \
  --config "$SERVER_PROPERTIES" \
  --add-scram "SCRAM-SHA-256=[name=tadka,password=${SASL_PASSWORD}]"

cat > /etc/kafka/docker/client.properties <<EOF
security.protocol=SASL_PLAINTEXT
sasl.mechanism=SCRAM-SHA-256
sasl.jaas.config=org.apache.kafka.common.security.scram.ScramLoginModule required username="tadka" password="${SASL_PASSWORD}";
EOF

exec /opt/kafka/bin/kafka-server-start.sh "$SERVER_PROPERTIES"
