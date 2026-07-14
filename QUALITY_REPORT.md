# Quality Report

Generated locally on 2026-07-14 for branch `codex/qa-clean-architecture`.

## Local Results

| Check | Result |
| --- | --- |
| Build | Passed: `dotnet build .\CoinPusherEngine.sln -c Release -v minimal` |
| Test discovery | No test project found in the solution |
| Code coverage | Not generated locally because there are no test projects |
| Snyk CLI | Not installed locally: `snyk` command was not found |
| Snyk counts | Not available locally without Snyk CLI/authentication |
| .NET package vulnerability check | Passed: no vulnerable packages reported by NuGet sources |
| SonarScanner for .NET | Not installed locally: `dotnet sonarscanner` command was not found |
| SonarQube upload | Not run locally because scanner/server credentials are not configured |

## Added Quality Setup

- `sonar-project.properties` contains the base SonarQube project configuration.
- `.github/workflows/quality-analysis.yml` runs restore, build, test coverage discovery, optional SonarQube analysis, and optional Snyk dependency scanning.

## Required Secrets For CI

Configure these repository secrets before expecting SonarQube/Snyk results in GitHub Actions:

| Secret | Purpose |
| --- | --- |
| `SONAR_PROJECT_KEY` | SonarQube project key |
| `SONAR_HOST_URL` | SonarQube server URL |
| `SONAR_TOKEN` | SonarQube authentication token |
| `SNYK_TOKEN` | Snyk authentication token |

## Current Coverage Position

The engine currently has only one project: `CoinPusherEngine.csproj`. Because there is no test project, measured line/branch coverage is not available yet. To produce real coverage, add a test project such as `CoinPusherEngine.Tests` and include focused tests around `Planner`, `CleanGenerationPipeline`, `TicketSerializer`, `TicketChecker`, and `Verifier`.

## Current Dependency Position

The project has one direct NuGet dependency:

- `Newtonsoft.Json` `13.0.3`

The local NuGet vulnerability check reported no vulnerable packages from the configured sources.
