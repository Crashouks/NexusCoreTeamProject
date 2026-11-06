# Automated testing and coverage

## Test results

The .NET test project contains **69 focused business-logic unit-test cases** covering registration/login validation, store pricing, database value conversion, and JWT lifetime/claims. The full test project has **158 passing tests**.

Run the suite and collect Cobertura coverage:

```powershell
dotnet test .\NexusCore.Api.Tests\NexusCore.Api.Tests.csproj --collect:"XPlat Code Coverage"
```

The report is written under `NexusCore.Api.Tests\TestResults\<run-id>\coverage.cobertura.xml`.

## Coverage snapshot

Measured for `NexusCore.Api` by Coverlet:

| Metric | Result |
|---|---:|
| Lines | 605 / 4,880 (**12.39%**) |
| Branches | **14.15%** |
| Methods | 68 / 526 (**12.93%**) |

The focused business-logic classes have **100% line and method coverage**: `RegistrationValidator`, `PricingService`, `DbValue`, and `JwtTokenService`. Branch coverage is 100% for `RegistrationValidator` and `DbValue`, 97.36% for `PricingService`, and 96.42% for `JwtTokenService`.

The overall API percentage is low because most controllers and database-backed services are not exercised by this suite. Priority areas for further testing are controller workflows and services that depend on MySQL or external systems. Coverage alone does not establish that the covered behavior is correct.

## Browser end-to-end tests

Three Playwright user flows are in `client\e2e\account-flows.spec.js`: registration and library access, login and sign-out, and regular-user access to the admin route.

Run them from `nexuscore\client`:

```powershell
npx playwright install chromium
npm run test:e2e
```

The tests use the local Vite server and expect the API at `http://127.0.0.1:5000` (or `VITE_PROXY_TARGET`). Start the API with a **dedicated disposable test database** initialized with the project schema before running them. Each flow registers a unique account; discard/reset that test database afterward. `E2E_BASE_URL` can point Playwright at an already-running frontend.

The browser tests were discovered successfully but not executed for this coverage snapshot: neither the frontend nor API was running locally. Their results are not included in the coverage figures above.
