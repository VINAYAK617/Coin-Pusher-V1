# ALW Neo Game Engine Integration

The integrated .NET 5 solution is in `Neo-Game-Engine-Solution`.

## Profile

- Assembly: `ALW-Coin-Pusher.dll`
- Winning tiers: `Tier1` through `Tier76`, mapped one-to-one to ALW PPS rows
- No-win tier: `Tier77`
- Offline catalog: 400 independently verified tickets embedded under `Resources/Tickets/Tiers`

## Build

```powershell
dotnet build .\Neo-Game-Engine-Solution\iRGS-SGS-Coin-Pusher-Solution.sln -c Release
```

## Validation

```powershell
$env:DOTNET_ROLL_FORWARD = 'Major'
dotnet run --project .\Neo-Game-Engine-Solution\FrameworkValidation\FrameworkValidation.csproj -c Release
```

The validation executable checks all 400 embedded tickets, every one of the 76 exact dynamic PPS combinations, and deliberate ticket corruptions.
