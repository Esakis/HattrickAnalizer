# Hattrick Analyzer

Hattrick Analyzer is a .NET 8 API and Angular 16 application for reviewing team data, comparing match sectors, and exploring lineup scenarios. CHPP access is optional for local development: the backend has an explicit deterministic mock mode.

## Requirements

- .NET 8 SDK
- Node.js 18 or newer and npm
- A CHPP application key only when using live Hattrick data

## Run locally

1. Copy `Backend/appsettings.example.json` to `Backend/appsettings.json`.
2. Add the CHPP consumer key and secret to the local file. Do not commit credentials.
3. Start the API from the repository root:

   ```powershell
   dotnet run --project Backend/HattrickAnalizer.csproj
   ```

4. Install and start the Angular app:

   ```powershell
   cd Frontend
   npm ci
   npm start
   ```

The API listens on the URL printed by ASP.NET Core; the Angular dev server defaults to `http://localhost:4200`. In Development, Swagger is available at `/swagger` on the API origin.

To run without CHPP credentials, set `UseMockData=true` in a local configuration override, or launch with `dotnet run --project Backend/HattrickAnalizer.csproj -- --UseMockData=true`. Mock mode is deterministic and makes no live CHPP calls.

## Build and test

Run these commands from the repository root:

```powershell
dotnet build Backend/HattrickAnalizer.csproj --configuration Release
dotnet test Backend.Tests/Backend.Tests.csproj --configuration Release
cd Frontend
npm ci
npm run build
npm test -- --watch=false --browsers=ChromeHeadless
```

The backend tests use mock data and local fixtures. They do not contact CHPP or submit match orders.

For the fixed-seed Win optimizer comparison and its frozen pre-change results, see [the benchmark guide](docs/win-optimization-benchmark.md). Run the 12-fixture comparison with `dotnet run --project Backend.Benchmarks/HattrickAnalizer.Benchmarks.csproj --configuration Release`; use `-- --legacy7` for the original seven seed-versus-optimized scenarios.

## Forecast limits and calibration

Match probabilities and sector ratings are model estimates, not guaranteed outcomes. The API returns a model version, confidence label, provenance, and warnings with optimizer and league forecasts. The current confidence label is `unvalidated-low`; regression tests establish consistency and guard known rules, but synthetic tests do not establish real-match accuracy. Several tactical inputs and opponent skills are unobserved or approximated, so use the warnings and sample counts when comparing scenarios.

For a real-world check:

1. Capture a complete own-team snapshot before kickoff with all 11 player IDs, their available private skills, positions and behaviors, tactic, attitude, coach type, team spirit, confidence, formation experience, assistant level, home/away status, and weather. The snapshot endpoint is `POST /api/calibration/snapshots/capture`.
2. Keep the snapshot time and match context. A snapshot recorded after kickoff, missing required context, or containing players absent from the captured roster is not eligible for replay.
Calibration replay rounds recorded team spirit and confidence to the integer inputs accepted by the optimizer; it does not substitute their later live values.
3. After the match, request `GET /api/calibration/own-matches?count=5`. Review included and excluded matches, sample counts, training metrics, and held-out metrics. Historical roster or skill changes must not be substituted for the snapshot captured before kickoff.
4. Calibration sorts observations by match date and reserves the latest 20% (at least one match when there is more than one) as a chronological holdout. Use the earlier training observations to propose changes; do not tune against held-out results. Re-evaluate on later matches before changing the confidence label or claiming improved predictive accuracy.

Store CHPP credentials in local user secrets or environment configuration, not in source control. `Backend/appsettings.example.json` contains placeholders only.

## Main directories

- `Backend/` — ASP.NET Core API and domain services
- `Backend.Tests/` — regression and mocked API integration tests
- `Frontend/` — Angular UI
- `.github/workflows/` — pull request checks and backend deployment workflow
