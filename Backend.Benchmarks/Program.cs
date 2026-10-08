using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;

// The baseline is immutable input captured before the search change. This program only reads it.
var legacySeven = false;
string? requestedBaselinePath = null;
string? reportPath = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--legacy7":
            legacySeven = true;
            break;
        case "--baseline":
            if (++i >= args.Length) throw new ArgumentException("--baseline requires a JSON file path.");
            requestedBaselinePath = args[i];
            break;
        case "--output":
            if (++i >= args.Length) throw new ArgumentException("--output requires a report file path.");
            reportPath = args[i];
            break;
        default:
            throw new ArgumentException($"Unknown benchmark argument '{args[i]}'. Use --baseline path, --output path, or --legacy7.");
    }
}
var baselinePath = Path.GetFullPath(requestedBaselinePath ?? "docs/win-optimization-baseline.json");
var optimizer = new AdvancedLineupOptimizer(null!, null!);
if (legacySeven)
{
    RunLegacySeven(optimizer);
    return;
}
var baseline = JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(baselinePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidDataException($"Could not read baseline {baselinePath}");
var fixtures = baseline.Fixtures.ToArray();
var comparisons = new List<ComparisonCapture>();
var failures = new List<string>();
Console.WriteLine("fixture,players,formation,profile,objective,assistant,venue,old_win_pct,new_win_pct,delta_pp,old_runtime_ms,new_runtime_ms,old_legal11,new_legal11,old_eligible,eligible,old_behavior_cap,behavior_cap,old_exact_reevaluation,exact_reevaluation,objective_correct,old_deterministic_repeat,permutation_stable");
foreach (var capture in fixtures)
{
    var fixture = capture.Fixture;
    var team = capture.Team;
    var opponent = capture.OpponentRatings;
    var request = capture.Request;
    var timer = Stopwatch.StartNew();
    var response = optimizer.OptimizeRoster(team, new OpponentRatingsResult { Ratings = opponent, Source = "fixed synthetic benchmark fixture" }, request);
    timer.Stop();
    var lineup = response.OptimalLineup;
    var eval = optimizer.EvaluateAssignedLineup(lineup, opponent, request);
    var oldWin = capture.OldWinProbability;
    var legal11 = IsLegal11(lineup);
    var eligible = lineup.Positions.Values.All(p => p.Player!.CanOptimize);
    var behaviorCount = lineup.Positions.Count(p => p.Key != p.Value.Behavior);
    var behaviorCap = behaviorCount <= 5 + fixture.Assistant;
    var selected = response.Alternatives.FirstOrDefault(a => RosterKey(a.Lineup) == RosterKey(lineup));
    var exact = selected is not null && SameEvaluation(eval, selected);
    var objectiveCorrect = selected is not null && response.Alternatives.All(a =>
        ObjectiveValue(request.Objective, a) <= ObjectiveValue(request.Objective, selected) + 1e-10);

    var reversed = CloneTeam(team);
    reversed.Players.Reverse();
    var shuffled = CloneTeam(team);
    var permutation = new Random(fixture.Seed);
    shuffled.Players = shuffled.Players.OrderBy(_ => permutation.Next()).ToList();
    var reversedResult = optimizer.OptimizeRoster(reversed, new OpponentRatingsResult { Ratings = CloneRatings(opponent), Source = "fixed synthetic benchmark fixture" }, CloneRequest(request));
    var shuffledResult = optimizer.OptimizeRoster(shuffled, new OpponentRatingsResult { Ratings = CloneRatings(opponent), Source = "fixed synthetic benchmark fixture" }, CloneRequest(request));
    var permutationStable = RosterKey(lineup) == RosterKey(reversedResult.OptimalLineup)
        && RosterKey(lineup) == RosterKey(shuffledResult.OptimalLineup);
    var delta = eval.WinProbability - oldWin;
    Console.WriteLine(string.Join(',', fixture.Id, team.Players.Count, fixture.Formation, fixture.Profile, request.Objective, fixture.Assistant,
        fixture.Home ? "home" : "away", F(oldWin * 100), F(eval.WinProbability * 100), F(delta * 100),
        capture.RuntimeMilliseconds.ToString("F1", CultureInfo.InvariantCulture), timer.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
        capture.Legal11, legal11, capture.Eligible, eligible, capture.BehaviorCapSatisfied, behaviorCap, capture.ExactReevaluation, exact, objectiveCorrect,
        capture.DeterministicPermutation, permutationStable));
    if (delta < -1e-9) failures.Add($"{fixture.Id}: Win probability regressed by {delta * 100:F6} percentage points.");
    if (!legal11) failures.Add($"{fixture.Id}: selected lineup is not a legal XI.");
    if (!eligible) failures.Add($"{fixture.Id}: selected lineup contains an ineligible player.");
    if (!behaviorCap) failures.Add($"{fixture.Id}: selected lineup exceeds assistant behavior cap.");
    if (!exact) failures.Add($"{fixture.Id}: selected alternative does not exactly reevaluate.");
    if (!objectiveCorrect) failures.Add($"{fixture.Id}: selected lineup is not best among reported alternatives for {request.Objective}.");
    if (!permutationStable) failures.Add($"{fixture.Id}: reversed or shuffled roster changes the selected roster.");
    comparisons.Add(new ComparisonCapture
    {
        Fixture = fixture.Id, OldWinProbability = oldWin, NewWinProbability = eval.WinProbability,
        DeltaPercentagePoints = delta * 100, OldRuntimeMilliseconds = capture.RuntimeMilliseconds, NewRuntimeMilliseconds = timer.Elapsed.TotalMilliseconds,
        OldLegal11 = capture.Legal11, NewLegal11 = legal11, OldEligible = capture.Eligible, NewEligible = eligible,
        OldBehaviorCapSatisfied = capture.BehaviorCapSatisfied, NewBehaviorCapSatisfied = behaviorCap,
        LegacyExactReevaluation = capture.ExactReevaluation, ExactReevaluation = exact, ObjectiveCorrect = objectiveCorrect,
        LegacyDeterministicRepeat = capture.DeterministicPermutation, DeterministicPermutationStable = permutationStable,
        NewOptimizerResponse = response
    });
}
if (reportPath is not null)
{
    var fullReportPath = Path.GetFullPath(reportPath);
    if (StringComparer.OrdinalIgnoreCase.Equals(fullReportPath, baselinePath))
        throw new InvalidOperationException("The comparative report path must not overwrite the baseline input.");
    File.WriteAllText(fullReportPath, JsonSerializer.Serialize(comparisons, new JsonSerializerOptions { WriteIndented = true }));
    Console.Error.WriteLine($"Wrote comparative results to {fullReportPath}; the baseline was not modified.");
}
Console.Error.WriteLine($"Compared {comparisons.Count} {(legacySeven ? "legacy7" : "win12")} fixtures against {baselinePath}; baseline was read-only.");
if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine($"FAIL: {failure}");
    Environment.ExitCode = 1;
}

