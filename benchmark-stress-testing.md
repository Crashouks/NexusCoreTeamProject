# Benchmark and stress testing

## Status

No load measurements or screenshots are recorded yet. Apache Benchmark (`ab`), k6, and Docker
are not installed in the current environment, so reporting throughput, latency, or saturation
values would be misleading. The commands and scenario below target this repository's .NET 10 API
and MySQL database; populate the results table after running them on a started test instance.

## Test setup

- API: `nexuscore/NexusCore.Api`, .NET 10
- Database: MySQL 8.4 in the Docker Compose deployment
- Safe read-only endpoints: `GET /api/health`, `GET /api/games?limit=20`, and cached
  `GET /api/v1/games?page=1&limit=20`
- Stress scenario: [`tests/performance/k6-stress-test.js`](./tests/performance/k6-stress-test.js)
- Defaults: p95 under 500 ms and request/check failure rate under 1%

The global API limiter defaults to 300 requests per minute per client. In an isolated test
environment only, raise it so the benchmark measures the service rather than the limiter:

```powershell
$env:RateLimiting__GlobalPermitLimit = "6000"
$env:RateLimiting__GlobalWindowSeconds = "60"
```

Start the API and database using the project's normal setup, then run Apache Benchmark at several
concurrency levels:

```powershell
ab -n 1000 -c 5  http://localhost:5000/api/health
ab -n 1000 -c 20 http://localhost:5000/api/health
ab -n 1000 -c 50 http://localhost:5000/api/health

ab -n 1000 -c 5  "http://localhost:5000/api/games?limit=20"
ab -n 1000 -c 20 "http://localhost:5000/api/games?limit=20"
ab -n 1000 -c 50 "http://localhost:5000/api/games?limit=20"
```

Run the staged stress test separately:

```powershell
$env:BASE_URL = "http://localhost:5000"
k6 run .\tests\performance\k6-stress-test.js
```

The scenario sends only GET requests; it does not create test records or require an API key.
Record the k6 summary and save screenshots under `docs/performance/screenshots/`, then link them
here with relative paths.

## Results

| Tool / endpoint | Concurrency / VUs | Throughput | p95 response | Failed requests | Status |
|---|---:|---:|---:|---:|---|
| `ab` `/api/health` | 5 | Not measured | Not measured | Not measured | Pending run |
| `ab` `/api/health` | 20 | Not measured | Not measured | Not measured | Pending run |
| `ab` `/api/health` | 50 | Not measured | Not measured | Not measured | Pending run |
| `ab` `/api/games` | 5 / 20 / 50 | Not measured | Not measured | Not measured | Pending run |
| k6 health + catalog + versioned catalog | 10 / 25 / 50 / 100 | Not measured | Not measured | Not measured | Pending run |

`ab` does not report p95 in its default summary; use its percentile output (for example
`-e percentiles.csv`) or report the available percentile rows. Record CPU/RAM separately if
available. The stable throughput limit and failure threshold cannot be concluded until a run
produces measurements.
