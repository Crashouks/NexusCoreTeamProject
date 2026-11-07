# API security & reliability – audit report and changes

Scope: `nexuscore/NexusCore.Api` (ASP.NET Core, .NET 10). The legacy Node server in `nexuscore/server` was not touched.

> **Verification:** .NET 10 SDK; `dotnet test nexuscore/NexusCore.Api.Tests/NexusCore.Api.Tests.csproj`
> passed all 90 tests. `dotnet list package --vulnerable --include-transitive` reports no vulnerable
> packages. Docker, MySQL, SonarQube, and load/profiling tools were unavailable, so those runtime
> analyses still require a configured test environment.

## 1. Audit findings

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | **Chat IDOR** – `CanAccessChatAsync` / `Join` allowed any user queued for *any* game to read, join and post in *every* chat | High | Fixed (per-chat check) |
| 2 | JWT secret hard-coded in `appsettings.json` and two different fallbacks (`Program.cs` vs `JwtTokenService`) | High | Fixed (`JwtSettings`, fail-fast outside Development, secret removed from appsettings) |
| 3 | JWT had no issuer/audience validation, 5-min clock skew, no algorithm pinning | Medium | Fixed |
| 4 | **No rate limiting** – login brute-force, anonymous agent endpoints, money/upload endpoints unlimited | High | Fixed |
| 5 | `X-Forwarded-For` trusted from anyone (`KnownProxies.Clear()`), so per-IP limits are spoofable | High (for #4) | Fixed (loopback + `TRUSTED_PROXIES` only, `ForwardLimit=1`) |
| 6 | `GET /api/media/validate-url` fetches any user-supplied URL → **SSRF** (cloud metadata, LAN, localhost); no retry/breaker | High | Fixed (connect-time IP guard + Polly pipeline) |
| 7 | 104 × `500` responses returned `ex.Message` (SQL / connection details) | Medium | Fixed (`ApiResults.ServerError`, logged server-side) |
| 8 | Admin-only actions relied only on inline `if (!IsAdmin)`; Games GETs were anonymous only *implicitly* | Low | Fixed (`[Authorize(Roles="admin")]`, explicit `[AllowAnonymous]`, audit test) |
| 9 | 401/403 had empty bodies – clients couldn't tell "expired" from "missing" | Low | Fixed (`UNAUTHENTICATED` / `TOKEN_EXPIRED` / `TOKEN_INVALID` / `FORBIDDEN`) |
| 10 | `POST /api/users/{id}/topup` lets any user credit unlimited balance to themselves | High (business) | **Not changed** – needs a product decision / real payment flow; now limited to 10 req/min |
| 11 | Role is baked into a JWT valid 7 days, so a demoted admin stays admin until expiry | Medium | **Not changed** – shorten `JWT_EXPIRES_IN` or re-check role in `OnTokenValidated` |
| 12 | MySqlConnector 2.6.0 advisory affected `VerifyCA`/`VerifyFull` without a configured CA | Moderate | Fixed by upgrading to 2.6.1 |

Verified OK (resource ownership): `/api/users/{id}/*` (`CanAccess`), trials, notifications, downloads, cart, wishlist,
cloud session privacy, media upload (`CanManageGameAsync`), `ChatHub`/`CloudStreamHub` membership checks.

## 2. What changed

**Authentication / authorization** – `Security/JwtSettings.cs`, `Security/AuthenticationExtensions.cs`
* One `JwtSettings` object feeds both the issuer and the validator. Validates issuer, audience, lifetime, signature, `HS256` only, 30 s skew.
* Outside `Development`, a missing/short (`< 32`) `JWT_SECRET` stops the app with a clear message.
* 401 and 403 return JSON `{ error, code }`.

**Rate limiting** – `Security/RateLimitingExtensions.cs` (built-in `Microsoft.AspNetCore.RateLimiting`, fixed window, no queue → `429` + `Retry-After`)

| Policy | Limit | Key | Applied to |
|--------|-------|-----|-----------|
| `sensitive` | **10 / min** | user id (else IP) | purchase, topup, checkout, subscribe, uploads, avatar, `validate-url`, forum posts, developer request |
| `auth` | 10 / min | IP only | login, register |
| `agent` | 180 / min | IP | `/api/cloud/agent/*` (agent polls ~40/min) |
| global | 300 / min | user id / IP | everything under `/api` and `/hubs` (exempt: `/api/health`, `OPTIONS`, static `/uploads`) |

All numbers are configurable in `appsettings.json → RateLimiting`.

**Fault tolerance (Polly v8 via `Microsoft.Extensions.Http.Resilience`)** – `Resilience/*`

`validate-url` → `IImageUrlProbe` → `HttpClient` with, outermost to innermost:
`total Timeout (10 s)` → `Retry (2×, exponential back-off + jitter, 5xx/408/429/network only)` →
`Circuit Breaker (≥50 % failures over ≥5 calls in 30 s ⇒ open 30 s, then half-open)` → `per-attempt Timeout (3 s)`.
Mapped to HTTP: timeout → `504 UPSTREAM_TIMEOUT`, open circuit → `503 UPSTREAM_UNAVAILABLE` + `Retry-After`.
4xx answers and SSRF rejections neither retry nor count towards the breaker.
Also: startup DB migration is wrapped in a Polly retry (5×, exponential) so the API survives MySQL still booting.

Retry is only applied to idempotent calls (HEAD, migrations). **Do not** wrap purchase/checkout in Retry without an idempotency key.

**Dependency hygiene** – upgraded MySqlConnector to 2.6.1 for
[GHSA-473q-m89c-ghf8](https://github.com/advisories/GHSA-473q-m89c-ghf8); the package audit is clean.

## 3. How to verify

```bash
cd nexuscore
dotnet restore && dotnet build
dotnet test NexusCore.Api.Tests        # 90 tests, no database required
```

Test map → acceptance criteria

| Criterion | Tests |
|-----------|-------|
| rejects no token / expired / no permission | `AuthenticationTests` (no token, expired, wrong key, wrong iss/aud, `alg=none`, malformed, user→admin = 403), `AuthorizationAuditTests` (every action explicitly protected or on the anonymous allow-list) |
| 429 after limit | `RateLimitingTests` (10 OK → 11th 429 + `Retry-After` + JSON, per-client buckets, IP keying, global exemptions) |
| Timeout / Retry / Circuit Breaker | `ResilienceTests` (hang → timeout, total budget, recovery after 2 failures, give-up after N, no retry on 404, breaker opens → host not called, half-open recovery, half-open failure re-opens, SSRF cases) |
| API contracts and caching | `ApiVersioningTests`, `GameCatalogCacheTests` |

Manual check against a running API (`npm run server`):
```bash
curl -i localhost:5000/api/users/me                                   # 401 UNAUTHENTICATED
curl -i -H "Authorization: Bearer <expired>" localhost:5000/api/users/me   # 401 TOKEN_EXPIRED
curl -i -H "Authorization: Bearer <user token>" localhost:5000/api/admin/stats  # 403 FORBIDDEN
for i in $(seq 1 12); do curl -s -o /dev/null -w "%{http_code} " -X POST localhost:5000/api/auth/login \
  -H 'Content-Type: application/json' -d '{"email":"a@b.c","password":"x"}'; done   # 401 ×10, then 429 429
curl -i -b "nc_auth=<token>" "localhost:5000/api/media/validate-url?url=http://169.254.169.254/"  # 400 INVALID_URL
```

## 4. Behaviour changes to be aware of

1. **`JWT_SECRET` is now mandatory** outside `ASPNETCORE_ENVIRONMENT=Development`. `npm run server` uses `--no-launch-profile`, i.e. *Production* – make sure `nexuscore/.env` exists (copy `.env.example`) with a real value. The placeholder from `.env.example` still starts (with a warning) but must be replaced for deployment.
2. Existing sessions are invalidated once (old tokens lack issuer/audience) – users simply log in again.
3. Behind a **non-loopback** reverse proxy (Docker network, nginx on another host) set `TRUSTED_PROXIES=<proxy ip>`, otherwise all clients share the proxy's IP bucket.
4. Chat: a user can only join/read the chat of a game they are queued for (plus the global lobby).

## 5. Known limitations / follow-ups

* The circuit breaker is **one shared breaker** for all external image hosts. A burst of dead URLs can open it for everybody for 30 s (the rate limit bounds this). If you want one breaker per host, add `.SelectPipelineByAuthority()` to the resilience handler.
* Fixed windows allow a burst of 2× the limit across a window boundary. Switch to `AddSlidingWindowLimiter` if that matters.
* Rate-limit state is in-memory (per instance). Use a shared store (e.g. Redis) when scaling out.
* DB calls are not retried (Dapper calls are spread across controllers). A Polly pipeline around a `DbService.ExecuteReadAsync` helper would be the next step for read-only queries.
* Items 10 and 11 in the findings table.