static string F(double d) => d.ToString("F6", CultureInfo.InvariantCulture);
static double LegacyObjectiveValue(string objective, OptimizerEvaluation e) => objective switch
{
    "Win" => e.WinProbability,
    "Draw" => e.DrawProbability,
    _ => e.ExpectedPoints
};
static double ObjectiveValue(string objective, FormationAlternative a) => objective switch
{
    "Win" => a.WinProbability,
    "Draw" => a.DrawProbability,
    _ => a.ExpectedPoints
};
static bool IsLegal11(Lineup lineup) => lineup.Positions.Count == 11 && lineup.Positions.Values.All(p => p.Player is not null)
    && lineup.Positions.Select(p => p.Value.Player!.PlayerId).Distinct().Count() == 11
    && FormationData.Formations.TryGetValue(lineup.Formation ?? "", out var formation)
    && formation.Positions.ToHashSet(StringComparer.Ordinal).SetEquals(lineup.Positions.Keys)
    && lineup.Positions.All(p => FormationData.SlotBehaviourOptions[p.Key].Contains(p.Value.Behavior, StringComparer.Ordinal));
static bool SameEvaluation(OptimizerEvaluation e, FormationAlternative a) =>
    Math.Abs(e.ExpectedGoalsFor - a.ExpectedGoalsFor) <= 1e-10 && Math.Abs(e.ExpectedGoalsAgainst - a.ExpectedGoalsAgainst) <= 1e-10
    && Math.Abs(e.WinProbability - a.WinProbability) <= 1e-10 && Math.Abs(e.DrawProbability - a.DrawProbability) <= 1e-10
    && Math.Abs(e.LossProbability - a.LossProbability) <= 1e-10 && Math.Abs(e.ExpectedPoints - a.ExpectedPoints) <= 1e-10
    && Math.Abs(e.Ratings.Midfield - a.Ratings.Midfield) <= 1e-10 && Math.Abs(e.Ratings.RightDefense - a.Ratings.RightDefense) <= 1e-10
    && Math.Abs(e.Ratings.CentralDefense - a.Ratings.CentralDefense) <= 1e-10 && Math.Abs(e.Ratings.LeftDefense - a.Ratings.LeftDefense) <= 1e-10
    && Math.Abs(e.Ratings.RightAttack - a.Ratings.RightAttack) <= 1e-10 && Math.Abs(e.Ratings.CentralAttack - a.Ratings.CentralAttack) <= 1e-10
    && Math.Abs(e.Ratings.LeftAttack - a.Ratings.LeftAttack) <= 1e-10 && Math.Abs(e.Ratings.Overall - a.Ratings.Overall) <= 1e-10;
