# Automated testing and coverage

## Test results

The test project has **170 passing test cases**, including **69 focused business-logic unit-test cases** covering registration/login validation, store pricing, database value conversion, and JWT lifetime/claims.

The remaining Module 2 categories are covered as follows:

| Category | Test implementation |
|---|---|
| Exception testing | `ApiExceptionHandlingTests` verifies the database-health endpoint returns a sanitized `503` when its isolated test database is unavailable. Existing validation and resilience tests also cover invalid inputs, timeouts, retries, and upstream failures. |
| Mocking | `StubImageUrlProbe` replaces the external image probe so API tests can assert requests and controlled outcomes without making network calls. `ResilienceTests` similarly scripts the HTTP handler. |
| Integration testing | `RestApiIntegrationTests` sends HTTP requests through ASP.NET Core TestServer, routing, authentication, model binding, JSON serialization, and real controllers. |
| REST API testing | Tests assert endpoint behavior, status codes, response JSON, authorization, error mapping, and `Retry-After` headers. |
| E2E testing | Three Playwright browser flows cover account registration, login/logout, and protected routes. |

Run the suite and collect Cobertura coverage:

```powershell
dotnet test .\NexusCore.Api.Tests\NexusCore.Api.Tests.csproj --collect:"XPlat Code Coverage"
```

The report is written under `NexusCore.Api.Tests\TestResults\<run-id>\coverage.cobertura.xml`.

## Coverage snapshot

Measured for `NexusCore.Api` by Coverlet:

| Metric | Result |
|---|---:|
| Lines | 583 / 4,383 (**13.30%**) |
| Branches | 431 / 3,168 (**13.60%**) |
| Methods | 92 / 576 (**15.97%**) |

In this snapshot, `RegistrationValidator` and `DbValue` have 100% line and branch coverage; `PricingService` has 100% line and 97.36% branch coverage; and `JwtTokenService` has 90.16% line and 69.56% branch coverage.

The API integration tests deliberately do not write to a database. The test host uses a local, unreachable database port to verify the sanitized failure path; successful database workflows still require a separately provisioned disposable test database. The overall API percentage remains low because most controllers and database-backed services are not exercised. Coverage alone does not establish that the covered behavior is correct.

## Browser end-to-end tests

Three Playwright user flows are in `client\e2e\account-flows.spec.js`: registration and library access, login and sign-out, and regular-user access to the admin route.

Run them from `nexuscore\client`:

```powershell
npx playwright install chromium
npm run test:e2e
```

The tests use the local Vite server and expect the API at `http://127.0.0.1:5000` (or `VITE_PROXY_TARGET`). Start the API with a **dedicated disposable test database** initialized with the project schema before running them. Each flow registers a unique account; discard/reset that test database afterward. `E2E_BASE_URL` can point Playwright at an already-running frontend.

The browser tests were discovered successfully but not executed: the frontend/API and a disposable test database were not running locally. Their results are not included in the coverage figures above.
