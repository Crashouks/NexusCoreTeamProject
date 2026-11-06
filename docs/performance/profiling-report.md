# Backend Application Profiling Report

## Executive Summary
This profiling report identifies performance bottlenecks, computational hotspots, and memory allocation patterns in the `NexusCore.Api` service during sustained high-load streaming telemetry scenarios. Profiling was conducted using **Visual Studio Profiler** (CPU Usage & Memory Allocation tracking) and **dotTrace**.

---

## 1. Methodology & Test Setup

* **Profiling Tools:** Visual Studio Enterprise 2022 Performance Profiler / JetBrains dotTrace 2024.1
* **Profiling Mode:** Instrumentation & Sampling Execution Call Trees
* **Duration:** 10 minutes continuous load execution (500 concurrent VUs @ 5,000 req/sec)
* **Target Processes:** `NexusCore.Api.dll` (.NET 8 runtime)

---

## 2. Identified CPU Hotspots

Profiling revealed three dominant CPU execution hot paths accounting for over **68% of total CPU time** under peak telemetry loads:

```text
Total CPU Consumption Breakdown:
+-- 34.2% System.Text.Json.Serializer (Telemetry Payload Deserialization)
+-- 21.5% NexusCore.Api.Security.AgentAuthAttribute (Header Parsing & Config Lookups)
+-- 12.8% NexusCore.Api.Hubs.StreamingAgentHub (SignalR Broadcast Serialization)
+-- 31.5% Other / ASP.NET Core Routing Pipeline
```

### Detailed Call Tree & Hotspots Table

| Rank | Method / Component | CPU Time (%) | Self Time (%) | Primary Bottleneck Cause |
| :--- | :--- | :--- | :--- | :--- |
| **1** | `System.Text.Json.JsonSerializer.Deserialize` | 34.2% | 29.8% | Dynamic reflection & un-cached `JsonSerializerOptions` in `AgentStatusReportDto` parsing. |
| **2** | `AgentAuthAttribute.OnActionExecutionAsync` | 21.5% | 18.1% | Reading `IConfiguration["AgentSettings:ApiKey"]` per-request without memory caching. |
| **3** | `StreamingAgentHub.SendMetrics` | 12.8% | 9.4% | Frequent object allocations during SignalR fan-out broadasting to connected dashboard clients. |
| **4** | `Microsoft.AspNetCore.Routing.Matching` | 8.6% | 5.2% | Standard route evaluation and middleware pipeline traversal. |

---

## 3. Memory Allocation & GC Analysis

### Memory Profile Summary
* **Total Allocated Memory:** 4.82 GB over 10 minutes
* **GC Collection Counts:**
  * **Gen 0:** 1,420 collections
  * **Gen 1:** 184 collections
  * **Gen 2:** 12 collections (Non-blocking background GC)
* **Allocation Hotspot:** High frequency of short-lived `System.Byte[]` and `AgentStatusReportDto` string allocations per incoming status ping.

---

## 4. Performance Bottlenecks & Optimization Recommendations

### Bottleneck 1: Reflection Overhead in JSON Deserialization
* **Issue:** `System.Text.Json` defaulted to dynamic reflection for complex nested DTOs (`SystemMetricsDto`).
* **Recommendation:** Implement C# Source Generators (`System.Text.Json.Serialization.JsonSerializerContext`) to eliminate reflection overhead.
* **Expected Impact:** Estimated 25-30% reduction in CPU consumption for `/api/v1/agent/status`.

### Bottleneck 2: Per-Request Configuration Lookups in `AgentAuthAttribute`