static string RosterKey(Lineup lineup) => lineup.Formation + ":" + lineup.TacticType + ":" + string.Join("|", lineup.Positions.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.Player!.PlayerId}/{p.Value.Behavior}"));
static void RunLegacySeven(AdvancedLineupOptimizer optimizer)
{
    Console.WriteLine("seed,players,venue,formation,opponent,objective,assistant,initial,optimized,delta,runtime_ms,legal11,eligible,behavior_cap");
    foreach (var fixture in LegacyFixture.All)
    {
        var team = MakeLegacyTeam(fixture);
        var request = MakeLegacyRequest(fixture);
        var placeholderOpponent = MakeLegacyPlaceholderOpponent(fixture.Seed);
        request.OpponentScenario = new OpponentScenario { Tactic = "Normal", Ratings = placeholderOpponent };
        var initialSeed = optimizer.CreateLegalInitialSeed(team, placeholderOpponent, request);
        var opponent = MakeLegacyOpponent(fixture, initialSeed.PredictedRatings);
        request.OpponentScenario.Ratings = opponent;
        var initialEval = optimizer.EvaluateAssignedLineup(initialSeed, opponent, request);
        var timer = Stopwatch.StartNew();
        var response = optimizer.OptimizeRoster(team, new OpponentRatingsResult { Ratings = opponent, Source = "reconstructed legacy benchmark" }, request);
        timer.Stop();
        var optimizedEval = optimizer.EvaluateAssignedLineup(response.OptimalLineup, opponent, request);
        var initialScore = LegacyObjectiveValue(request.Objective, initialEval);
        var optimizedScore = LegacyObjectiveValue(request.Objective, optimizedEval);
        var legal = IsLegal11(response.OptimalLineup);
        var eligible = response.OptimalLineup.Positions.Values.All(p => p.Player!.CanOptimize);
        var customOrders = response.OptimalLineup.Positions.Count(p => p.Key != p.Value.Behavior);
        var cap = customOrders <= 5 + fixture.Assistant;
        Console.WriteLine(string.Join(',', fixture.Seed, fixture.PlayerCount, fixture.Home ? "home" : "away",
            request.PreferredFormation, fixture.BalancedOpponent ? "balanced" : "challenging", request.Objective, fixture.Assistant,
            F(initialScore), F(optimizedScore), F(optimizedScore - initialScore),
            timer.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture), legal, eligible, cap));
        if (optimizedScore < initialScore - 1e-9 || !legal || !eligible || !cap)
            throw new InvalidOperationException($"Legacy fixture {fixture.Seed} failed objective or lineup validation.");
    }
}
static Team MakeLegacyTeam(LegacyFixture fixture)
{
    var random = new Random(fixture.Seed);
    var players = Enumerable.Range(1, fixture.PlayerCount).Select(i => new Player
    {
        PlayerId = fixture.Seed * 100 + i,
        FirstName = $"Seed{fixture.Seed}",
        LastName = $"Player{i}",
        Age = 20 + random.Next(15),
        Skills = new PlayerSkills
        {
            Keeper = random.Next(1, 13), KeeperAvailable = true,
            Defending = random.Next(3, 16), DefendingAvailable = true,
            Playmaking = random.Next(3, 16), PlaymakingAvailable = true,
            Winger = random.Next(2, 15), WingerAvailable = true,
            Passing = random.Next(2, 15), PassingAvailable = true,
            Scoring = random.Next(2, 16), ScoringAvailable = true,
            SetPieces = random.Next(1, 13), SetPiecesAvailable = true
        },
        Form = random.Next(4, 9),
        Stamina = random.Next(5, 10),
        StaminaAvailable = true,
        Experience = random.Next(1, 15),
        Loyalty = random.Next(1, 21),
        Specialty = random.Next(4) switch { 0 => "Powerful", 1 => "Quick", 2 => "Technical", _ => "" },
        InjuryLevel = -1,
        InjuryStatusKnown = true,
        IsSuspended = false,
        SuspensionStatusKnown = true
    }).ToList();
    return new Team { TeamId = fixture.Seed, TeamName = $"Legacy seed {fixture.Seed}", Players = players };
}
static TeamRatings MakeLegacyPlaceholderOpponent(int seed)
{
    var random = new Random(seed * 17 + 3);
    double Rating() => 8 + random.NextDouble();
    return new TeamRatings
    {
        MidfieldRating = Rating(), RightDefenseRating = Rating(), CentralDefenseRating = Rating(), LeftDefenseRating = Rating(),
        RightAttackRating = Rating(), CentralAttackRating = Rating(), LeftAttackRating = Rating(),
        IndirectSetPiecesAttRating = Rating(), IndirectSetPiecesDefRating = Rating()
    };
}
static TeamRatings MakeLegacyOpponent(LegacyFixture fixture, LineupRatings seedRatings)
{
    var random = new Random(fixture.Seed * 17 + 3);
    double Scale(double rating) => rating * (fixture.BalancedOpponent ? 0.85 + 0.30 * random.NextDouble() : 1.10 + 0.30 * random.NextDouble());
    return new TeamRatings
    {
        MidfieldRating = Scale(seedRatings.Midfield), RightDefenseRating = Scale(seedRatings.RightDefense),
        CentralDefenseRating = Scale(seedRatings.CentralDefense), LeftDefenseRating = Scale(seedRatings.LeftDefense),
        RightAttackRating = Scale(seedRatings.RightAttack), CentralAttackRating = Scale(seedRatings.CentralAttack),
        LeftAttackRating = Scale(seedRatings.LeftAttack), IndirectSetPiecesAttRating = Scale(seedRatings.CentralAttack),
        IndirectSetPiecesDefRating = Scale(seedRatings.CentralDefense)
    };
}
static OptimizerRequest MakeLegacyRequest(LegacyFixture fixture) => new()
{
    MyTeamId = fixture.Seed, OpponentTeamId = fixture.Seed + 1,
    PreferredFormation = fixture.AutoFormation ? "Auto" : "4-4-2", PreferredTactic = "Auto",
    TeamAttitude = "Normal", CoachType = "Neutral", AssistantManagerLevel = fixture.Assistant,
    TeamSpiritLevel = 3 + fixture.Seed % 5, ConfidenceLevel = 4 + fixture.Seed % 4,
    Objective = fixture.Objective, IsHomeMatch = fixture.Home, Language = "en",
    FormationExperience = new Dictionary<string, int>(StringComparer.Ordinal) { ["4-4-2"] = 8 }
};
static TeamRatings CloneRatings(TeamRatings x) => JsonSerializer.Deserialize<TeamRatings>(JsonSerializer.Serialize(x))!;
static Team CloneTeam(Team x) => JsonSerializer.Deserialize<Team>(JsonSerializer.Serialize(x))!;
static OptimizerRequest CloneRequest(OptimizerRequest x) => JsonSerializer.Deserialize<OptimizerRequest>(JsonSerializer.Serialize(x))!;

