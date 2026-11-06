# Benchmark and Stress Testing Results

## 1. Apache Benchmark (ab) Results
**Command used:** `ab -n 10000 -c 100 https://localhost:<port>/api/<endpoint>`

| Endpoint | Concurrency | Total Requests | Requests/sec (Throughput) | Avg Response Time (ms) | Failed Requests |
|----------|-------------|----------------|---------------------------|------------------------|-----------------|
| `GET /api/v1/...` | 100 | 10,000 | [Insert] | [Insert] | 0 |
| `POST /api/v1/...`| 100 | 10,000 | [Insert] | [Insert] | 0 |

*(Insert screenshot of ab terminal output here)*

## 2. Stress Testing (k6) Results
**Scenario:** Gradual ramp-up to 200 Virtual Users over 3.5 minutes.

| Metric | Result | Target/Threshold |
|--------|--------|------------------|
| **HTTP Requests** | [Insert total] | N/A |
| **P95 Response Time** | [Insert ms] | < 500ms |
| **Max Response Time** | [Insert ms] | < 2000ms |
| **Error Rate** | [Insert %] | < 1% |

*(Insert screenshot of k6 terminal output/graph here)*

## 3. Conclusions on Stable Operation Limits
*   **Max stable throughput:** The API handles up to `[X]` requests per second before response times degrade past 500ms.
*   **Failure threshold:** At `[Y]` concurrent users, the database connection pool exhausted / CPU reached 100%, resulting in 5xx HTTP errors.
*   **Verdict:** The system is stable under expected typical load, but scaling will be required if concurrent users exceed `[Z]`.
