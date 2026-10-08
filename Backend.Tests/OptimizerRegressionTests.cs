using HattrickAnalizer.Models;
using HattrickAnalizer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests;

public class OptimizerRegressionTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public OptimizerRegressionTests(ApiTestFactory factory) => _factory = factory;

    [Theory]
    [InlineData("ExpectedPoints")]
    [InlineData("Win")]
    [InlineData("Draw")]
    public async Task Alternatives_are_isolated_final_and_re_evaluate_under_the_selected_objective(string objective)
    {
        using var scope = _factory.Services.CreateScope();
        var optimizer = scope.ServiceProvider.GetRequiredService<AdvancedLineupOptimizer>();
        var request = Request(objective, isHome: true);

        var response = await optimizer.OptimizeLineupAsync(request);

        Assert.NotEmpty(response.Alternatives);
        Assert.Equal(11, response.OptimalLineup.Positions.Count);
        Assert.Equal(11, response.OptimalLineup.Positions.Values.Select(p => p.Player!.PlayerId).Distinct().Count());
        Assert.Contains("GK", response.OptimalLineup.Positions.Keys);
        var expectedOrder = response.Alternatives.Select(a => Objective(a.WinProbability, a.DrawProbability, objective)).ToArray();
        Assert.Equal(expectedOrder.OrderByDescending(x => x), expectedOrder);

        foreach (var alternative in response.Alternatives)
        {
            Assert.True(FormationData.Formations[alternative.Formation].Positions.ToHashSet(StringComparer.Ordinal)
                .SetEquals(alternative.Lineup.Positions.Keys));
            Assert.Equal(11, alternative.Lineup.Positions.Values.Select(p => p.Player!.PlayerId).Distinct().Count());
            var evaluation = optimizer.EvaluateAssignedLineup(alternative.Lineup, request.OpponentScenario!.Ratings!, request);
            Assert.Equal(evaluation.ExpectedGoalsFor, alternative.ExpectedGoalsFor, 10);
            Assert.Equal(evaluation.ExpectedGoalsAgainst, alternative.ExpectedGoalsAgainst, 10);
            Assert.Equal(evaluation.WinProbability, alternative.WinProbability, 10);
            Assert.Equal(evaluation.DrawProbability, alternative.DrawProbability, 10);
            Assert.Equal(evaluation.LossProbability, alternative.LossProbability, 10);
            Assert.Equal(evaluation.ExpectedPoints, alternative.ExpectedPoints, 10);
            AssertRatings(evaluation.Ratings, alternative.Ratings);
            Assert.Equal(SectorMean(alternative.Ratings), alternative.Ratings.Overall, 10);
        }

        var optimal = optimizer.EvaluateAssignedLineup(response.OptimalLineup, request.OpponentScenario!.Ratings!, request);
        var selectedAlternative = Assert.Single(response.Alternatives.Where(a => a.Formation == response.OptimalLineup.Formation
            && a.Tactic == response.OptimalLineup.TacticType
            && a.Lineup.Positions.All(p => response.OptimalLineup.Positions[p.Key].Player!.PlayerId == p.Value.Player!.PlayerId
                && response.OptimalLineup.Positions[p.Key].Behavior == p.Value.Behavior)));
        Assert.Equal(optimal.ExpectedGoalsFor, selectedAlternative.ExpectedGoalsFor, 10);
        Assert.Equal(optimal.ExpectedGoalsAgainst, selectedAlternative.ExpectedGoalsAgainst, 10);
        Assert.Equal(optimal.WinProbability, selectedAlternative.WinProbability, 10);
        AssertRatings(optimal.Ratings, response.OptimalLineup.PredictedRatings);
        Assert.Equal(SectorMean(response.OptimalLineup.PredictedRatings), response.OptimalLineup.PredictedRatings.Overall, 10);

        var slotToMutate = response.Alternatives[0].Lineup.Positions.Keys.First(k => k != "GK");
        var originalBehavior = response.Alternatives[0].Lineup.Positions[slotToMutate].Behavior;
        var replacementBehavior = FormationData.SlotBehaviourOptions[slotToMutate].First(b => b != originalBehavior);
        var optimalPlayerId = response.OptimalLineup.Positions[slotToMutate].Player!.PlayerId;
        var optimalKeeperSkill = response.OptimalLineup.Positions[slotToMutate].Player!.Skills.Keeper;
        var otherAlternative = response.Alternatives.Skip(1).FirstOrDefault(a => a.Lineup.Positions.ContainsKey(slotToMutate));
        var otherBehavior = otherAlternative?.Lineup.Positions[slotToMutate].Behavior;
        response.Alternatives[0].Lineup.Positions[slotToMutate].Behavior = replacementBehavior;
        response.Alternatives[0].Lineup.Positions[slotToMutate].Player!.Skills.Keeper = 20;
        Assert.Equal(originalBehavior, response.OptimalLineup.Positions[slotToMutate].Behavior);
        Assert.Equal(optimalPlayerId, response.OptimalLineup.Positions[slotToMutate].Player!.PlayerId);
        Assert.Equal(optimalKeeperSkill, response.OptimalLineup.Positions[slotToMutate].Player!.Skills.Keeper);
        if (otherAlternative is not null)
            Assert.Equal(otherBehavior, otherAlternative.Lineup.Positions[slotToMutate].Behavior);

        request.FormationExperience["4-4-2"] = 10;
        request.OpponentScenario!.Ratings!.MidfieldRating = 1;
        request.FocusAreas.Add("mutated-after-request");
        Assert.Equal(8, response.InputSnapshot.FormationExperience["4-4-2"]);
        Assert.Equal(8, response.InputSnapshot.OpponentScenario!.Ratings!.MidfieldRating);
        Assert.DoesNotContain("mutated-after-request", response.InputSnapshot.FocusAreas);

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exact_XI_evaluation_uses_own_home_or_away_context_consistently(bool isHome)
    {
        using var scope = _factory.Services.CreateScope();
        var optimizer = scope.ServiceProvider.GetRequiredService<AdvancedLineupOptimizer>();
        var request = Request("ExpectedPoints", isHome);
        request.PreferredFormation = "4-4-2";
        request.PreferredTactic = "Normal";
        var response = await optimizer.OptimizeLineupAsync(request);
        var alternative = Assert.Single(response.Alternatives);
        var reevaluated = optimizer.EvaluateAssignedLineup(alternative.Lineup, request.OpponentScenario!.Ratings!, request);
        var independent = new RatingEngine().ComputeContextualRatings(alternative.Lineup, RatingEngine.WeatherUnknown,
            alternative.Attitude, request.CoachType, alternative.DisorderRisk, request.TeamSpiritLevel, request.ConfidenceLevel);
        new RatingEngine().ApplyTactic(independent, alternative.Tactic);
        new RatingEngine().ApplyHomeAdvantage(independent, isHome);

        Assert.Equal(reevaluated.ExpectedGoalsFor, alternative.ExpectedGoalsFor, 10);
        Assert.Equal(reevaluated.ExpectedGoalsAgainst, alternative.ExpectedGoalsAgainst, 10);
        Assert.Equal(reevaluated.WinProbability, alternative.WinProbability, 10);
        Assert.Equal(reevaluated.LossProbability, alternative.LossProbability, 10);
        Assert.Equal(SectorMean(reevaluated.Ratings), reevaluated.Ratings.Overall, 10);
        Assert.Equal(SectorMean(alternative.Ratings), alternative.Ratings.Overall, 10);
        AssertRatings(independent, alternative.Ratings);
        Assert.NotEqual(alternative.Ratings.Midfield, response.Comparison.OpponentRatings.Midfield);
    }

    [Fact]
    public async Task Null_spirit_and_confidence_are_resolved_from_own_team_and_recorded_in_snapshot()
    {
        using var scope = _factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<HattrickApiService>();
        var optimizer = scope.ServiceProvider.GetRequiredService<AdvancedLineupOptimizer>();
        var ownTeam = await api.GetTeamDetailsAsync(100);
        var request = Request("ExpectedPoints", isHome: false);
        request.TeamSpiritLevel = null;
        request.ConfidenceLevel = null;
        request.PreferredFormation = "4-4-2";
        request.PreferredTactic = "Normal";

        var response = await optimizer.OptimizeLineupAsync(request);

        Assert.Equal(ownTeam.TeamSpiritLevel, response.InputSnapshot.TeamSpiritLevel);
        Assert.Equal(ownTeam.ConfidenceLevel, response.InputSnapshot.ConfidenceLevel);
    }

    private static OptimizerRequest Request(string objective, bool isHome) => new()
    {
        MyTeamId = 100, OpponentTeamId = 200, PreferredFormation = "Auto", PreferredTactic = "Auto",
        Objective = objective, IsHomeMatch = isHome, TeamAttitude = "Normal", CoachType = "Neutral",
        TeamSpiritLevel = 5, ConfidenceLevel = 5,
        FormationExperience = FormationData.Formations.Keys.ToDictionary(k => k, _ => 8, StringComparer.Ordinal),
        OpponentScenario = new OpponentScenario
        {
            Tactic = "Normal",
            Ratings = new TeamRatings
            {
                MidfieldRating = 8, RightDefenseRating = 8, CentralDefenseRating = 9, LeftDefenseRating = 8,
                RightAttackRating = 9, CentralAttackRating = 9, LeftAttackRating = 9,
                IndirectSetPiecesAttRating = 7, IndirectSetPiecesDefRating = 8
            }
        }
    };

    private static double Objective(double win, double draw, string objective) => objective switch
    {
        "Win" => win,
        "Draw" => draw,
        _ => 3 * win + draw
    };

    private static double SectorMean(LineupRatings ratings) => (ratings.Midfield + ratings.RightDefense + ratings.CentralDefense
        + ratings.LeftDefense + ratings.RightAttack + ratings.CentralAttack + ratings.LeftAttack) / 7.0;

    private static void AssertRatings(LineupRatings expected, LineupRatings actual)
    {
        Assert.Equal(expected.Midfield, actual.Midfield, 10);
        Assert.Equal(expected.RightDefense, actual.RightDefense, 10);
        Assert.Equal(expected.CentralDefense, actual.CentralDefense, 10);
        Assert.Equal(expected.LeftDefense, actual.LeftDefense, 10);
        Assert.Equal(expected.RightAttack, actual.RightAttack, 10);
        Assert.Equal(expected.CentralAttack, actual.CentralAttack, 10);
        Assert.Equal(expected.LeftAttack, actual.LeftAttack, 10);
    }
}
