# Win objective optimization benchmark

The comparison reruns the optimizer against the captured pre-search-change results using the exact saved team, request, and opponent ratings. The checked-in baseline is immutable benchmark input; the harness never writes to it. It reports old and new win percentages, percentage-point deltas, runtimes, lineup legality, eligibility, assistant behavior limits, exact reevaluation, objective ranking, and roster-order determinism.

## Reproduce

Run these commands from the repository root with .NET 8:

```powershell
dotnet test Backend.Tests/Backend.Tests.csproj --configuration Release
dotnet run --project Backend.Benchmarks/HattrickAnalizer.Benchmarks.csproj --configuration Release
dotnet run --project Backend.Benchmarks/HattrickAnalizer.Benchmarks.csproj --configuration Release -- --legacy7
```

The default runs all 12 win-search fixtures. `--legacy7` reconstructs the original seven seed-versus-optimized fixtures documented in [optimizer-benchmark.md](optimizer-benchmark.md); it does not select seven from the new Win fixture set. Pass `--baseline path.json` to compare another compatible snapshot. Add `--output path.json` to save comparative output; the harness rejects an output path equal to the baseline path.

## Frozen baseline

`docs/win-optimization-baseline.json` (SHA-256 `266523C4DB9242CA6DFDE6CBD61A644FE687A2EB274BC5A5BC2D46C244F18177`) was captured at `2026-10-08T14:51:05Z`, before the win-search implementation changed. It stores complete team, request, and opponent inputs alongside the old optimizer response, old win probability, runtime, and constraint checks. The 12 fixed seeds cover 18–25 player rosters, balanced/strong/weak opponents, asymmetric left/right defense and attack, heterogeneous specialists, automatic and forced formations, assistant levels 0 and 5, and home and away matches.

The archived pre-change optimizer source is `Backend.Benchmarks/legacy-baseline/AdvancedLineupOptimizer.cs.txt` (SHA-256 `42D71BE45D3F077F476FB6795473D435B072DCF581F65E2E874B82385547C641`). This preserves the implementation used for the baseline if a future rerun is needed.

## Observed Release comparison

Run on 2026-10-08: 7 of 12 fixtures improved, 5 tied, and none regressed. Mean change was `+0.283` percentage points and the largest gain was `+1.310` points; all 12 selected XIs were legal and eligible, met the assistant cap, independently reevaluated exactly, ranked correctly for Win, and stayed stable under reversed and shuffled rosters. Average optimizer time was 1,780 ms before and 2,419 ms after; medians were 415 ms and 533 ms. Automatic formation search accounts for most of the runtime.

| Fixture | Old Win % | New Win % | Delta (pp) | Old ms | New ms |
|---|---:|---:|---:|---:|---:|
| balanced-auto-a0-home | 85.542710 | 85.784295 | +0.241585 | 3726.3 | 4831.5 |
| balanced-forced-a5-away | 53.410066 | 54.720294 | +1.310228 | 415.3 | 512.8 |
| strong-auto-a5-home | 20.802221 | 20.802221 | +0.000000 | 3615.5 | 4478.4 |
| strong-forced-a0-away | 5.671148 | 5.951971 | +0.280823 | 314.3 | 398.8 |
| weak-auto-a0-away | 93.241162 | 93.241162 | +0.000000 | 2607.9 | 3516.7 |
| weak-forced-a5-home | 97.901848 | 97.995612 | +0.093764 | 332.1 | 474.8 |
| asym-left-defense-auto | 79.405035 | 79.405035 | +0.000000 | 2747.6 | 3931.8 |
| asym-right-defense-forced | 39.127627 | 39.127627 | +0.000000 | 402.1 | 530.6 |
| asym-left-attack-auto | 40.115616 | 40.115616 | +0.000000 | 4049.5 | 5825.5 |
| asym-right-attack-forced | 77.929385 | 78.621555 | +0.692170 | 214.6 | 298.8 |
| specialists-auto-a0 | 63.846101 | 64.436304 | +0.590203 | 2558.2 | 3695.1 |
| specialists-forced-a5 | 18.694950 | 18.878036 | +0.183086 | 377.6 | 533.4 |

The reconstructed original seven seed-versus-optimized scenarios also passed their objective and lineup checks in Release. Their reproduction command and historical recorded run are in [optimizer-benchmark.md](optimizer-benchmark.md); the current run prints the newly evaluated seed and optimized values.

## Quality checks

`Backend.Tests/WinOptimizationTests.cs` replays fixed captured inputs and checks the selected result against the requested objective across multiple Auto candidates. It independently reevaluates the returned alternative across probabilities, expected goals, and all seven sectors, verifies a legal 11 with eligible players and assistant limits, checks home advantage appears once in opponent comparison ratings, and requires a fixed fixture to show a Win-versus-points roster tradeoff. Its assistant-level-0 adversarial case evaluates every paired behavior choice on the same assigned players that stays within the five-order cap. The benchmark also reruns fixtures after reversing and deterministically shuffling the roster to catch order-dependent results.

The legacy snapshot's `ExactReevaluation` and `DeterministicPermutation` fields are preserved as historical capture metadata. The comparative harness independently compares the current selected alternative against a fresh exact-XI evaluation, and it tests actual reversed and shuffled roster orders.

All 12 saved pre-change XIs are legal, eligible, and within their captured assistant behavior caps. The comparative output retains those historical flags for reference and reports current flags beside them.
