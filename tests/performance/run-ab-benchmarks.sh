#!/usr/bin/env bash
set -euo pipefail

TARGET_HOST="${1:-http://localhost:5000}"
TARGET_HOST="${TARGET_HOST%/}"
REQUESTS="${AB_REQUESTS:-1000}"
CONCURRENCIES="${AB_CONCURRENCIES:-5 20 50}"
read -r -a concurrency_levels <<< "$CONCURRENCIES"

if ! command -v ab >/dev/null 2>&1; then
  echo "Apache Benchmark (ab) is required but was not found in PATH." >&2
  exit 1
fi

echo "=== NexusCore read-only Apache Benchmark runs ==="
echo "Target: $TARGET_HOST"
echo "Requests per run: $REQUESTS"

for concurrency in "${concurrency_levels[@]}"; do
  echo "--- GET /api/health (concurrency $concurrency) ---"
  ab -n "$REQUESTS" -c "$concurrency" "$TARGET_HOST/api/health"

  echo "--- GET /api/games?limit=20 (concurrency $concurrency) ---"
  ab -n "$REQUESTS" -c "$concurrency" "$TARGET_HOST/api/games?limit=20"

  echo "--- GET /api/v1/games?page=1&limit=20 (concurrency $concurrency) ---"
  ab -n "$REQUESTS" -c "$concurrency" "$TARGET_HOST/api/v1/games?page=1&limit=20"
done
