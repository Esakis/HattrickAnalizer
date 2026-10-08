using System.Text.Json;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;

namespace Backend.Tests;

public sealed class WinOptimizationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("balanced-forced-a5-away")]
    [InlineData("strong-forced-a0-away")]
    [InlineData("asym-left-attack-auto")]
    public void Win_result_is_legal_exact_and_top_ranked_on_captured_fixture(string fixtureId)
    {
        var fixture = LoadFixture(fixtureId);
        fixture.Request.Objective = "Win";
        var optimizer = new AdvancedLineupOptimizer(null!, null!);

        var response = optimizer.OptimizeRoster(Clone(fixture.Team),
            new OpponentRatingsResult { Ratings = Clone(fixture.OpponentRatings), Source = "captured fixed-seed fixture" },
            Clone(fixture.Request));

        var lineup = response.OptimalLineup;
        Assert.True(IsLegal11(lineup));
        Assert.All(lineup.Positions.Values, slot => Assert.True(slot.Player!.CanOptimize));
        Assert.InRange(lineup.Positions.Count(p => p.Key != p.Value.Behavior), 0, 5 + fixture.Request.AssistantManagerLevel);
        var best = response.Alternatives.OrderByDescending(a => a.WinProbability).First();
        Assert.Equal(RosterKey(best.Lineup), RosterKey(lineup));
        var reevaluated = optimizer.EvaluateAssignedLineup(lineup, fixture.OpponentRatings, fixture.Request);
        AssertAlternativeMatches(reevaluated, Assert.Single(response.Alternatives.Where(a => RosterKey(a.Lineup) == RosterKey(lineup))));
    }

    [Fact]
    public void Assistant_zero_considers_every_legal_paired_same_player_behavior_change()
    {
        var fixture = LoadFixture("strong-forced-a0-away");
        fixture.Request.Objective = "Win";
        var optimizer = new AdvancedLineupOptimizer(null!, null!);
        var response = optimizer.OptimizeRoster(Clone(fixture.Team),
            new OpponentRatingsResult { Ratings = Clone(fixture.OpponentRatings), Source = "captured fixed-seed fixture" },
            Clone(fixture.Request));
        var incumbent = response.OptimalLineup;
        var incumbentScore = optimizer.EvaluateAssignedLineup(incumbent, fixture.OpponentRatings, fixture.Request).WinProbability;
        var oldSeed = fixture.OldOptimizerResponse.OptimalLineup;
        Assert.Equal(5, oldSeed.Positions.Count(p => p.Key != p.Value.Behavior));
        var oldSeedBestPairedScore = BestPairedBehaviorScore(oldSeed, fixture, optimizer);
        Assert.True(incumbentScore + 1e-10 >= oldSeedBestPairedScore,
            $"Search result {incumbentScore:F12} did not reach the best legal paired behavior score {oldSeedBestPairedScore:F12} from the captured old XI.");
        Assert.True(BestPairedBehaviorScore(incumbent, fixture, optimizer) <= incumbentScore + 1e-10,
            "The selected XI has an improving legal paired same-player behavior neighbor.");
    }

    private static double BestPairedBehaviorScore(Lineup source, BaselineFixture fixture, AdvancedLineupOptimizer optimizer)
    {
        var best = optimizer.EvaluateAssignedLineup(source, fixture.OpponentRatings, fixture.Request).WinProbability;
        var slots = source.Positions.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        for (var left = 0; left < slots.Length; left++)
        for (var right = left + 1; right < slots.Length; right++)
        foreach (var leftBehavior in FormationData.SlotBehaviourOptions[slots[left]])
        foreach (var rightBehavior in FormationData.SlotBehaviourOptions[slots[right]])
        {
            var candidate = Clone(source);
            candidate.Positions[slots[left]].Behavior = leftBehavior;
            candidate.Positions[slots[right]].Behavior = rightBehavior;
            var customOrders = candidate.Positions.Count(p => p.Key != p.Value.Behavior);
            if (customOrders > 5 + fixture.Request.AssistantManagerLevel) continue;

            // The two player assignments stay fixed; only their behaviors change.
            Assert.Equal(source.Positions[slots[left]].Player!.PlayerId, candidate.Positions[slots[left]].Player!.PlayerId);
            Assert.Equal(source.Positions[slots[right]].Player!.PlayerId, candidate.Positions[slots[right]].Player!.PlayerId);
            var score = optimizer.EvaluateAssignedLineup(candidate, fixture.OpponentRatings, fixture.Request).WinProbability;
            best = Math.Max(best, score);
        }
        return best;
    }

    [Theory]
    [InlineData("Win")]
    [InlineData("ExpectedPoints")]
    public void Selected_objective_matches_best_reported_alternative(string objective)
    {
        var fixture = LoadFixture("balanced-auto-a0-home");
        fixture.Request.Objective = objective;
        var optimizer = new AdvancedLineupOptimizer(null!, null!);
        var response = optimizer.OptimizeRoster(Clone(fixture.Team),
            new OpponentRatingsResult { Ratings = Clone(fixture.OpponentRatings), Source = "captured fixed-seed fixture" },
            Clone(fixture.Request));

        Assert.True(response.Alternatives.Count > 1, "Auto formation must expose multiple candidates to make objective ranking meaningful.");
        var selected = Assert.Single(response.Alternatives.Where(a => RosterKey(a.Lineup) == RosterKey(response.OptimalLineup)));
        var selectedScore = Objective(selected, objective);
        Assert.All(response.Alternatives, alternative => Assert.True(selectedScore + 1e-10 >= Objective(alternative, objective)));
        if (objective == "Win") Assert.Equal(response.Alternatives.Max(a => a.WinProbability), selected.WinProbability, 10);
        else Assert.Equal(response.Alternatives.Max(a => a.ExpectedPoints), selected.ExpectedPoints, 10);
    }

    [Fact]
    public void Win_and_points_objectives_choose_different_rosters_on_a_tradeoff_fixture()
    {
        var optimizer = new AdvancedLineupOptimizer(null!, null!);
        var distinctObjectivesFound = false;
        foreach (var fixtureId in new[]
        {
            "specialists-auto-a0", "balanced-auto-a0-home", "weak-auto-a0-away",
            "strong-auto-a5-home", "asym-left-defense-auto", "asym-left-attack-auto"
        })
        {
            var fixture = LoadFixture(fixtureId);
            var winRequest = Clone(fixture.Request);
            winRequest.Objective = "Win";
            var pointsRequest = Clone(fixture.Request);
            pointsRequest.Objective = "ExpectedPoints";
            var winResult = optimizer.OptimizeRoster(Clone(fixture.Team),
                new OpponentRatingsResult { Ratings = Clone(fixture.OpponentRatings), Source = "captured fixed-seed fixture" }, winRequest);
            var pointsResult = optimizer.OptimizeRoster(Clone(fixture.Team),
                new OpponentRatingsResult { Ratings = Clone(fixture.OpponentRatings), Source = "captured fixed-seed fixture" }, pointsRequest);

            AssertSelectedObjective(winResult, "Win");
            AssertSelectedObjective(pointsResult, "ExpectedPoints");
            var winSelected = Assert.Single(winResult.Alternatives.Where(a => RosterKey(a.Lineup) == RosterKey(winResult.OptimalLineup)));
            var pointsSelected = Assert.Single(pointsResult.Alternatives.Where(a => RosterKey(a.Lineup) == RosterKey(pointsResult.OptimalLineup)));
            if (RosterKey(winResult.OptimalLineup) != RosterKey(pointsResult.OptimalLineup)
                && winSelected.WinProbability > pointsSelected.WinProbability + 1e-10
                && pointsSelected.ExpectedPoints > winSelected.ExpectedPoints + 1e-10)
            {
                distinctObjectivesFound = true;
                break;
            }
        }
        Assert.True(distinctObjectivesFound, "The fixed fixtures should include a Win-versus-points roster tradeoff.");
    }

    [Theory]
    [InlineData("weak-forced-a5-home", true)]
    [InlineData("balanced-forced-a5-away", false)]
    public void Comparison_opponent_ratings_apply_home_midfield_advantage_once(string fixtureId, bool ownTeamIsHome)
    {
        var fixture = LoadFixture(fixtureId);
        Assert.Equal(ownTeamIsHome, fixture.Request.IsHomeMatch);
        var optimizer = new AdvancedLineupOptimizer(null!, null!);
        var response = optimizer.OptimizeRoster(Clone(fixture.Team),
            new OpponentRatingsResult { Ratings = Clone(fixture.OpponentRatings), Source = "captured fixed-seed fixture" },
            Clone(fixture.Request));
        var input = fixture.OpponentRatings;
        var compared = response.Comparison.OpponentRatings;

        Assert.Equal(input.MidfieldRating * (ownTeamIsHome ? 1 : 1.19892), compared.Midfield, 10);
        Assert.Equal(input.RightDefenseRating, compared.RightDefense, 10);
        Assert.Equal(input.CentralDefenseRating, compared.CentralDefense, 10);
        Assert.Equal(input.LeftDefenseRating, compared.LeftDefense, 10);
        Assert.Equal(input.RightAttackRating, compared.RightAttack, 10);
        Assert.Equal(input.CentralAttackRating, compared.CentralAttack, 10);
        Assert.Equal(input.LeftAttackRating, compared.LeftAttack, 10);
    }

    private static void AssertSelectedObjective(OptimizerResponse response, string objective)
    {
        var selected = Assert.Single(response.Alternatives.Where(a => RosterKey(a.Lineup) == RosterKey(response.OptimalLineup)));
        Assert.All(response.Alternatives, alternative => Assert.True(Objective(selected, objective) + 1e-10 >= Objective(alternative, objective)));
    }

    private static BaselineFixture LoadFixture(string id)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "win-optimization-baseline.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "docs", "win-optimization-baseline.json");
        var snapshot = JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(path), JsonOptions)!;
        return Assert.Single(snapshot.Fixtures.Where(f => f.Fixture.Id == id));
    }

    private static bool IsLegal11(Lineup lineup) => lineup.Positions.Count == 11
        && lineup.Positions.Values.All(p => p.Player is not null)
        && lineup.Positions.Values.Select(p => p.Player!.PlayerId).Distinct().Count() == 11
        && FormationData.Formations.TryGetValue(lineup.Formation ?? "", out var formation)
        && formation.Positions.ToHashSet(StringComparer.Ordinal).SetEquals(lineup.Positions.Keys)
        && lineup.Positions.All(p => FormationData.SlotBehaviourOptions[p.Key].Contains(p.Value.Behavior, StringComparer.Ordinal));

    private static double Objective(FormationAlternative a, string objective) => objective switch
    {
        "Win" => a.WinProbability,
        "Draw" => a.DrawProbability,
        _ => a.ExpectedPoints
    };

    private static string RosterKey(Lineup lineup) => lineup.Formation + ":" + lineup.TacticType + ":" + string.Join("|",
        lineup.Positions.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.Player!.PlayerId}/{p.Value.Behavior}"));

    private static Team Clone(Team team) => JsonSerializer.Deserialize<Team>(JsonSerializer.Serialize(team), JsonOptions)!;
    private static TeamRatings Clone(TeamRatings ratings) => JsonSerializer.Deserialize<TeamRatings>(JsonSerializer.Serialize(ratings), JsonOptions)!;
    private static OptimizerRequest Clone(OptimizerRequest request) => JsonSerializer.Deserialize<OptimizerRequest>(JsonSerializer.Serialize(request), JsonOptions)!;
    private static Lineup Clone(Lineup lineup) => JsonSerializer.Deserialize<Lineup>(JsonSerializer.Serialize(lineup), JsonOptions)!;

    private static void AssertAlternativeMatches(OptimizerEvaluation expected, FormationAlternative actual)
    {
        Assert.Equal(expected.WinProbability, actual.WinProbability, 10);
        Assert.Equal(expected.DrawProbability, actual.DrawProbability, 10);
        Assert.Equal(expected.LossProbability, actual.LossProbability, 10);
        Assert.Equal(expected.ExpectedGoalsFor, actual.ExpectedGoalsFor, 10);
        Assert.Equal(expected.ExpectedGoalsAgainst, actual.ExpectedGoalsAgainst, 10);
        Assert.Equal(expected.Ratings.Midfield, actual.Ratings.Midfield, 10);
        Assert.Equal(expected.Ratings.RightDefense, actual.Ratings.RightDefense, 10);
        Assert.Equal(expected.Ratings.CentralDefense, actual.Ratings.CentralDefense, 10);
        Assert.Equal(expected.Ratings.LeftDefense, actual.Ratings.LeftDefense, 10);
        Assert.Equal(expected.Ratings.RightAttack, actual.Ratings.RightAttack, 10);
        Assert.Equal(expected.Ratings.CentralAttack, actual.Ratings.CentralAttack, 10);
        Assert.Equal(expected.Ratings.LeftAttack, actual.Ratings.LeftAttack, 10);
        Assert.Equal(expected.Ratings.Overall, actual.Ratings.Overall, 10);
    }

    private sealed class BaselineFile { public BaselineFile() { } public List<BaselineFixture> Fixtures { get; set; } = []; }
    private sealed class BaselineFixture
    {
        public BaselineFixture() { }
        public FixtureInfo Fixture { get; set; } = new();
        public Team Team { get; set; } = new();
        public OptimizerRequest Request { get; set; } = new();
        public TeamRatings OpponentRatings { get; set; } = new();
        public OptimizerResponse OldOptimizerResponse { get; set; } = new();
    }
    private sealed class FixtureInfo { public FixtureInfo() { } public string Id { get; set; } = ""; }
}
