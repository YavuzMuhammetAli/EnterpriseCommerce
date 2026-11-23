#!/bin/sh
# Creates every topic of the manifest (ADR-023 section 2). Idempotent: an existing topic is left unchanged.
# Usage: create-topics.sh <manifest>; KAFKA_BOOTSTRAP names the broker (default kafka:9092).
set -eu

bootstrap="${KAFKA_BOOTSTRAP:-kafka:9092}"
manifest="${1:-/topics/topics.txt}"

grep -v '^[[:space:]]*#' "$manifest" | grep -v '^[[:space:]]*$' | while read -r name partitions replication configs; do
  set -- --bootstrap-server "$bootstrap" --create --if-not-exists --topic "$name" --partitions "$partitions" --replication-factor "$replication"
  for config in $(echo "${configs:-}" | tr ',' ' '); do
    set -- "$@" --config "$config"
  done
  /opt/kafka/bin/kafka-topics.sh "$@"
  echo "Topic ready: $name"
done
