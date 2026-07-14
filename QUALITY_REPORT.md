# Quality Report

Generated locally on 2026-07-14 for branch `codex/qa-clean-architecture`.

## Local Results

| Check | Result |
| --- | --- |
| Build | Passed: `dotnet build .\CoinPusherEngine.sln -c Release -v minimal` |
| Test project | Added `CoinPusherEngine.Tests` |
| Test result | Passed: 21 passed, 0 failed, 0 skipped |
| Code coverage | Passed threshold: 86.23% line, 72.47% branch, 93.3% method |
| Snyk CLI | Installed via `npm.cmd install -g snyk` |
| Snyk counts | Scan attempted; Snyk returned `401 Unauthorized`, so counts require valid `SNYK_TOKEN`/`snyk auth` |
| .NET package vulnerability check | Passed: no vulnerable packages reported by NuGet sources |
| SonarScanner for .NET | Installed: `dotnet-sonarscanner` 11.2.1 |
| SonarQube upload | Not run locally because scanner/server credentials are not configured |

## Added Quality Setup

- `CoinPusherEngine.Tests` contains deterministic game-logic coverage tests.
- `sonar-project.properties` contains the base SonarQube project configuration and points at `coverage/coverage.opencover.xml`.
- `.github/workflows/quality-analysis.yml` runs restore, build, coverage with an 85% line threshold, optional SonarQube analysis, and optional Snyk dependency scanning.
- `.config/dotnet-tools.json` records `dotnet-sonarscanner` 11.2.1 as a repo-local tool.
- Snyk CLI is installed globally on this machine, but the scan needs authentication.

## Required Secrets For CI

Configure these repository secrets before expecting SonarQube/Snyk results in GitHub Actions:

| Secret | Purpose |
| --- | --- |
| `SONAR_PROJECT_KEY` | SonarQube project key |
| `SONAR_HOST_URL` | SonarQube server URL |
| `SONAR_TOKEN` | SonarQube authentication token |
| `SNYK_TOKEN` | Snyk authentication token |

## Coverage Command

```powershell
dotnet test .\CoinPusherEngine.Tests\CoinPusherEngine.Tests.csproj -c Release --no-build -v minimal /p:CollectCoverage=true /p:CoverletOutputFormat=opencover /p:CoverletOutput="C:\Users\vinay\OneDrive\Documents\CoinPusherUpdated\coverage\coverage" /p:Threshold=85 /p:ThresholdType=line
```

Latest local result:

| Metric | Result |
| --- | --- |
| Line | 86.23% |
| Branch | 72.47% |
| Method | 93.3% |

The generated Sonar-ready coverage file is `coverage/coverage.opencover.xml`.

## Current Dependency Position

The project has one direct NuGet dependency:

- `Newtonsoft.Json` `13.0.3`

The local NuGet vulnerability check reported no vulnerable packages from the configured sources.

Snyk dependency scan command attempted:

```powershell
snyk test --file=CoinPusherEngine.sln --package-manager=nuget
```

Result: `401 Unauthorized`. Configure `SNYK_TOKEN` in CI or run `snyk auth` locally to produce Snyk severity counts.
