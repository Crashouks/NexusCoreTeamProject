# Benchmark and Stress Testing Report

## Executive Summary
This document outlines the performance, throughput, and latency characteristics of the NexusCore backend under simulated workloads. Testing was executed using **Apache Benchmark (`ab`)** for baseline throughput measurements and **k6** for multi-stage concurrent virtual user (VU) stress testing.

---

## 1. Test Environment & Setup

* **Backend Environment:** .NET 8 Web API (`NexusCore.Api`)
* **Hosting Configuration:** Kestrel Web Server (4 vCPU, 8 GB RAM, Linux container)
* **Database:** PostgreSQL 16
* **Network Latency:** Local loopback / Isolated 10Gbps Virtual Network
* **Test Tools:**
  * Apache Benchmark (`ab` v2.3)
  * k6 (v0.49.0)

---

## 2. Apache Benchmark (ab) Baseline Testing

Baseline testing targeted lightweight REST endpoints (`GET /health` and `GET /api/v1/agent/session/sess_test/config`) to measure raw web server request-handling capability.

### Command Execution
```bash
ab -n 10000 -c 100 -H "X-NexusAgent-API-Key: agent_sec_live_9f83a2b7c4e1" http://localhost:5000/api/v1/agent/session/sess_test/config
```

### Key Metrics Summary
| Metric | Benchmark Target | Measured Value | Status |
| :--- | :--- | :--- | :--- |
| **Total Requests** | 10,000 | 10,000 | Completed |
| **Concurrency Level** | 100 | 100 | Pass |
| **Time Taken for Tests** | - | 1.84 seconds | - |
| **Requests Per Second (RPS)** | > 4,000 req/sec | **5,434.78 req/sec** | Pass |
| **Mean Time per Request** | < 25 ms | **18.40 ms** (across concurrent) | Pass |
| **Across-connection Mean** | < 0.5 ms | **0.184 ms** | Pass |
| **Transfer Rate** | - | 2,840.12 KB/sec | Pass |

### Percentile Latency Distribution
```text
  50%     14ms
  66%     17ms
  75%     19ms
  80%     21ms
  90%     26ms
  95%     32ms
  98%     45ms
  99%     61ms
 100%    112ms (longest request)
```

---

## 3. k6 Multi-Stage Stress Testing

Stress testing evaluated system scalability under ramping load profiles (up to 1,000 concurrent virtual users) across authentication, session config fetching, and telemetry status reporting (`POST /api/v1/agent/status`).

### Test Stages
1. **Ramp-up:** 0 to 200 VUs over 2 minutes
2. **Sustained Load:** 200 to 500 VUs over 5 minutes
3. **Peak Stress:** 500 to 1,000 VUs over 3 minutes
4. **Ramp-down:** 1,000 down to 0 VUs over 2 minutes

### Visual Load & Response Time Profile
```text
VU Count & Latency Timeline
VUs  1000 |                                  /------\
      500 |                  /--------------/        \
      200 |  /--------------/                          \
        0 +-----------------------------------------------> Time (12 min)
RPS       |  [~1.2k req/s]   [~3.8k req/s]  [~6.1k req/s]  [Degrading >750 VUs]
```

### Throughput & Response Time Comparison Table

| Load Stage | Concurrent VUs | Target Endpoint | Throughput (RPS) | p50 Latency | p90 Latency | p99 Latency | Error Rate |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Baseline** | 10 VUs | `/api/v1/agent/status` | 820 req/sec | 4.2 ms | 8.1 ms | 14.5 ms | 0.00% |
| **Moderate** | 200 VUs | `/api/v1/agent/status` | 3,150 req/sec | 12.8 ms | 28.4 ms | 48.2 ms | 0.00% |
| **High** | 500 VUs | `/api/v1/agent/status` | 5,420 req/sec | 34.1 ms | 72.6 ms | 118.0 ms | 0.02% |
| **Peak Load** | 750 VUs | `/api/v1/agent/status` | 6,180 req/sec | 68.4 ms | 142.0 ms | 245.0 ms | 0.15% |
| **Saturation Limit** | 1,000 VUs | Combined Workload | 5,890 req/sec | 154.0 ms | 480.0 ms | 1,240.0 ms | 2.41% |

