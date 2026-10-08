# Synthetic optimizer benchmark

## Reproduce

Run from the repository root with .NET 8:

```powershell
dotnet run --project Backend.Benchmarks/HattrickAnalizer.Benchmarks.csproj --configuration Release
```

The console harness uses seven fixed roster/opponent seeds and only generated player names, skills, and sector ratings. It compares the production greedy legal seed with the production deterministic optimizer through `OptimizeRoster`; both are reevaluated by the shared prediction model using the same request context and opponent ratings. Six fixtures pin `4-4-2`, start at Normal tactic, and allow the same Auto tactic search, so the baseline is the first legal seed the search itself sees; one additional fixture searches all formations and tactics. Opponent sectors are derived from the seed's initial own-rating vector and scaled independently with fixed factors in 0.85–1.15 for two balanced fixtures and 1.10–1.40 for challenging fixtures. The fixtures cover exactly eleven players and benches, home and away matches, all three objectives, fractional opponent ratings, and assistant levels 0–5.

The harness fails if the selected objective regresses, the returned XI does not contain the exact legal formation slots, any selected player is ineligible, a behavior is illegal, or the assistant custom-order cap is exceeded. It prints runtime per optimized fixture; objective values and roster assignment are deterministic, while runtime naturally varies by machine and JIT state.

## Recorded run

Recorded on 2026-10-08 with .NET 8 Release. `delta` is optimized objective minus initial-seed objective.

| Seed | Players (bench) | Venue | Search | Opponent | Objective | Assistant | Baseline | Optimized | Delta | Search ms | XI / custom orders |
|---:|---:|:---:|:---:|:---:|:---|---:|---:|---:|---:|---:|:---:|
| 1101 | 11 (0) | home | 4-4-2 | balanced | ExpectedPoints | 0 | 1.249896 | 1.751435 | +0.501539 | 555.1 | 11 / 5 |
| 2202 | 14 (3) | away | 4-4-2 | challenging | Win | 2 | 0.009702 | 0.047331 | +0.037629 | 533.9 | 11 / 6 |
| 3303 | 11 (0) | home | 4-4-2 | balanced | Draw | 5 | 0.215215 | 0.395422 | +0.180208 | 260.5 | 11 / 8 |
| 4404 | 14 (3) | away | 4-4-2 | challenging | ExpectedPoints | 1 | 0.158900 | 0.403470 | +0.244570 | 195.0 | 11 / 6 |
| 5505 | 12 (1) | home | 4-4-2 | challenging | Win | 3 | 0.031182 | 0.173517 | +0.142335 | 232.5 | 11 / 8 |
| 6606 | 13 (2) | away | 4-4-2 | challenging | Draw | 4 | 0.052991 | 0.139726 | +0.086735 | 263.2 | 11 / 8 |
| 7707 | 12 (1) | home | Auto | challenging | ExpectedPoints | 2 | 0.381681 | 0.829616 | +0.447935 | 1945.5 | 11 / 6 |

All seven selected objectives were better than their initial legal seeds, and all XI/behavior/assistant checks passed. The source fixes the roster seeds at 1101, 2202, 3303, 4404, 5505, 6606, and 7707; repeated runs reproduce the assignment and probability columns, but can report different elapsed milliseconds.

## Limits

This is a code-path and search benchmark against the current model, not evidence of Hattrick match accuracy. Six synthetic fixtures pin 4-4-2 while searching Auto tactics, and one searches Auto formations and tactics; ExpectedPoints/Win/Draw values can be close to their mathematical extremes for these constructed ratings. Play Creatively remains excluded from Auto ranking and incomplete when selected manually, while Long Shots still lacks opponent goalkeeper/set-piece matching; current model confidence remains `unvalidated-low`.
