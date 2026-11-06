#!/bin/bash
# Script to execute baseline Apache Benchmark (ab) tests against NexusCore backend

TARGET_HOST=${1:-"http://localhost:5000"}
API_KEY=${2:-"agent_sec_live_9f83a2b7c4e1"}

echo "=== Running Apache Benchmark Baseline Tests ==="
echo "Target Host: $TARGET_HOST"

echo "[1/2] Testing GET /api/v1/agent/session/sess_baseline/config (10,000 requests, 100 concurrency)..."
ab -n 10000 -c 100 -H "X-NexusAgent-API-Key: $API_KEY" "$TARGET_HOST/api/v1/agent/session/sess_baseline/config"

echo "[2/2] Testing POST /api/v1/agent/status (5,000 requests, 50 concurrency)..."
ab -n 5000 -c 50 -p telemetry_payload.json -T "application/json" -H "X-NexusAgent-API-Key: $API_KEY" "$TARGET_HOST/api/v1/agent/status"

