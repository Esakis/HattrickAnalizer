using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>Shared tactical xG and finite Poisson outcome model used by optimizer and league simulation.</summary>
public static class MatchPredictionModel
{
    public const string Version = "core-2";
    private const double MidfieldExponent = 2.75;
    private const double FinishExponent = 3.5;

    public static TacticalMatchPrediction PredictMatch(MatchTacticalSide home, MatchTacticalSide away)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(away);
        var h = CloneRatings(home.Ratings);
        var a = CloneRatings(away.Ratings);
        ValidateRatings(h, nameof(home));
        ValidateRatings(a, nameof(away));
        ValidateSideInputs(home, nameof(home));
        ValidateSideInputs(away, nameof(away));

        NormalizeObservedTacticEffects(h, home);
        NormalizeObservedTacticEffects(a, away);
        if (home.IsHome && !home.HomeAdvantageAlreadyApplied) h.Midfield *= FormationData.TacticModifiers.HomeAdvantage;
        if (away.IsHome && !away.HomeAdvantageAlreadyApplied) a.Midfield *= FormationData.TacticModifiers.HomeAdvantage;

        // Hattrick explicitly checks CA possession eligibility before its 7% midfield penalty.
        var homeShareBeforeTactics = Share(h.Midfield, a.Midfield);
        var awayShareBeforeTactics = 1 - homeShareBeforeTactics;
        ApplyTacticRatingEffects(h, home.Tactic, home.ApplyCurrentTacticRatingEffects);
        ApplyTacticRatingEffects(a, away.Tactic, away.ApplyCurrentTacticRatingEffects);

        var homeShare = Share(h.Midfield, a.Midfield);
        var homeActions = 10 * homeShare;
        var awayActions = 10 * (1 - homeShare);
        var homeIspAtt = home.IndirectSetPieceAttack > 0 ? home.IndirectSetPieceAttack : 0.8 * h.CentralAttack;
        var homeIspDef = home.IndirectSetPieceDefense > 0 ? home.IndirectSetPieceDefense : 0.9 * h.CentralDefense;
        var awayIspAtt = away.IndirectSetPieceAttack > 0 ? away.IndirectSetPieceAttack : 0.8 * a.CentralAttack;
        var awayIspDef = away.IndirectSetPieceDefense > 0 ? away.IndirectSetPieceDefense : 0.9 * a.CentralDefense;

        var homeCenter = Finish(h.CentralAttack, a.CentralDefense);
        var homeRight = Finish(h.RightAttack, a.LeftDefense);
        var homeLeft = Finish(h.LeftAttack, a.RightDefense);
        var homeSetPiece = Finish(homeIspAtt, awayIspDef);
        var awayCenter = Finish(a.CentralAttack, h.CentralDefense);
        var awayRight = Finish(a.RightAttack, h.LeftDefense);
        var awayLeft = Finish(a.LeftAttack, h.RightDefense);
        var awaySetPiece = Finish(awayIspAtt, homeIspDef);

        var homePGoal = RouteOpenPlay(homeCenter, homeRight, homeLeft, homeSetPiece, home);
        var awayPGoal = RouteOpenPlay(awayCenter, awayRight, awayLeft, awaySetPiece, away);
        var homeLambda = homeActions * homePGoal;
        var awayLambda = awayActions * awayPGoal;
        if (home.Tactic == "Counter" && homeShareBeforeTactics < 0.5)
            homeLambda += awayActions * (1 - awayPGoal) * CounterQuality(home) * homePGoal;
        if (away.Tactic == "Counter" && awayShareBeforeTactics < 0.5)
            awayLambda += homeActions * (1 - homePGoal) * CounterQuality(away) * awayPGoal;

