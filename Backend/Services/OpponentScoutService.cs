using System.Collections.Concurrent;
using System.Globalization;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>
/// Skaut przeciwnika: agreguje ostatnie rozegrane mecze druzyny (matchdetails + matchlineup)
/// w raport — najczestsza formacja/taktyka, oceny sektorowe wazone swiezoscia,
/// przewidywana podstawowa jedenastka.
///
/// Dane sa publiczne (CHPP), wiec cache jest wspolny dla wszystkich sesji.
/// TTL chroni CHPP przed lawina zapytan przy kazdym przeliczeniu optymalizatora.
/// </summary>
public class OpponentScoutService
{
    private const int DefaultMatchCount = 5;
    // Waga swiezosci: najnowszy mecz 1.0, kazdy starszy x0.85.
    private const double RecencyDecay = 0.85;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (DateTime At, OpponentScoutReport Report)> Cache = new();

    private readonly HattrickApiService _api;
    private readonly ILogger<OpponentScoutService> _logger;

    public OpponentScoutService(HattrickApiService api, ILogger<OpponentScoutService> logger)
    {
        _api = api;
        _logger = logger;
    }

    // includeLineups=false pomija matchlineup (bez przewidywanej XI) — o polowe mniej
    // zapytan CHPP; uzywane przy skanowaniu calej ligi przez symulator tabeli.
    public async Task<OpponentScoutReport> GetScoutReportAsync(int teamId, int count = DefaultMatchCount, bool includeLineups = true)
    {
        count = Math.Clamp(count, 2, 10);
        var cacheKey = $"{teamId}:{count}:{includeLineups}";
        if (Cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
        {
            return cached.Report;
        }

        var report = await BuildReportAsync(teamId, count, includeLineups);
        Cache[cacheKey] = (DateTime.UtcNow, report);
        return report;
    }

    /// <summary>
    /// Oceny przeciwnika do predykcji: srednia wazona z ostatnich meczow zamiast
    /// pojedynczego ostatniego meczu. Fallback do dotychczasowego zrodla, gdy
    /// przeciwnik ma mniej niz 2 rozegrane mecze albo skauting sie nie powiedzie.
    /// </summary>
    public async Task<OpponentRatingsResult> GetWeightedRatingsAsync(int teamId)
    {
        if (_api.MockMode)
        {
            return await _api.GetOpponentRatingsAsync(teamId);
        }

        try
        {
            var report = await GetScoutReportAsync(teamId);
            if (report.MatchesAnalyzed >= 2)
            {
                return new OpponentRatingsResult
                {
                    Ratings = report.WeightedRatings,
                    Source = "scout",
                    SourceMatchDate = report.Matches.FirstOrDefault()?.MatchDate
                };
            }
        }
        catch (ChppApiException ex)
        {
            _logger.LogWarning(ex, "Skauting przeciwnika {TeamId} nie powiodl sie — fallback do ostatniego meczu.", teamId);
        }

        return await _api.GetOpponentRatingsAsync(teamId);
    }

    private async Task<OpponentScoutReport> BuildReportAsync(int teamId, int count, bool includeLineups = true)
    {
        var matchesDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matches" }, { "teamId", teamId.ToString() }, { "version", "2.8" }
        }, $"matches teamId={teamId}");

        var played = matchesDoc.Descendants("Match")
            .Where(m =>
            {
                var status = m.Element("Status")?.Value ?? "";
                var matchType = int.Parse(m.Element("MatchType")?.Value ?? "0");
                return status.Equals("FINISHED", StringComparison.OrdinalIgnoreCase)
                    && matchType >= 1 && matchType <= 12;
            })
            .OrderByDescending(m => ParseDate(m.Element("MatchDate")?.Value) ?? DateTime.MinValue)
            .Take(count)
            .ToList();

        var report = new OpponentScoutReport { TeamId = teamId };
        // slot -> (playerId -> wystapienia); do przewidywanej jedenastki.
        var slotAppearances = new Dictionary<string, Dictionary<int, ScoutStarterAggregate>>();

        foreach (var match in played)
        {
            var matchId = match.Element("MatchID")?.Value;
            if (string.IsNullOrEmpty(matchId)) continue;

            try
            {
                var entry = await ScoutMatchAsync(teamId, matchId, match, slotAppearances, includeLineups);
                if (entry != null) report.Matches.Add(entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skaut: pominieto mecz {MatchId} druzyny {TeamId}", matchId, teamId);
            }
        }

        report.MatchesAnalyzed = report.Matches.Count;
        if (report.MatchesAnalyzed == 0) return report;

        report.FormationCounts = report.Matches
            .Where(m => !string.IsNullOrEmpty(m.Formation))
            .GroupBy(m => m.Formation)
            .ToDictionary(g => g.Key, g => g.Count());
        report.MostCommonFormation = report.FormationCounts
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => kvp.Key)
            .FirstOrDefault() ?? "";

