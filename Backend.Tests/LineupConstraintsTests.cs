using System.Net;
using System.Net.Http.Json;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests;

public class LineupConstraintsTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    public LineupConstraintsTests(ApiTestFactory factory) => _factory = factory;

    [Theory]
    [InlineData("Win", "Auto")]
    [InlineData("ExpectedPoints", "4-4-2")]
    public async Task Every_returned_candidate_honours_required_excluded_and_locked_players(string objective, string formation)
    {
        using var scope = _factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<HattrickApiService>();
        var optimizer = scope.ServiceProvider.GetRequiredService<AdvancedLineupOptimizer>();
        var team = await api.GetTeamDetailsAsync(10);
        var eligible = team.Players.Where(p => p.CanOptimize).OrderBy(p => p.PlayerId).ToList();
        var request = Request();
        request.Objective = objective;
        request.PreferredFormation = formation;
        request.LockedPositions["GK"] = eligible[^1].PlayerId; // Deliberately force a non-greedy keeper.
        request.RequiredPlayerIds = eligible.TakeLast(3).Select(p => p.PlayerId).ToList();
        request.ExcludedPlayerIds = new() { eligible[0].PlayerId };
        var result = optimizer.OptimizeRoster(team, Opponent(), request);
        foreach (var lineup in result.Alternatives.Select(a => a.Lineup).Append(result.OptimalLineup))
        {
            var ids = lineup.Positions.Values.Select(p => p.Player!.PlayerId).ToList();
            Assert.Equal(11, ids.Distinct().Count());
            Assert.All(request.RequiredPlayerIds, id => Assert.Contains(id, ids));
            Assert.DoesNotContain(eligible[0].PlayerId, ids);
            Assert.Equal(eligible[^1].PlayerId, lineup.Positions["GK"].Player!.PlayerId);
        }
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("unknown")]
    [InlineData("unavailable")]
    [InlineData("duplicate-lock")]
    [InlineData("wrong-formation")]
    [InlineData("too-many")]
    public async Task Impossible_constraints_are_rejected_explicitly(string invalidCase)
    {
        using var scope = _factory.Services.CreateScope();
        var team = await scope.ServiceProvider.GetRequiredService<HattrickApiService>().GetTeamDetailsAsync(10);
        var optimizer = scope.ServiceProvider.GetRequiredService<AdvancedLineupOptimizer>();
        var ids = team.Players.Where(p => p.CanOptimize).Select(p => p.PlayerId).ToList();
        var request = Request();
        switch (invalidCase)
        {
            case "conflict": request.RequiredPlayerIds.Add(ids[0]); request.ExcludedPlayerIds.Add(ids[0]); break;
            case "unknown": request.RequiredPlayerIds.Add(int.MaxValue); break;
            case "unavailable": team.Players.Single(p => p.PlayerId == ids[0]).InjuryLevel = 2; request.RequiredPlayerIds.Add(ids[0]); break;
            case "duplicate-lock": request.LockedPositions = new() { ["GK"] = ids[0], ["RWB"] = ids[0] }; break;
            case "wrong-formation": request.LockedPositions["CD"] = ids[0]; break;
            case "too-many": request.RequiredPlayerIds = ids.Take(12).ToList(); break;
        }
        Assert.Throws<ArgumentException>(() => optimizer.OptimizeRoster(team, Opponent(), request));
    }

    [Fact]
    public async Task Manual_evaluation_preserves_exact_XI_behaviours_and_uses_roster_skills()
    {
        using var scope = _factory.Services.CreateScope();
        var team = await scope.ServiceProvider.GetRequiredService<HattrickApiService>().GetTeamDetailsAsync(10);
        var optimizer = scope.ServiceProvider.GetRequiredService<AdvancedLineupOptimizer>();
        var players = team.Players.Where(p => p.CanOptimize).Take(11).ToList();
        var request = new EvaluateLineupRequest
        {
            Context = Request(), Tactic = "Pressing",
            Positions = FormationData.Formations["4-4-2"].Positions.Select((slot, i) => (slot, i))
                .ToDictionary(x => x.slot, x => new EvaluationPosition { PlayerId = players[x.i].PlayerId, Behaviour = x.slot })
        };
        request.Positions["RW"].Behaviour = "WO";
        var result = optimizer.EvaluateRoster(team, Opponent(), request);
        Assert.Single(result.Alternatives);
        Assert.Equal("Pressing", result.OptimalLineup.TacticType);
        foreach (var (slot, order) in request.Positions)
        {
            Assert.Equal(order.PlayerId, result.OptimalLineup.Positions[slot].Player!.PlayerId);
            Assert.Equal(order.Behaviour, result.OptimalLineup.Positions[slot].Behavior);
        }
        var reevaluation = optimizer.EvaluateAssignedLineup(result.OptimalLineup, Opponent().Ratings, result.InputSnapshot);
        Assert.Equal(reevaluation.WinProbability, result.Alternatives[0].WinProbability, 10);
        request.Context.ExcludedPlayerIds.Add(players[0].PlayerId);
        Assert.Throws<ArgumentException>(() => optimizer.EvaluateRoster(team, Opponent(), request));
    }

    [Fact]
    public async Task Evaluate_endpoint_rejects_foreign_player_with_structured_validation_error()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/optimizer/evaluate", new EvaluateLineupRequest
        {
            Context = Request(), Positions = new() { ["GK"] = new() { PlayerId = int.MaxValue, Behaviour = "GK" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(true, "scout")]
    [InlineData(false, "scout")]
    [InlineData(false, "lastMatch")]
    [InlineData(false, "explicit")]
    public async Task Sensitivity_one_reproduces_baseline_and_scaling_preserves_home_tactics_and_set_pieces(bool isHome, string source)
    {
        using var scope = _factory.Services.CreateScope();
        var team = await scope.ServiceProvider.GetRequiredService<HattrickApiService>().GetTeamDetailsAsync(10);
        var optimizer = scope.ServiceProvider.GetRequiredService<AdvancedLineupOptimizer>();
        var context = Request();
        context.IsHomeMatch = isHome;
        var opponent = Opponent();
        opponent.Source = source;
        opponent.Ratings.IndirectSetPiecesAttRating = 17;
        opponent.Ratings.IndirectSetPiecesDefRating = 3;
        context.OpponentScenario = source == "explicit" ? new() { Tactic = "Pressing", Ratings = opponent.Ratings } : null;
        var baseline = optimizer.OptimizeRoster(team, opponent, context);
        var evaluation = new EvaluateLineupRequest
        {
            Context = context, Tactic = baseline.OptimalLineup.TacticType,
            Positions = baseline.OptimalLineup.Positions.ToDictionary(kv => kv.Key,
                kv => new EvaluationPosition { PlayerId = kv.Value.Player!.PlayerId, Behaviour = kv.Value.Behavior })
        };
        var same = optimizer.EvaluateRoster(team, opponent, evaluation);
        Assert.Equal(baseline.Alternatives[0].WinProbability, same.Alternatives[0].WinProbability, 10);
        Assert.Equal(baseline.Comparison.OpponentRatings.Midfield, same.Comparison.OpponentRatings.Midfield, 10);
        context.OpponentRatingScale = 1.15;
        var scaled = optimizer.EvaluateRoster(team, opponent, evaluation);
        Assert.Equal(same.Comparison.OpponentRatings.Midfield * 1.15, scaled.Comparison.OpponentRatings.Midfield, 10);
        Assert.Equal(1.15, scaled.InputSnapshot.OpponentRatingScale);
        Assert.Equal(17, opponent.Ratings.IndirectSetPiecesAttRating); // Original input is not mutated.

        // An explicitly scaled copy must produce the same result, including indirect set pieces.
        var copied = new OpponentRatingsResult { Source = source, Ratings = new()
        {
            MidfieldRating = 8 * 1.15, CentralDefenseRating = 8 * 1.15, LeftDefenseRating = 8 * 1.15,
            RightDefenseRating = 8 * 1.15, CentralAttackRating = 8 * 1.15, LeftAttackRating = 8 * 1.15,
            RightAttackRating = 8 * 1.15, IndirectSetPiecesAttRating = 17 * 1.15, IndirectSetPiecesDefRating = 3 * 1.15
        }};
        context.OpponentRatingScale = 1;
        if (context.OpponentScenario != null) context.OpponentScenario.Ratings = copied.Ratings;
        var expected = optimizer.EvaluateRoster(team, copied, evaluation);
        Assert.Equal(expected.Alternatives[0].WinProbability, scaled.Alternatives[0].WinProbability, 10);
        Assert.Equal(expected.Alternatives[0].ExpectedGoalsAgainst, scaled.Alternatives[0].ExpectedGoalsAgainst, 10);
    }

    private static OptimizerRequest Request() => new()
    {
        MyTeamId = 10, OpponentTeamId = 20, PreferredFormation = "4-4-2", PreferredTactic = "Normal",
        FormationExperience = FormationData.Formations.Keys.ToDictionary(x => x, _ => 8),
        TeamSpiritLevel = 5, ConfidenceLevel = 5,
        OpponentScenario = new() { Tactic = "Normal", Ratings = Opponent().Ratings }
    };
    private static OpponentRatingsResult Opponent() => new()
    {
        Source = "fixture", Ratings = new() { MidfieldRating = 8, CentralDefenseRating = 8, LeftDefenseRating = 8,
            RightDefenseRating = 8, CentralAttackRating = 8, LeftAttackRating = 8, RightAttackRating = 8 }
    };
}