        // Pressing stops potential chances on both sides; if both press, effects stack.
        var homePress = home.Tactic == "Pressing" ? PressingSuppression(home) : 0;
        var awayPress = away.Tactic == "Pressing" ? PressingSuppression(away) : 0;
        homeLambda *= (1 - homePress) * (1 - awayPress);
        awayLambda *= (1 - homePress) * (1 - awayPress);
        homeLambda = Math.Max(0.05, homeLambda);
        awayLambda = Math.Max(0.05, awayLambda);

        RecomputeOverall(h);
        RecomputeOverall(a);

        var probabilities = Predict(homeLambda, awayLambda);
        return new TacticalMatchPrediction(h, a, probabilities);
    }

    public static MatchProbability Predict(double homeLambda, double awayLambda)
    {
        if (!double.IsFinite(homeLambda) || !double.IsFinite(awayLambda) || homeLambda < 0 || awayLambda < 0)
            throw new ArgumentOutOfRangeException(nameof(homeLambda), "Expected-goal rates must be finite and non-negative.");
        var home = PoissonMass(homeLambda);
        var away = PoissonMass(awayLambda);
        double win = 0, draw = 0, loss = 0, awayCdf = 0;
        for (int i = 0; i < home.Length; i++)
        {
            win += home[i] * awayCdf;
            double awayInclusive = awayCdf;
            if (i < away.Length)
            {
                draw += home[i] * away[i];
                awayInclusive += away[i];
            }
            loss += home[i] * Math.Max(0, 1 - awayInclusive);
            awayCdf = awayInclusive;
        }
        var sum = win + draw + loss;
        if (!(sum > 0) || !double.IsFinite(sum)) throw new ArithmeticException("Could not normalize scoreline probabilities.");
        return new MatchProbability(win / sum, draw / sum, loss / sum, homeLambda, awayLambda);
    }

    public static int SamplePoisson(Random random, double lambda)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!double.IsFinite(lambda) || lambda < 0) throw new ArgumentOutOfRangeException(nameof(lambda));
        var mass = PoissonMass(lambda);
        var sample = random.NextDouble();
        for (int i = 0; i < mass.Length; i++)
        {
            sample -= mass[i];
            if (sample <= 0) return i;
        }
        return mass.Length - 1;
    }

    private static double[] PoissonMass(double lambda)
    {
        // Cap the support before allocation; the omitted tail at this radius is negligible.
        // A domain limit prevents integer overflow or unreasonable arrays for corrupt xG input.
        if (!double.IsFinite(lambda) || lambda < 0 || lambda > 100_000)
            throw new ArgumentOutOfRangeException(nameof(lambda), "Poisson xG must be finite and at most 100,000.");
        int max = Math.Max(32, (int)Math.Ceiling(lambda + 12 * Math.Sqrt(lambda + 1) + 20));
        var p = new double[max + 1];
        if (lambda == 0) { p[0] = 1; return p; }
        int mode = (int)Math.Floor(lambda);
        p[mode] = 1;
        for (int i = mode - 1; i >= 0; i--) p[i] = p[i + 1] * (i + 1) / lambda;
        for (int i = mode + 1; i <= max; i++) p[i] = p[i - 1] * lambda / i;
        var sum = p.Sum();
        for (int i = 0; i < p.Length; i++) p[i] /= sum;
        return p;
    }

    private static void NormalizeObservedTacticEffects(LineupRatings r, MatchTacticalSide side)
    {
        if (!side.RatingsIncludeHistoricalTacticEffects || string.IsNullOrWhiteSpace(side.HistoricalTactic)) return;
        // Undo known sector multipliers before applying the requested future tactic.
        ApplyTacticRatingEffects(r, side.HistoricalTactic, true, inverse: true);
    }

    internal static void ApplyTacticRatingEffects(LineupRatings r, string tactic, bool apply, bool inverse = false)
    {
        if (!apply) return;
        double M(double factor) => inverse ? 1 / factor : factor;
        switch (tactic)
        {
            case "Counter": r.Midfield *= M(FormationData.TacticModifiers.CounterAttackMidfieldPenalty); break;
            case "AttackInMiddle":
                r.RightDefense *= M(FormationData.TacticModifiers.AIMSideAttackPenalty);
                r.LeftDefense *= M(FormationData.TacticModifiers.AIMSideAttackPenalty);
                break;
            case "AttackOnWings": r.CentralDefense *= M(FormationData.TacticModifiers.AOWCentralAttackPenalty); break;
            case "PlayCreatively":
                r.CentralDefense *= M(FormationData.TacticModifiers.CreativelyDefensePenalty);
                r.RightDefense *= M(FormationData.TacticModifiers.CreativelyDefensePenalty);
                r.LeftDefense *= M(FormationData.TacticModifiers.CreativelyDefensePenalty);
                break;
            case "LongShots":
                r.Midfield *= M(FormationData.TacticModifiers.LongShotsMidfieldPenalty);
                r.CentralAttack *= M(FormationData.TacticModifiers.LongShotsAttackPenalty);
                r.RightAttack *= M(FormationData.TacticModifiers.LongShotsAttackPenalty);
                r.LeftAttack *= M(FormationData.TacticModifiers.LongShotsAttackPenalty);
                break;
        }
        RecomputeOverall(r);
    }

    private static double RouteOpenPlay(double center, double right, double left, double setPiece, MatchTacticalSide side)
    {
        double centerWeight = 0.35, rightWeight = 0.25, leftWeight = 0.25;
        if (side.Tactic == "LongShots")
        {
            // Long Shots diverts up to 30% of open-play chances and leaves the fixed 15% set-piece share intact.
            const double longShotShare = 0.30;
            var openPlayWeight = centerWeight + rightWeight + leftWeight;
            var openPlayGoal = (centerWeight * center + rightWeight * right + leftWeight * left) / openPlayWeight;
            var convertedOpenPlay = (1 - longShotShare) * openPlayGoal + longShotShare * LongShotQuality(side);
            return openPlayWeight * convertedOpenPlay + 0.15 * setPiece;
        }
        if (side.Tactic == "AttackInMiddle")
        {
            var transfer = side.PassingSkillTotal.HasValue
                ? Math.Clamp(0.15 + Math.Max(0, FormationData.TacticModifiers.CalculateAOWAIMLevel((int)side.PassingSkillTotal.Value)) * 0.005, 0.15, 0.30)
                : 0.225;
            centerWeight += transfer / 2;
            rightWeight *= 1 - transfer;
            leftWeight *= 1 - transfer;
        }
        else if (side.Tactic == "AttackOnWings")
        {
            var transfer = side.PassingSkillTotal.HasValue
                ? Math.Clamp(0.20 + Math.Max(0, FormationData.TacticModifiers.CalculateAOWAIMLevel((int)side.PassingSkillTotal.Value)) * 0.005, 0.20, 0.40)
                : 0.30;
            centerWeight *= 1 - transfer;
            rightWeight += 0.5 * 0.35 * transfer;
            leftWeight += 0.5 * 0.35 * transfer;
        }
        return centerWeight * center + rightWeight * right + leftWeight * left + 0.15 * setPiece;
    }

    private static double LongShotQuality(MatchTacticalSide side) => side.LongShotGoalProbability is { } quality
        ? Math.Clamp(quality, 0, 1)
        : 0.15;
    private static double CounterQuality(MatchTacticalSide side) => side.CounterConversion is { } quality
        ? Math.Clamp(quality, 0, 1)
        : 0.20;
    private static double PressingSuppression(MatchTacticalSide side) => side.PressingSuppression is { } suppression
        ? Math.Clamp(suppression, 0, 0.5)
        : 0.20;

    private static double Share(double ownMid, double opponentMid)
    {
        var logOdds = MidfieldExponent * (Math.Log(Math.Max(0.01, ownMid)) - Math.Log(Math.Max(0.01, opponentMid)));
        return Logistic(logOdds);
    }

    private static double Finish(double attack, double defense)
    {
        var logOdds = FinishExponent * (Math.Log(Math.Max(0.01, attack)) - Math.Log(Math.Max(0.01, defense)));
        return Logistic(logOdds);
    }

    private static double Logistic(double value)
    {
        if (value >= 0) return 1 / (1 + Math.Exp(-Math.Min(value, 745)));
        var e = Math.Exp(Math.Max(value, -745));
        return e / (1 + e);
    }

    private static void RecomputeOverall(LineupRatings r) =>
        r.Overall = r.Midfield / 7 + r.CentralDefense / 7 + r.RightDefense / 7 + r.LeftDefense / 7 +
                    r.CentralAttack / 7 + r.RightAttack / 7 + r.LeftAttack / 7;

    private static void ValidateRatings(LineupRatings ratings, string parameterName)
    {
        var values = new[] { ratings.Midfield, ratings.CentralDefense, ratings.RightDefense, ratings.LeftDefense,
            ratings.CentralAttack, ratings.RightAttack, ratings.LeftAttack };
        if (values.Any(value => !double.IsFinite(value) || value < 0 || value > 1_000_000))
            throw new ArgumentException("Sector ratings must be finite, non-negative, and at most 1,000,000.", parameterName);
    }

    private static void ValidateSideInputs(MatchTacticalSide side, string parameterName)
    {
        var setPieces = new[] { side.IndirectSetPieceAttack, side.IndirectSetPieceDefense };
        if (setPieces.Any(value => !double.IsFinite(value) || value < 0 || value > 1_000_000) ||
            side.PassingSkillTotal is { } passing && (!double.IsFinite(passing) || passing < 0) ||
            side.CounterConversion is { } counter && (!double.IsFinite(counter) || counter < 0 || counter > 1) ||
            side.PressingSuppression is { } pressing && (!double.IsFinite(pressing) || pressing < 0 || pressing > 0.5) ||
            side.LongShotGoalProbability is { } longShot && (!double.IsFinite(longShot) || longShot < 0 || longShot > 1))
            throw new ArgumentException("Tactical side inputs must be finite and within their supported ranges.", parameterName);
    }

    private static LineupRatings CloneRatings(LineupRatings source) => new()
    {
        Midfield = source.Midfield, CentralDefense = source.CentralDefense,
        RightDefense = source.RightDefense, LeftDefense = source.LeftDefense,
        CentralAttack = source.CentralAttack, RightAttack = source.RightAttack,
        LeftAttack = source.LeftAttack, Overall = source.Overall
    };
}