        report.TacticCounts = report.Matches
            .GroupBy(m => m.Tactic)
            .ToDictionary(g => g.Key, g => g.Count());
        report.MostCommonTactic = report.TacticCounts
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => kvp.Key)
            .FirstOrDefault() ?? "Normal";

        report.WeightedRatings = ComputeWeightedRatings(report.Matches);
        report.LikelyStarters = BuildLikelyStarters(slotAppearances);
        return report;
    }

    private async Task<ScoutMatchSummary?> ScoutMatchAsync(
        int teamId, string matchId, System.Xml.Linq.XElement matchElement,
        Dictionary<string, Dictionary<int, ScoutStarterAggregate>> slotAppearances,
        bool includeLineups)
    {
        var detailsDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matchdetails" }, { "matchId", matchId }, { "version", "3.0" }
        }, $"matchdetails matchId={matchId}");

        var homeTeam = detailsDoc.Descendants("HomeTeam").FirstOrDefault();
        var awayTeam = detailsDoc.Descendants("AwayTeam").FirstOrDefault();
        var homeTeamId = int.Parse(homeTeam?.Element("HomeTeamID")?.Value ?? "0");
        bool isHome = homeTeamId == teamId;
        var myElement = isHome ? homeTeam : awayTeam;
        var oppElement = isHome ? awayTeam : homeTeam;
        if (myElement == null) return null;

        var ratings = new TeamRatings
        {
            MidfieldRating = ParseIntOrZero(myElement, "RatingMidfield"),
            RightDefenseRating = ParseIntOrZero(myElement, "RatingRightDef"),
            CentralDefenseRating = ParseIntOrZero(myElement, "RatingMidDef"),
            LeftDefenseRating = ParseIntOrZero(myElement, "RatingLeftDef"),
            RightAttackRating = ParseIntOrZero(myElement, "RatingRightAtt"),
            CentralAttackRating = ParseIntOrZero(myElement, "RatingMidAtt"),
            LeftAttackRating = ParseIntOrZero(myElement, "RatingLeftAtt"),
            IndirectSetPiecesAttRating = ParseIntOrZero(myElement, "RatingIndirectSetPiecesAtt"),
            IndirectSetPiecesDefRating = ParseIntOrZero(myElement, "RatingIndirectSetPiecesDef")
        };
        if (ratings.MidfieldRating <= 0) return null; // walkower / brak ocen

        var homeGoals = ParseIntOrZero(matchElement, "HomeGoals");
        var awayGoals = ParseIntOrZero(matchElement, "AwayGoals");
        var opponentName = isHome
            ? (matchElement.Element("AwayTeam")?.Element("AwayTeamName")?.Value ?? "")
            : (matchElement.Element("HomeTeam")?.Element("HomeTeamName")?.Value ?? "");

        var entry = new ScoutMatchSummary
        {
            MatchId = long.Parse(matchId),
            MatchDate = ParseDate(matchElement.Element("MatchDate")?.Value),
            IsHomeMatch = isHome,
            Opponent = opponentName,
            GoalsFor = isHome ? homeGoals : awayGoals,
            GoalsAgainst = isHome ? awayGoals : homeGoals,
            Formation = myElement.Element("Formation")?.Value ?? "",
            Tactic = CalibrationService.MapTacticCode(ParseIntOrZero(myElement, "TacticType")),
            Ratings = ratings
        };

        if (!includeLineups) return entry;

        // Podstawowa jedenastka z matchlineup (role 100-113).
        var lineupDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matchlineup" }, { "matchId", matchId }, { "teamId", teamId.ToString() }, { "version", "2.1" }
        }, $"matchlineup matchId={matchId}");

        var teamElement = lineupDoc.Descendants("Team")
            .FirstOrDefault(t => t.Element("TeamID")?.Value == teamId.ToString());
        var lineupContainer = teamElement?.Element("StartingLineup") ?? teamElement?.Element("Lineup");

        foreach (var playerEl in lineupContainer?.Elements("Player") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            if (!int.TryParse(playerEl.Element("PlayerID")?.Value, out int pid)) continue;
            var roleId = int.Parse(playerEl.Element("RoleID")?.Value ?? "0");
            if (roleId < 100 || roleId > 113) continue;

            var slot = CalibrationService.MapRoleIdToSlot(roleId);
            if (string.IsNullOrEmpty(slot)) continue;

            if (!slotAppearances.TryGetValue(slot, out var perPlayer))
            {
                perPlayer = new Dictionary<int, ScoutStarterAggregate>();
                slotAppearances[slot] = perPlayer;
            }
            if (!perPlayer.TryGetValue(pid, out var agg))
            {
                agg = new ScoutStarterAggregate
                {
                    PlayerId = pid,
                    PlayerName = $"{playerEl.Element("FirstName")?.Value} {playerEl.Element("LastName")?.Value}".Trim()
                };
                perPlayer[pid] = agg;
            }
            agg.Appearances++;
        }

        return entry;
    }

    private static TeamRatings ComputeWeightedRatings(List<ScoutMatchSummary> matches)
    {
        // matches sa juz od najnowszego; waga maleje wykladniczo ze swiezoscia.
        double wSum = 0;
        double mid = 0, rd = 0, cd = 0, ld = 0, ra = 0, ca = 0, la = 0, ispAtt = 0, ispDef = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            double w = Math.Pow(RecencyDecay, i);
            var r = matches[i].Ratings;
            wSum += w;
            mid += w * r.MidfieldRating;
            rd += w * r.RightDefenseRating;
            cd += w * r.CentralDefenseRating;
            ld += w * r.LeftDefenseRating;
            ra += w * r.RightAttackRating;
            ca += w * r.CentralAttackRating;
            la += w * r.LeftAttackRating;
            ispAtt += w * r.IndirectSetPiecesAttRating;
            ispDef += w * r.IndirectSetPiecesDefRating;
        }
        return new TeamRatings
        {
            MidfieldRating = (int)Math.Round(mid / wSum),
            RightDefenseRating = (int)Math.Round(rd / wSum),
            CentralDefenseRating = (int)Math.Round(cd / wSum),
            LeftDefenseRating = (int)Math.Round(ld / wSum),
            RightAttackRating = (int)Math.Round(ra / wSum),
            CentralAttackRating = (int)Math.Round(ca / wSum),
            LeftAttackRating = (int)Math.Round(la / wSum),
            IndirectSetPiecesAttRating = (int)Math.Round(ispAtt / wSum),
            IndirectSetPiecesDefRating = (int)Math.Round(ispDef / wSum)
        };
    }

    private static List<ScoutLikelyStarter> BuildLikelyStarters(
        Dictionary<string, Dictionary<int, ScoutStarterAggregate>> slotAppearances)
    {
        // Zachlanne przypisanie: sloty w kolejnosci najmocniejszego kandydata,
        // kazdy gracz moze zajac tylko jeden slot.
        var used = new HashSet<int>();
        var result = new List<ScoutLikelyStarter>();

        var slotsByStrength = slotAppearances
            .OrderByDescending(kvp => kvp.Value.Values.Max(a => a.Appearances))
            .ToList();

        foreach (var (slot, perPlayer) in slotsByStrength)
        {
            var pick = perPlayer.Values
                .Where(a => !used.Contains(a.PlayerId))
                .OrderByDescending(a => a.Appearances)
                .FirstOrDefault();
            if (pick == null) continue;

            used.Add(pick.PlayerId);
            result.Add(new ScoutLikelyStarter
            {
                Slot = slot,
                PlayerId = pick.PlayerId,
                PlayerName = pick.PlayerName,
                Appearances = pick.Appearances
            });
        }

        return result.OrderBy(s => SlotOrder(s.Slot)).ToList();
    }

    private static int SlotOrder(string slot) => slot switch
    {
        "GK" => 0,
        "RWB" => 1, "RCD" => 2, "CD" => 3, "LCD" => 4, "LWB" => 5,
        "RW" => 6, "RIM" => 7, "IM" => 8, "LIM" => 9, "LW" => 10,
        "RFW" => 11, "FW" => 12, "LFW" => 13,
        _ => 99
    };

    private static int ParseIntOrZero(System.Xml.Linq.XElement parent, string name) =>
        int.TryParse(parent.Element(name)?.Value, out var v) ? v : 0;

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;

    private class ScoutStarterAggregate
    {
        public int PlayerId { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public int Appearances { get; set; }
    }
}

public class OpponentScoutReport
{
    public int TeamId { get; set; }
    public int MatchesAnalyzed { get; set; }
    public string MostCommonFormation { get; set; } = string.Empty;
    public Dictionary<string, int> FormationCounts { get; set; } = new();
    public string MostCommonTactic { get; set; } = "Normal";
    public Dictionary<string, int> TacticCounts { get; set; } = new();
    // Oceny sektorowe wazone swiezoscia (najnowszy mecz najwazniejszy).
    public TeamRatings WeightedRatings { get; set; } = new();
    public List<ScoutMatchSummary> Matches { get; set; } = new();
    public List<ScoutLikelyStarter> LikelyStarters { get; set; } = new();
}

public class ScoutMatchSummary
{
    public long MatchId { get; set; }
    public DateTime? MatchDate { get; set; }
    public bool IsHomeMatch { get; set; }
    public string Opponent { get; set; } = string.Empty;
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public string Formation { get; set; } = string.Empty;
    public string Tactic { get; set; } = "Normal";
    public TeamRatings Ratings { get; set; } = new();
}

public class ScoutLikelyStarter
{
    public string Slot { get; set; } = string.Empty;
    public int PlayerId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int Appearances { get; set; }
}
