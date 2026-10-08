using System.Globalization;
using System.Xml.Linq;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>
/// Harness kalibracyjny: porownuje oceny sektorowe przewidziane przez RatingEngine
/// z PRAWDZIWYMI ocenami z matchdetails dla rozegranych meczow wlasnej druzyny.
/// Sluzy do dopasowania stalych silnika (RatingScale, wspolczynniki XP itd.).
///
/// </summary>
public class CalibrationService
{
    private readonly HattrickApiService _api;
    private readonly ILogger<CalibrationService> _logger;
    private readonly CalibrationSnapshotStore _snapshots;
    private readonly RatingEngine _ratingEngine;

    public CalibrationService(HattrickApiService api, ILogger<CalibrationService> logger,
        CalibrationSnapshotStore snapshots, RatingEngine ratingEngine)
    {
        _api = api;
        _logger = logger;
        _snapshots = snapshots;
        _ratingEngine = ratingEngine;
    }

    public async Task<CalibrationReport> CompareOwnMatchesAsync(int teamId, int count)
    {
        count = Math.Clamp(count, 1, 10);

        var roster = await _api.GetTeamPlayersAsync(teamId);
        await _snapshots.SaveAsync(new CalibrationSnapshot
        {
            TeamId = teamId,
            RecordedAt = DateTimeOffset.UtcNow,
            Players = roster,
            Source = "CHPP own roster",
            Warnings = new() { "Match context not supplied; this snapshot is not eligible for calibration." }
        });
        var availableSnapshots = await _snapshots.GetAsync(teamId);

        // Ostatnie rozegrane mecze seniorskie.
        var matchesDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matches" }, { "teamId", teamId.ToString() }, { "version", "2.8" }
        }, $"matches teamId={teamId}");

        var playedMatches = matchesDoc.Descendants("Match")
            .Where(m =>
            {
                var status = m.Element("Status")?.Value ?? "";
                var matchType = ParseIntOrZero(m, "MatchType");
                return status.Equals("FINISHED", StringComparison.OrdinalIgnoreCase)
                    && matchType >= 1 && matchType <= 12;
            })
            .OrderByDescending(m => DateTime.TryParse(m.Element("MatchDate")?.Value, out var d) ? d : DateTime.MinValue)
            .Take(count)
            .ToList();

        var report = new CalibrationReport { TeamId = teamId };

        foreach (var match in playedMatches)
        {
            var matchId = match.Element("MatchID")?.Value;
            if (string.IsNullOrEmpty(matchId)) continue;

            var kickoff = ParseDate(match.Element("MatchDate")?.Value);
            var snapshot = long.TryParse(matchId, out var numericMatchId) && kickoff.HasValue
                ? availableSnapshots.Where(s => s.Context.MatchId == numericMatchId
                        && s.RecordedAt <= kickoff.Value && s.Context.MatchDate <= kickoff
                        && s.Context.IsComplete && s.Context.BehavioursBySlot.Count == 11
                        && s.Context.PlayerIdsBySlot.Values.Distinct().Count() == 11
                        && s.Context.PlayerIdsBySlot.Values.All(id => s.Players.Any(p => p.PlayerId == id && p.SkillsAvailable)))
                    .OrderByDescending(s => s.RecordedAt).FirstOrDefault()
                : null;
            if (snapshot == null)
            {
                report.ExcludedMatches++;
                report.ExclusionReasons.Add($"Match {matchId}: no complete roster and match-context snapshot captured by kickoff.");
                continue;
            }
            try
            {
                var entry = await CompareMatchAsync(teamId, matchId, snapshot);
                if (entry == null)
                {
                    report.ExcludedMatches++;
                    report.ExclusionReasons.Add($"Match {matchId}: snapshot lineup/context did not match the observed lineup.");
                }
                else report.Matches.Add(entry);
            }
            catch (Exception ex)
            {
                report.ExcludedMatches++;
                report.ExclusionReasons.Add($"Match {matchId}: replay failed ({ex.Message}).");
                _logger.LogWarning(ex, "Calibration replay skipped match {MatchId}", matchId);
            }
        }

        ComputeAggregates(report);
        report.EvaluationStatus = report.Matches.Count == 0 ? "No evaluable snapshots"
            : report.Matches.Count < 2 ? "Evaluated without chronological holdout (one match)"
            : "Evaluated with chronological holdout";
        return report;
    }

    private async Task<CalibrationMatchEntry?> CompareMatchAsync(int teamId, string matchId, CalibrationSnapshot snapshot)
    {
        // matchdetails: prawdziwe oceny + taktyka + dom/wyjazd.
        var detailsDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matchdetails" }, { "matchId", matchId }, { "version", "3.0" }
        }, $"matchdetails matchId={matchId}");

        var homeTeam = detailsDoc.Descendants("HomeTeam").FirstOrDefault();
        var awayTeam = detailsDoc.Descendants("AwayTeam").FirstOrDefault();
        if (!int.TryParse(homeTeam?.Element("HomeTeamID")?.Value, out var homeTeamId)) return null;
        bool isHome = homeTeamId == teamId;
        if (snapshot.Context.IsHomeMatch != isHome) return null;
        var myElement = isHome ? homeTeam : awayTeam;
        if (myElement == null) return null;

        if (!TryParseRatings(myElement, out var actualRatings)) return null;
        var actual = new LineupRatings
        {
            Midfield = actualRatings.MidfieldRating, RightDefense = actualRatings.RightDefenseRating,
            CentralDefense = actualRatings.CentralDefenseRating, LeftDefense = actualRatings.LeftDefenseRating,
            RightAttack = actualRatings.RightAttackRating, CentralAttack = actualRatings.CentralAttackRating,
            LeftAttack = actualRatings.LeftAttackRating
        };
        if (actual.Midfield <= 0) return null; // brak ocen (np. walkower)

        if (!int.TryParse(myElement.Element("TacticType")?.Value, out var tacticCode)) return null;
        var observedTactic = MapTacticCode(tacticCode);
        if (string.IsNullOrEmpty(observedTactic)) return null;
        var tactic = snapshot.Context.Tactic!;
        if (!string.Equals(tactic, observedTactic, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(myElement.Element("Formation")?.Value, snapshot.Context.Formation, StringComparison.Ordinal)) return null;
        var attitudeText = myElement.Element("TeamAttitude")?.Value ?? myElement.Element("Attitude")?.Value;
        if (!int.TryParse(attitudeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attitudeCode)
            || MapAttitudeCode(attitudeCode) != snapshot.Context.Attitude) return null;
        var weatherText = detailsDoc.Descendants("WeatherID").FirstOrDefault()?.Value;
        if (!int.TryParse(weatherText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var observedWeather)
            || observedWeather != snapshot.Context.WeatherId) return null;
        var players = snapshot.Players.ToDictionary(p => p.PlayerId);

        // matchlineup: faktyczne pozycje i zachowania.
        var lineupDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matchlineup" }, { "matchId", matchId }, { "teamId", teamId.ToString() }, { "version", "2.1" }
        }, $"matchlineup matchId={matchId}");

        var teamElement = lineupDoc.Descendants("Team")
            .FirstOrDefault(t => t.Element("TeamID")?.Value == teamId.ToString());
        var lineupContainer = teamElement?.Element("Lineup") ?? teamElement?.Element("StartingLineup");
        if (lineupContainer == null) return null;

        var lineup = new Lineup { Formation = snapshot.Context.Formation, TacticType = snapshot.Context.Tactic! };

        foreach (var playerEl in lineupContainer.Elements("Player"))
        {
            if (!int.TryParse(playerEl.Element("PlayerID")?.Value, out int pid)) continue;
            if (!int.TryParse(playerEl.Element("RoleID")?.Value, out var roleId)) continue;
            if (roleId < 100 || roleId > 113) continue; // tylko podstawowa 11

            var slot = MapRoleIdToSlot(roleId);
            if (string.IsNullOrEmpty(slot)) continue;
            if (!snapshot.Context.PlayerIdsBySlot.TryGetValue(slot, out var expectedPlayerId) || expectedPlayerId != pid) return null;
            if (!players.TryGetValue(pid, out var player) || !player.SkillsAvailable) return null;

            if (!int.TryParse(playerEl.Element("Behaviour")?.Value, out var behaviourCode)
                || !IsValidBehaviourCode(slot, behaviourCode)) return null;
            var behaviour = MapSlotBehaviour(slot, behaviourCode);
            if (!snapshot.Context.BehavioursBySlot.TryGetValue(slot, out var expectedBehaviour)
                || !string.Equals(expectedBehaviour, behaviour, StringComparison.OrdinalIgnoreCase)) return null;
            lineup.Positions[slot] = new LineupPosition { Position = slot, Player = player, Behavior = behaviour };
        }

        if (lineup.Positions.Count != 11 || !lineup.Positions.ContainsKey("GK")
            || lineup.Positions.Values.Select(s => s.Player!.PlayerId).Distinct().Count() != 11)
        {
            // Za duzo brakujacych graczy (sprzedani) — porownanie byloby zaklamane.
            return null;
        }

        var context = snapshot.Context;
        var disorderRisk = AdvancedLineupOptimizer.ComputeDisorderRisk(context.FormationExperience!.Value);
        var predicted = _ratingEngine.ComputeContextualRatings(lineup, context.WeatherId!.Value,
            context.Attitude!, context.CoachType!, disorderRisk, context.TeamSpiritLevel,
            context.ConfidenceLevel, applyHomeAdvantage: isHome);
        _ratingEngine.ApplyTactic(predicted, tactic);

        return new CalibrationMatchEntry
        {
            MatchId = long.Parse(matchId),
            MatchDate = ParseDate(detailsDoc.Descendants("MatchDate").FirstOrDefault()?.Value),
            IsHomeMatch = isHome,
            Tactic = tactic,
            PlayersMatched = lineup.Positions.Count,
            Predicted = predicted,
            Actual = actual
        };
    }

    private static void ComputeAggregates(CalibrationReport report)
    {
        if (report.Matches.Count == 0) return;
        var ordered = report.Matches.OrderBy(m => m.MatchDate).ToList();
        var holdoutCount = ordered.Count > 1 ? Math.Max(1, (int)Math.Ceiling(ordered.Count * 0.2)) : 0;
        var training = ordered.Take(ordered.Count - holdoutCount).ToList();
        var heldOut = ordered.Skip(ordered.Count - holdoutCount).ToList();
        report.TrainingSampleCount = training.Count;
        report.HeldOutSampleCount = heldOut.Count;
        report.TrainingSectorSampleCounts = SectorCounts(training.Count);
        report.HeldOutSectorSampleCounts = SectorCounts(heldOut.Count);
        if (training.Count == 0) return;
        report.MeanAbsoluteError = new LineupRatings
        {
            Midfield = training.Average(m => Math.Abs(m.Predicted.Midfield - m.Actual.Midfield)),
            CentralDefense = training.Average(m => Math.Abs(m.Predicted.CentralDefense - m.Actual.CentralDefense)),
            RightDefense = training.Average(m => Math.Abs(m.Predicted.RightDefense - m.Actual.RightDefense)),
            LeftDefense = training.Average(m => Math.Abs(m.Predicted.LeftDefense - m.Actual.LeftDefense)),
            CentralAttack = training.Average(m => Math.Abs(m.Predicted.CentralAttack - m.Actual.CentralAttack)),
            RightAttack = training.Average(m => Math.Abs(m.Predicted.RightAttack - m.Actual.RightAttack)),
            LeftAttack = training.Average(m => Math.Abs(m.Predicted.LeftAttack - m.Actual.LeftAttack))
        };
        report.MeanBias = ErrorBias(training);
        if (heldOut.Count > 0)
        {
            report.HeldOutMeanAbsoluteError = new LineupRatings
            {
                Midfield = heldOut.Average(m => Math.Abs(m.Predicted.Midfield - m.Actual.Midfield)),
                CentralDefense = heldOut.Average(m => Math.Abs(m.Predicted.CentralDefense - m.Actual.CentralDefense)),
                RightDefense = heldOut.Average(m => Math.Abs(m.Predicted.RightDefense - m.Actual.RightDefense)),
                LeftDefense = heldOut.Average(m => Math.Abs(m.Predicted.LeftDefense - m.Actual.LeftDefense)),
                CentralAttack = heldOut.Average(m => Math.Abs(m.Predicted.CentralAttack - m.Actual.CentralAttack)),
                RightAttack = heldOut.Average(m => Math.Abs(m.Predicted.RightAttack - m.Actual.RightAttack)),
                LeftAttack = heldOut.Average(m => Math.Abs(m.Predicted.LeftAttack - m.Actual.LeftAttack))
            };
            report.HeldOutMeanBias = ErrorBias(heldOut);
        }
        // Sredni stosunek actual/predicted per sektor — bezposrednia wskazowka dla RatingScale.K.
        report.MeanActualToPredictedRatio = new LineupRatings
        {
            Midfield = SafeRatio(training.Select(m => (m.Actual.Midfield, m.Predicted.Midfield))),
            CentralDefense = SafeRatio(training.Select(m => (m.Actual.CentralDefense, m.Predicted.CentralDefense))),
            RightDefense = SafeRatio(training.Select(m => (m.Actual.RightDefense, m.Predicted.RightDefense))),
            LeftDefense = SafeRatio(training.Select(m => (m.Actual.LeftDefense, m.Predicted.LeftDefense))),
            CentralAttack = SafeRatio(training.Select(m => (m.Actual.CentralAttack, m.Predicted.CentralAttack))),
            RightAttack = SafeRatio(training.Select(m => (m.Actual.RightAttack, m.Predicted.RightAttack))),
            LeftAttack = SafeRatio(training.Select(m => (m.Actual.LeftAttack, m.Predicted.LeftAttack)))
        };
    }

    private static LineupRatings ErrorBias(IEnumerable<CalibrationMatchEntry> matches)
    {
        var items = matches.ToList();
        return new LineupRatings
        {
            Midfield = items.Average(m => m.Predicted.Midfield - m.Actual.Midfield),
            CentralDefense = items.Average(m => m.Predicted.CentralDefense - m.Actual.CentralDefense),
            RightDefense = items.Average(m => m.Predicted.RightDefense - m.Actual.RightDefense),
            LeftDefense = items.Average(m => m.Predicted.LeftDefense - m.Actual.LeftDefense),
            CentralAttack = items.Average(m => m.Predicted.CentralAttack - m.Actual.CentralAttack),
            RightAttack = items.Average(m => m.Predicted.RightAttack - m.Actual.RightAttack),
            LeftAttack = items.Average(m => m.Predicted.LeftAttack - m.Actual.LeftAttack)
        };
    }

    private static Dictionary<string, int> SectorCounts(int count) => new()
    {
        ["midfield"] = count, ["rightDefense"] = count, ["centralDefense"] = count,
        ["leftDefense"] = count, ["rightAttack"] = count, ["centralAttack"] = count, ["leftAttack"] = count
    };

    private static double SafeRatio(IEnumerable<(double Actual, double Predicted)> pairs)
    {
        var valid = pairs.Where(p => p.Predicted > 0.01).ToList();
        return valid.Count > 0 ? valid.Average(p => p.Actual / p.Predicted) : 0;
    }

    private static int ParseIntOrZero(XElement parent, string name) =>
        int.TryParse(parent.Element(name)?.Value, out var v) ? v : 0;

    private static bool TryParseRatings(XElement element, out TeamRatings ratings)
    {
        ratings = new TeamRatings();
        var fields = new[] { "RatingMidfield", "RatingRightDef", "RatingMidDef", "RatingLeftDef", "RatingRightAtt", "RatingMidAtt", "RatingLeftAtt", "RatingIndirectSetPiecesAtt", "RatingIndirectSetPiecesDef" };
        var values = new int[fields.Length];
        for (var i = 0; i < fields.Length; i++)
            if (!int.TryParse(element.Element(fields[i])?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)
                return false;
        ratings = new TeamRatings
        {
            MidfieldRating = values[0], RightDefenseRating = values[1], CentralDefenseRating = values[2],
            LeftDefenseRating = values[3], RightAttackRating = values[4], CentralAttackRating = values[5],
            LeftAttackRating = values[6], IndirectSetPiecesAttRating = values[7], IndirectSetPiecesDefRating = values[8]
        };
        return true;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;

    internal static string MapTacticCode(int code) => code switch
    {
        1 => "Pressing",
        2 => "Counter",
        3 => "AttackInMiddle",
        4 => "AttackOnWings",
        7 => "PlayCreatively",
        8 => "LongShots",
        0 => "Normal",
        _ => ""
    };

    private static string MapAttitudeCode(int code) => code switch
    {
        -1 => "PIC", 0 => "Normal", 1 => "MOTS", _ => ""
    };

    internal static string MapRoleIdToSlot(int roleId) => roleId switch
    {
        100 => "GK",
        101 => "RWB", 102 => "RCD", 103 => "CD", 104 => "LCD", 105 => "LWB",
        106 => "RW", 107 => "RIM", 108 => "IM", 109 => "LIM", 110 => "LW",
        111 => "RFW", 112 => "FW", 113 => "LFW",
        _ => ""
    };

    /// <summary>
    /// Mapowanie (slot, kod zachowania CHPP) -> klucz tabeli wkladow.
    /// Kody CHPP: 0=normalne, 1=ofensywne, 2=defensywne, 3=do srodka, 4=na skrzydlo.
    /// </summary>
    internal static string MapSlotBehaviour(string slot, int behaviourCode)
    {
        return (slot, behaviourCode) switch
        {
            ("RWB" or "LWB", 1) => "WBO",
            ("RWB" or "LWB", 2) => "WBD",
            ("RWB" or "LWB", 3) => "WBTM",
            ("RCD" or "LCD" or "CD", 1) => "CDO",
            ("RCD" or "LCD", 4) => "CDTW",
            ("RW" or "LW", 1) => "WO",
            ("RW" or "LW", 2) => "WD",
            ("RW" or "LW", 3) => "WTM",
            ("RIM" or "LIM" or "IM", 1) => "IMO",
            ("RIM" or "LIM" or "IM", 2) => "IMD",
            ("RIM" or "LIM", 4) => "IMTW",
            ("RFW" or "LFW" or "FW", 2) => "DF",
            ("RFW" or "LFW" or "FW", 4) => "FTW",
            _ => slot
        };
    }

    internal static bool IsValidBehaviourCode(string slot, int code) => (slot, code) switch
    {
        ("GK", 0) => true,
        ("RWB" or "LWB", 0 or 1 or 2 or 3) => true,
        ("RCD" or "CD" or "LCD", 0 or 1) => true,
        ("RCD" or "LCD", 4) => true,
        ("RW" or "LW", 0 or 1 or 2 or 3) => true,
        ("RIM" or "IM" or "LIM", 0 or 1 or 2) => true,
        ("RIM" or "LIM", 4) => true,
        ("RFW" or "FW" or "LFW", 0 or 2 or 4) => true,
        _ => false
    };

    public static bool IsValidSlotBehaviour(string slot, string behaviour)
    {
        var code = (slot, behaviour) switch
        {
            ("GK", "GK") => 0,
            ("RWB", "RWB") or ("LWB", "LWB") or ("RCD", "RCD") or ("CD", "CD") or ("LCD", "LCD")
                or ("RW", "RW") or ("LW", "LW") or ("RIM", "RIM") or ("IM", "IM") or ("LIM", "LIM")
                or ("RFW", "RFW") or ("FW", "FW") or ("LFW", "LFW") => 0,
            (_, "WBO" or "CDO" or "WO" or "IMO") => 1,
            (_, "WBD" or "WD" or "IMD" or "DF") => 2,
            (_, "WBTM" or "WTM") => 3,
            (_, "CDTW" or "FTW" or "IMTW") => 4,
            _ => -1
        };
        return code >= 0 && IsValidBehaviourCode(slot, code);
    }
}

public class CalibrationReport
{
    public int TeamId { get; set; }
    public List<CalibrationMatchEntry> Matches { get; set; } = new();
    public string EvaluationStatus { get; set; } = "Not evaluated";
    public int ExcludedMatches { get; set; }
    public int TrainingSampleCount { get; set; }
    public int HeldOutSampleCount { get; set; }
    public LineupRatings? MeanBias { get; set; }
    public LineupRatings? HeldOutMeanAbsoluteError { get; set; }
    public LineupRatings? HeldOutMeanBias { get; set; }
    public Dictionary<string, int> TrainingSectorSampleCounts { get; set; } = new();
    public Dictionary<string, int> HeldOutSectorSampleCounts { get; set; } = new();
    public List<string> ExclusionReasons { get; set; } = new();
    // Sredni blad bezwzgledny per sektor (cel: <= ~2 punkty denominacji HT).
    public LineupRatings? MeanAbsoluteError { get; set; }
    // Sredni actual/predicted per sektor — gdy stabilnie != 1, skoryguj RatingScale.
    public LineupRatings? MeanActualToPredictedRatio { get; set; }
}

public class CalibrationMatchEntry
{
    public long MatchId { get; set; }
    public DateTime? MatchDate { get; set; }
    public bool IsHomeMatch { get; set; }
    public string Tactic { get; set; } = "Normal";
    public int PlayersMatched { get; set; }
    public LineupRatings Predicted { get; set; } = new();
    public LineupRatings Actual { get; set; } = new();
}
