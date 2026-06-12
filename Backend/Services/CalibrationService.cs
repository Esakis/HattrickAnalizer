using System.Globalization;
using System.Xml.Linq;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>
/// Harness kalibracyjny: porownuje oceny sektorowe przewidziane przez RatingEngine
/// z PRAWDZIWYMI ocenami z matchdetails dla rozegranych meczow wlasnej druzyny.
/// Sluzy do dopasowania stalych silnika (RatingScale, wspolczynniki XP itd.).
///
/// Ograniczenie: uzywa BIEZACYCH umiejetnosci graczy, nie historycznych —
/// dla meczow z ostatnich tygodni przyblizenie jest akceptowalne.
/// </summary>
public class CalibrationService
{
    private readonly HattrickApiService _api;
    private readonly ILogger<CalibrationService> _logger;
    private readonly RatingEngine _engine = new();

    public CalibrationService(HattrickApiService api, ILogger<CalibrationService> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<CalibrationReport> CompareOwnMatchesAsync(int teamId, int count)
    {
        count = Math.Clamp(count, 1, 10);

        // Biezace umiejetnosci graczy (players file).
        var playersDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "players" }, { "teamId", teamId.ToString() }, { "version", "2.8" }
        }, $"players teamId={teamId}");
        var players = playersDoc.Descendants("Player")
            .Select(_api.ParsePlayer)
            .ToDictionary(p => p.PlayerId);

        // Ostatnie rozegrane mecze seniorskie.
        var matchesDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matches" }, { "teamId", teamId.ToString() }, { "version", "2.8" }
        }, $"matches teamId={teamId}");

        var playedMatches = matchesDoc.Descendants("Match")
            .Where(m =>
            {
                var status = m.Element("Status")?.Value ?? "";
                var matchType = int.Parse(m.Element("MatchType")?.Value ?? "0");
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

            try
            {
                var entry = await CompareMatchAsync(teamId, matchId, players);
                if (entry != null) report.Matches.Add(entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kalibracja: pominieto mecz {MatchId}", matchId);
            }
        }

        ComputeAggregates(report);
        return report;
    }

    private async Task<CalibrationMatchEntry?> CompareMatchAsync(int teamId, string matchId, Dictionary<int, Player> players)
    {
        // matchdetails: prawdziwe oceny + taktyka + dom/wyjazd.
        var detailsDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matchdetails" }, { "matchId", matchId }, { "version", "3.0" }
        }, $"matchdetails matchId={matchId}");

        var homeTeam = detailsDoc.Descendants("HomeTeam").FirstOrDefault();
        var awayTeam = detailsDoc.Descendants("AwayTeam").FirstOrDefault();
        var homeTeamId = int.Parse(homeTeam?.Element("HomeTeamID")?.Value ?? "0");
        bool isHome = homeTeamId == teamId;
        var myElement = isHome ? homeTeam : awayTeam;
        if (myElement == null) return null;

        var actual = new LineupRatings
        {
            Midfield = ParseIntOrZero(myElement, "RatingMidfield"),
            RightDefense = ParseIntOrZero(myElement, "RatingRightDef"),
            CentralDefense = ParseIntOrZero(myElement, "RatingMidDef"),
            LeftDefense = ParseIntOrZero(myElement, "RatingLeftDef"),
            RightAttack = ParseIntOrZero(myElement, "RatingRightAtt"),
            CentralAttack = ParseIntOrZero(myElement, "RatingMidAtt"),
            LeftAttack = ParseIntOrZero(myElement, "RatingLeftAtt")
        };
        if (actual.Midfield <= 0) return null; // brak ocen (np. walkower)

        var tacticCode = int.Parse(myElement.Element("TacticType")?.Value ?? "0");
        var tactic = MapTacticCode(tacticCode);

        // matchlineup: faktyczne pozycje i zachowania.
        var lineupDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matchlineup" }, { "matchId", matchId }, { "teamId", teamId.ToString() }, { "version", "2.1" }
        }, $"matchlineup matchId={matchId}");

        var teamElement = lineupDoc.Descendants("Team")
            .FirstOrDefault(t => t.Element("TeamID")?.Value == teamId.ToString());
        var lineupContainer = teamElement?.Element("Lineup") ?? teamElement?.Element("StartingLineup");
        if (lineupContainer == null) return null;

        var assigned = new AssignedLineup
        {
            Formation = FormationData.Formations["4-4-2"], // formacja nieistotna dla ComputeRatings
            Slots = new Dictionary<string, AssignedSlot>()
        };

        foreach (var playerEl in lineupContainer.Elements("Player"))
        {
            if (!int.TryParse(playerEl.Element("PlayerID")?.Value, out int pid)) continue;
            var roleId = int.Parse(playerEl.Element("RoleID")?.Value ?? "0");
            if (roleId < 100 || roleId > 113) continue; // tylko podstawowa 11

            var slot = MapRoleIdToSlot(roleId);
            if (string.IsNullOrEmpty(slot)) continue;
            if (!players.TryGetValue(pid, out var player)) continue; // sprzedany — brak skilli

            var behaviourCode = int.Parse(playerEl.Element("Behaviour")?.Value ?? "0");
            assigned.Slots[slot] = new AssignedSlot
            {
                SlotId = slot,
                Player = player,
                Behaviour = MapSlotBehaviour(slot, behaviourCode)
            };
        }

        if (assigned.Slots.Count < 9)
        {
            // Za duzo brakujacych graczy (sprzedani) — porownanie byloby zaklamane.
            return null;
        }

        var predicted = _engine.ComputeRatings(assigned);
        _engine.ApplyTactic(predicted, tactic);
        // Postawa (PIC/MOTS) nie jest publiczna — zakladamy Normal.
        _engine.ApplyHomeAdvantage(predicted, isHome);

        return new CalibrationMatchEntry
        {
            MatchId = long.Parse(matchId),
            MatchDate = ParseDate(detailsDoc.Descendants("MatchDate").FirstOrDefault()?.Value),
            IsHomeMatch = isHome,
            Tactic = tactic,
            PlayersMatched = assigned.Slots.Count,
            Predicted = predicted,
            Actual = actual
        };
    }

    private static void ComputeAggregates(CalibrationReport report)
    {
        if (report.Matches.Count == 0) return;
        report.MeanAbsoluteError = new LineupRatings
        {
            Midfield = report.Matches.Average(m => Math.Abs(m.Predicted.Midfield - m.Actual.Midfield)),
            CentralDefense = report.Matches.Average(m => Math.Abs(m.Predicted.CentralDefense - m.Actual.CentralDefense)),
            RightDefense = report.Matches.Average(m => Math.Abs(m.Predicted.RightDefense - m.Actual.RightDefense)),
            LeftDefense = report.Matches.Average(m => Math.Abs(m.Predicted.LeftDefense - m.Actual.LeftDefense)),
            CentralAttack = report.Matches.Average(m => Math.Abs(m.Predicted.CentralAttack - m.Actual.CentralAttack)),
            RightAttack = report.Matches.Average(m => Math.Abs(m.Predicted.RightAttack - m.Actual.RightAttack)),
            LeftAttack = report.Matches.Average(m => Math.Abs(m.Predicted.LeftAttack - m.Actual.LeftAttack))
        };
        // Sredni stosunek actual/predicted per sektor — bezposrednia wskazowka dla RatingScale.K.
        report.MeanActualToPredictedRatio = new LineupRatings
        {
            Midfield = SafeRatio(report.Matches.Select(m => (m.Actual.Midfield, m.Predicted.Midfield))),
            CentralDefense = SafeRatio(report.Matches.Select(m => (m.Actual.CentralDefense, m.Predicted.CentralDefense))),
            RightDefense = SafeRatio(report.Matches.Select(m => (m.Actual.RightDefense, m.Predicted.RightDefense))),
            LeftDefense = SafeRatio(report.Matches.Select(m => (m.Actual.LeftDefense, m.Predicted.LeftDefense))),
            CentralAttack = SafeRatio(report.Matches.Select(m => (m.Actual.CentralAttack, m.Predicted.CentralAttack))),
            RightAttack = SafeRatio(report.Matches.Select(m => (m.Actual.RightAttack, m.Predicted.RightAttack))),
            LeftAttack = SafeRatio(report.Matches.Select(m => (m.Actual.LeftAttack, m.Predicted.LeftAttack)))
        };
    }

    private static double SafeRatio(IEnumerable<(double Actual, double Predicted)> pairs)
    {
        var valid = pairs.Where(p => p.Predicted > 0.01).ToList();
        return valid.Count > 0 ? valid.Average(p => p.Actual / p.Predicted) : 0;
    }

    private static int ParseIntOrZero(XElement parent, string name) =>
        int.TryParse(parent.Element(name)?.Value, out var v) ? v : 0;

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;

    private static string MapTacticCode(int code) => code switch
    {
        1 => "Pressing",
        2 => "Counter",
        3 => "AttackInMiddle",
        4 => "AttackOnWings",
        7 => "PlayCreatively",
        8 => "LongShots",
        _ => "Normal"
    };

    private static string MapRoleIdToSlot(int roleId) => roleId switch
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
}

public class CalibrationReport
{
    public int TeamId { get; set; }
    public List<CalibrationMatchEntry> Matches { get; set; } = new();
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