public sealed class MatchTacticalSide
{
    public LineupRatings Ratings { get; init; } = new();
    public string Tactic { get; init; } = "Normal";
    public bool IsHome { get; init; }
    public bool HomeAdvantageAlreadyApplied { get; init; }
    public bool RatingsIncludeHistoricalTacticEffects { get; init; }
    public bool ApplyCurrentTacticRatingEffects { get; init; } = true;
    public string? HistoricalTactic { get; init; }
    public double IndirectSetPieceAttack { get; init; }
    public double IndirectSetPieceDefense { get; init; }
    public double? PassingSkillTotal { get; init; }
    public double? CounterConversion { get; init; }
    public double? PressingSuppression { get; init; }
    public double? LongShotGoalProbability { get; init; }
}

public sealed record TacticalMatchPrediction(LineupRatings HomeRatings, LineupRatings AwayRatings, MatchProbability Probabilities);
public sealed record MatchProbability(double WinProbability, double DrawProbability, double LossProbability,
    double ExpectedGoalsFor, double ExpectedGoalsAgainst)
{
    public double ExpectedPoints => 3 * WinProbability + DrawProbability;
}

public sealed record OptimizerEvaluation(LineupRatings Ratings, double ExpectedGoalsFor, double ExpectedGoalsAgainst,
    double WinProbability, double DrawProbability, double LossProbability, double ExpectedPoints, double DisorderRisk);
