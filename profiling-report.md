# Backend application profiling report

## Status and environment

Profiling was not run in this workspace: `dotnet-trace`, `dotnet-counters`, and k6 are not
installed, and the API/database load scenario was not started. Therefore this report contains no
claimed CPU percentages, allocation counts, or before/after timings. Capture those from a real
profile before using this as measured evidence.

The available SDKs are .NET 10.0.103 and 10.0.201. The API targets `net10.0`.

## Scenario to profile

Use the read-only `GET /api/v1/games?page=1&limit=20` request while generating load with
[`tests/performance/k6-stress-test.js`](./tests/performance/k6-stress-test.js). Capture a cold
request after process restart and repeated warm requests within the 30-second cache lifetime;
inspect MySQL query counts as well as request latency. The existing unversioned `/api/games`
endpoint has a different response/query shape, so its timings are not an isolated cache comparison.

## Source-based investigation (not profiler findings)

`GamesController.List` executes a count query and a separate catalog/review aggregation query for
each request. Under load, inspect time spent awaiting MySQL versus CPU spent materializing and
serializing the joined result; source inspection alone cannot establish which is the bottleneck.
The versioned catalog endpoints cache the first ten pages for common page sizes and invalidate
those entries after .NET API writes that affect approved game summaries.

## Reproducible capture

Install the diagnostic tools in the test environment, start the API and MySQL, then collect a
short trace while k6 is running:

```powershell
dotnet tool install --global dotnet-trace
dotnet tool install --global dotnet-counters
dotnet-counters monitor --process-id <API_PID> System.Runtime
dotnet-trace collect --process-id <API_PID> --duration 00:01:00 --output nexuscore-api.nettrace
```

Open the trace in Visual Studio or PerfView and record the workload, top inclusive/self CPU
methods, allocation/GC observations, and any visible database wait time. Save screenshots or
exported views under `docs/performance/screenshots/` and link them here.

| Measurement | Before | After | Evidence |
|---|---|---|---|
| CPU hotspots / inclusive time | Not measured | Not measured | Profile capture pending |
| Allocated bytes / GC | Not measured | Not measured | Profile capture pending |
| Catalog request latency | Not measured | Not measured | Benchmark run pending |

No profiler-derived optimization conclusion is available until the captures are produced.
