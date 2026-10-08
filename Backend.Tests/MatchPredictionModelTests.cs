using HattrickAnalizer.Models;
using HattrickAnalizer.Services;

namespace Backend.Tests;

public class MatchPredictionModelTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(1000)]
    public void Poisson_probabilities_are_normalized_and_symmetric(double xg)
    {
        var result = MatchPredictionModel.Predict(xg, xg);

        Assert.Equal(1, result.WinProbability + result.DrawProbability + result.LossProbability, 12);
        Assert.Equal(result.WinProbability, result.LossProbability, 12);
        Assert.InRange(result.DrawProbability, 0, 1);
    }

    [Fact]
    public void Zero_xg_is_a_scoreless_draw_and_corrupt_huge_xg_is_rejected()
    {
        var zero = MatchPredictionModel.Predict(0, 0);

        Assert.Equal(0, zero.WinProbability);
        Assert.Equal(1, zero.DrawProbability);
        Assert.Equal(0, zero.LossProbability);
        Assert.Throws<ArgumentOutOfRangeException>(() => MatchPredictionModel.Predict(100_001, 1));
    }

    [Fact]
    public void Pressing_on_both_sides_reduces_both_expected_goal_rates()
    {
        var home = Side("Pressing", midfield: 10, attack: 8, defense: 7, suppression: 0.2);
        var away = Side("Pressing", midfield: 9, attack: 7, defense: 8, suppression: 0.1);
        var baseline = MatchPredictionModel.PredictMatch(Side("Normal", 10, 8, 7), Side("Normal", 9, 7, 8)).Probabilities;

        var pressed = MatchPredictionModel.PredictMatch(home, away).Probabilities;

        Assert.True(pressed.ExpectedGoalsFor < baseline.ExpectedGoalsFor);
        Assert.True(pressed.ExpectedGoalsAgainst < baseline.ExpectedGoalsAgainst);
        Assert.Equal(baseline.ExpectedGoalsFor * 0.72, pressed.ExpectedGoalsFor, 8);
        Assert.Equal(baseline.ExpectedGoalsAgainst * 0.72, pressed.ExpectedGoalsAgainst, 8);
    }

    [Fact]
    public void Home_advantage_is_applied_once_and_not_when_already_in_observed_ratings()
    {
        var home = Side("Normal", midfield: 10, attack: 8, defense: 7);
        var away = Side("Normal", midfield: 9, attack: 7, defense: 8);
        var without = MatchPredictionModel.PredictMatch(home, away);
        var withBonus = MatchPredictionModel.PredictMatch(WithHome(home, true), away);
        var alreadyApplied = MatchPredictionModel.PredictMatch(WithHome(home, true, true), away);

        Assert.True(withBonus.Probabilities.ExpectedGoalsFor > without.Probabilities.ExpectedGoalsFor);
        Assert.Equal(without.Probabilities.ExpectedGoalsFor, alreadyApplied.Probabilities.ExpectedGoalsFor, 12);
    }

    [Fact]
    public void Long_shots_preserve_the_fixed_set_piece_route()
    {
        var homeNormal = Side("Normal", 10, 8, 7, ispAttack: 0, applyRatingEffects: false);
        var homeNormalWithIsp = Side("Normal", 10, 8, 7, ispAttack: 15, applyRatingEffects: false);
        var homeLongShots = Side("LongShots", 10, 8, 7, ispAttack: 0, longShot: 0.5, applyRatingEffects: false);
        var homeLongShotsWithIsp = Side("LongShots", 10, 8, 7, ispAttack: 15, longShot: 0.5, applyRatingEffects: false);
        var away = Side("Normal", 9, 7, 8, ispDefense: 11);

        var normalBase = MatchPredictionModel.PredictMatch(homeNormal, away).Probabilities.ExpectedGoalsFor;
        var normalWithIsp = MatchPredictionModel.PredictMatch(homeNormalWithIsp, away).Probabilities.ExpectedGoalsFor;
        var longShotsBase = MatchPredictionModel.PredictMatch(homeLongShots, away).Probabilities.ExpectedGoalsFor;
        var longShotsWithIsp = MatchPredictionModel.PredictMatch(homeLongShotsWithIsp, away).Probabilities.ExpectedGoalsFor;

        Assert.Equal(normalWithIsp - normalBase, longShotsWithIsp - longShotsBase, 12);
    }

    [Fact]
    public void AIM_and_AOW_conserve_equal_route_weights_but_change_asymmetric_attack_routes()
    {
        var equalNormal = EqualAttackSide("Normal");
        var equalAim = EqualAttackSide("AttackInMiddle");
        var equalAow = EqualAttackSide("AttackOnWings");
        var opponent = EqualAttackSide("Normal");

        var baselineEqual = MatchPredictionModel.PredictMatch(equalNormal, opponent).Probabilities.ExpectedGoalsFor;
        Assert.Equal(baselineEqual, MatchPredictionModel.PredictMatch(equalAim, opponent).Probabilities.ExpectedGoalsFor, 12);
        Assert.Equal(baselineEqual, MatchPredictionModel.PredictMatch(equalAow, opponent).Probabilities.ExpectedGoalsFor, 12);

        var asymmetric = Side("Normal", 10, 8, 8);
        asymmetric = WithAttacks(asymmetric, center: 20, right: 2, left: 2);
        var normal = MatchPredictionModel.PredictMatch(asymmetric, opponent).Probabilities.ExpectedGoalsFor;
        var aim = MatchPredictionModel.PredictMatch(WithTactic(asymmetric, "AttackInMiddle"), opponent).Probabilities.ExpectedGoalsFor;
        var aow = MatchPredictionModel.PredictMatch(WithTactic(asymmetric, "AttackOnWings"), opponent).Probabilities.ExpectedGoalsFor;
        Assert.True(aim > normal);
        Assert.True(aow < normal);
    }

    [Fact]
    public void Counter_eligibility_is_checked_before_the_midfield_penalty()
    {
        const double homeMidfieldBeforePenalty = 10.1;
        const double opponentMidfield = 10;
        var penalty = FormationData.TacticModifiers.CounterAttackMidfieldPenalty;
        var normalEquivalent = Side("Normal", homeMidfieldBeforePenalty * penalty, 8, 8);
        var away = Side("Normal", opponentMidfield, 8, 8);
        var counter = Side("Counter", homeMidfieldBeforePenalty, 8, 8, counter: 0.4);

        var expectedWithoutCounterEligibility = MatchPredictionModel.PredictMatch(normalEquivalent, away).Probabilities;
        var actual = MatchPredictionModel.PredictMatch(counter, away).Probabilities;

        Assert.True(homeMidfieldBeforePenalty > opponentMidfield);
        Assert.True(homeMidfieldBeforePenalty * penalty < opponentMidfield);
        Assert.Equal(expectedWithoutCounterEligibility.ExpectedGoalsFor, actual.ExpectedGoalsFor, 12);
    }

    [Fact]
    public void Counter_adds_quality_only_for_opponent_chances_that_were_not_scored()
    {
        const double conversion = 0.4;
        const double homeMidfield = 8;
        const double awayMidfield = 10;
        var penalty = FormationData.TacticModifiers.CounterAttackMidfieldPenalty;
        var home = Side("Counter", homeMidfield, 8, 8, counter: conversion);
        var away = Side("Normal", awayMidfield, 8, 8);
        var normal = MatchPredictionModel.PredictMatch(Side("Normal", homeMidfield * penalty, 8, 8), away).Probabilities;
        var counter = MatchPredictionModel.PredictMatch(home, away).Probabilities;
        var hPow = Math.Pow(homeMidfield * penalty, 2.75);
        var aPow = Math.Pow(awayMidfield, 2.75);
        var homeActions = 10 * hPow / (hPow + aPow);
        var awayActions = 10 - homeActions;
        var homeGoalQuality = normal.ExpectedGoalsFor / homeActions;
        var awayGoalQuality = normal.ExpectedGoalsAgainst / awayActions;
        var expectedExtra = awayActions * (1 - awayGoalQuality) * conversion * homeGoalQuality;

        Assert.Equal(normal.ExpectedGoalsFor + expectedExtra, counter.ExpectedGoalsFor, 12);
        Assert.Equal(normal.ExpectedGoalsAgainst, counter.ExpectedGoalsAgainst, 12);
    }

    [Fact]
    public void League_lambdas_match_the_shared_tactical_model_without_duplicate_rating_penalties()
    {
        var home = new TeamRatings
        {
            MidfieldRating = 45, RightDefenseRating = 42, CentralDefenseRating = 48, LeftDefenseRating = 41,
            RightAttackRating = 47, CentralAttackRating = 50, LeftAttackRating = 39,
            IndirectSetPiecesAttRating = 13, IndirectSetPiecesDefRating = 11
        };
        var away = new TeamRatings
        {
            MidfieldRating = 44, RightDefenseRating = 38, CentralDefenseRating = 51, LeftDefenseRating = 46,
            RightAttackRating = 42, CentralAttackRating = 49, LeftAttackRating = 44,
            IndirectSetPiecesAttRating = 12, IndirectSetPiecesDefRating = 15
        };
        var expected = MatchPredictionModel.PredictMatch(ObservedSide(home, "LongShots", true), ObservedSide(away, "Counter", false));

        var actual = LeagueSimulationService.ComputeLambdas(home, away, "LongShots", "Counter");

        Assert.Equal(expected.Probabilities.ExpectedGoalsFor, actual.Home, 12);
        Assert.Equal(expected.Probabilities.ExpectedGoalsAgainst, actual.Away, 12);
    }

    private static MatchTacticalSide Side(string tactic, double midfield, double attack, double defense,
        double? suppression = null, double ispAttack = 0, double ispDefense = 0, double? longShot = null,
        double? counter = null, bool applyRatingEffects = true) => new()
    {
        Tactic = tactic,
        Ratings = new LineupRatings
        {
            Midfield = midfield, CentralAttack = attack, RightAttack = attack + 0.5, LeftAttack = attack - 0.5,
            CentralDefense = defense, RightDefense = defense + 0.5, LeftDefense = defense - 0.5
        },
        PressingSuppression = suppression,
        CounterConversion = counter,
        ApplyCurrentTacticRatingEffects = applyRatingEffects,
        IndirectSetPieceAttack = ispAttack,
        IndirectSetPieceDefense = ispDefense,
        LongShotGoalProbability = longShot
    };

    private static MatchTacticalSide WithHome(MatchTacticalSide side, bool isHome, bool alreadyApplied = false) => new()
    {
        Ratings = side.Ratings, Tactic = side.Tactic, IsHome = isHome, HomeAdvantageAlreadyApplied = alreadyApplied,
        PressingSuppression = side.PressingSuppression, IndirectSetPieceAttack = side.IndirectSetPieceAttack,
        IndirectSetPieceDefense = side.IndirectSetPieceDefense, LongShotGoalProbability = side.LongShotGoalProbability
    };

    private static MatchTacticalSide EqualAttackSide(string tactic) => new()
    {
        Tactic = tactic, ApplyCurrentTacticRatingEffects = false, PassingSkillTotal = 20,
        Ratings = new LineupRatings { Midfield = 10, RightDefense = 8, CentralDefense = 8, LeftDefense = 8,
            RightAttack = 8, CentralAttack = 8, LeftAttack = 8 }
    };

    private static MatchTacticalSide WithAttacks(MatchTacticalSide source, double center, double right, double left) => new()
    {
        Tactic = source.Tactic, ApplyCurrentTacticRatingEffects = false, PassingSkillTotal = 20,
        Ratings = new LineupRatings { Midfield = source.Ratings.Midfield, RightDefense = source.Ratings.RightDefense,
            CentralDefense = source.Ratings.CentralDefense, LeftDefense = source.Ratings.LeftDefense,
            CentralAttack = center, RightAttack = right, LeftAttack = left }
    };

    private static MatchTacticalSide WithTactic(MatchTacticalSide source, string tactic) => new()
    {
        Tactic = tactic, ApplyCurrentTacticRatingEffects = false, PassingSkillTotal = 20,
        Ratings = source.Ratings
    };

    private static MatchTacticalSide ObservedSide(TeamRatings ratings, string tactic, bool home) => new()
    {
        Tactic = tactic, IsHome = home, RatingsIncludeHistoricalTacticEffects = true,
        ApplyCurrentTacticRatingEffects = false,
        IndirectSetPieceAttack = ratings.IndirectSetPiecesAttRating,
        IndirectSetPieceDefense = ratings.IndirectSetPiecesDefRating,
        Ratings = new LineupRatings
        {
            Midfield = ratings.MidfieldRating, RightDefense = ratings.RightDefenseRating,
            CentralDefense = ratings.CentralDefenseRating, LeftDefense = ratings.LeftDefenseRating,
            RightAttack = ratings.RightAttackRating, CentralAttack = ratings.CentralAttackRating,
            LeftAttack = ratings.LeftAttackRating
        }
    };
}
