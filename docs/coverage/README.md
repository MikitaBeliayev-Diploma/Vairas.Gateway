# Vairas.Gateway — code coverage

**Line coverage 99.14 %** (459/463 executable lines), branch coverage 94.97 % — threshold 70 % **met**. Generated 2026-11-25.

| Assembly | Line % | Lines | Branch % |
|---|---:|---:|---:|
| Vairas.Gateway | 99.14 | 459/463 | 94.97 |

## How it was measured

- Tool: coverlet (`XPlat Code Coverage`) driven by `Backend/local/coverage.py`; settings `Vairas.Gateway/coverage.runsettings` (migrations and test assemblies excluded).
- Test projects: `Vairas.Gateway.ServiceTests`, `Vairas.Gateway.Tests`; line hits of all test projects of the service are merged (maximum per line), so shared code is never counted twice.
- Filter: `TestCategory!=Broker`. Broker tests need the Service Bus emulator and are executed (and passed) separately by `local/test_all.py`.
- The numbers are measured line coverage, not a statement about functional completeness; acceptance status is tracked in `Documentation/Final-Verification/AC-Traceability-Matrix.xlsx`.

## Reproduce

```powershell
cd Backend
py -3 local/coverage.py --services Vairas.Gateway --filter "TestCategory!=Broker" --threshold 70
```

## Files

`coverage-summary.json` (machine-readable, per file) and `index.html` (per-file table, lowest coverage first) are in this folder.

## Least covered files

| File | Line % | Lines |
|---|---:|---:|
| `Program.cs` | 0.0 | 0/1 |
| `Configuration/ConfigureRequestTimeouts.cs` | 91.67 | 11/12 |
| `Configuration/ConfigureAuthentication.cs` | 97.62 | 41/42 |
| `Security/LessonActions.cs` | 98.36 | 60/61 |
| `Security/ReadPreviewDelegation.cs` | 100.0 | 80/80 |
| `GatewayApplication.cs` | 100.0 | 43/43 |
| `GatewayProblems.cs` | 100.0 | 10/10 |
| `IngressLimiter.cs` | 100.0 | 22/22 |