sealed class BaselineFile { public BaselineFile() { } public int SchemaVersion { get; set; } public string ModelVersion { get; set; } = ""; public DateTimeOffset CapturedUtc { get; set; } public List<FixtureCapture> Fixtures { get; set; } = []; }
sealed class FixtureCapture
{
    public FixtureCapture() { }
    public Fixture Fixture { get; set; } = null!; public Team Team { get; set; } = null!; public OptimizerRequest Request { get; set; } = null!;
    public TeamRatings OpponentRatings { get; set; } = null!; public OptimizerResponse OldOptimizerResponse { get; set; } = null!;
    public double OldWinProbability { get; set; } public double OldObjectiveValue { get; set; } public double RuntimeMilliseconds { get; set; }
    public bool Legal11 { get; set; } public bool Eligible { get; set; } public bool BehaviorCapSatisfied { get; set; } public bool ExactReevaluation { get; set; } public bool DeterministicPermutation { get; set; }
}
sealed class ComparisonCapture
{
    public ComparisonCapture() { }
    public string Fixture { get; set; } = ""; public double OldWinProbability { get; set; } public double NewWinProbability { get; set; }
    public double DeltaPercentagePoints { get; set; } public double OldRuntimeMilliseconds { get; set; } public double NewRuntimeMilliseconds { get; set; }
    public bool OldLegal11 { get; set; } public bool NewLegal11 { get; set; } public bool OldEligible { get; set; } public bool NewEligible { get; set; }
    public bool OldBehaviorCapSatisfied { get; set; } public bool NewBehaviorCapSatisfied { get; set; }
    public bool LegacyExactReevaluation { get; set; } public bool ExactReevaluation { get; set; } public bool ObjectiveCorrect { get; set; }
    public bool LegacyDeterministicRepeat { get; set; } public bool DeterministicPermutationStable { get; set; }
    public OptimizerResponse NewOptimizerResponse { get; set; } = new();
}
sealed record LegacyFixture(int Seed, int PlayerCount, bool Home, string Objective, int Assistant, bool AutoFormation, bool BalancedOpponent)
{
    public static readonly LegacyFixture[] All =
    [
        new(1101, 11, true, "ExpectedPoints", 0, false, true),
        new(2202, 14, false, "Win", 2, false, false),
        new(3303, 11, true, "Draw", 5, false, true),
        new(4404, 14, false, "ExpectedPoints", 1, false, false),
        new(5505, 12, true, "Win", 3, false, false),
        new(6606, 13, false, "Draw", 4, false, false),
        new(7707, 12, true, "ExpectedPoints", 2, true, false)
    ];
}
sealed record Fixture(string Id, int Seed, int PlayerCount, string Formation, string Profile, int Assistant, bool Home)
{
    public static readonly Fixture[] All =
    [
        new("balanced-auto-a0-home", 8101, 22, "Auto", "balanced", 0, true),
        new("balanced-forced-a5-away", 8102, 20, "4-4-2", "balanced", 5, false),
        new("strong-auto-a5-home", 8103, 19, "Auto", "strong", 5, true),
        new("strong-forced-a0-away", 8104, 24, "3-5-2", "strong", 0, false),
        new("weak-auto-a0-away", 8105, 21, "Auto", "weak", 0, false),
        new("weak-forced-a5-home", 8106, 18, "4-3-3", "weak", 5, true),
        new("asym-left-defense-auto", 8107, 23, "Auto", "asym-left-defense", 0, true),
        new("asym-right-defense-forced", 8108, 20, "4-5-1", "asym-right-defense", 5, false),
        new("asym-left-attack-auto", 8109, 25, "Auto", "asym-left-attack", 5, false),
        new("asym-right-attack-forced", 8110, 18, "3-4-3", "asym-right-attack", 0, true),
        new("specialists-auto-a0", 8111, 21, "Auto", "balanced", 0, false),
        new("specialists-forced-a5", 8112, 19, "4-4-2", "strong", 5, true)
    ];
}
