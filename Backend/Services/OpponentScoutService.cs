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
    // Explicit heuristic weights: 45-day exponential recency and lower competition weight outside league/cup.
    private const double RecencyHalfLifeDays = 45;
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
    // leagueOnly=true ogranicza do meczow ligowych (typ 1) — towarzyskie graja czesto
    // rezerwami i zanizaja oceny, co psuje szacunek sily do symulacji sezonu.
    public async Task<OpponentScoutReport> GetScoutReportAsync(int teamId, int count = DefaultMatchCount, bool includeLineups = true, bool leagueOnly = false)
    {
        count = Math.Clamp(count, 2, 10);
        var cacheKey = $"{teamId}:{count}:{includeLineups}:{leagueOnly}";
        if (Cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
        {
            return cached.Report;
        }

        var report = await BuildReportAsync(teamId, count, includeLineups, leagueOnly);
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
                    SourceMatchDate = report.Matches.FirstOrDefault()?.MatchDate,
                    SampleCount = report.MatchesAnalyzed,
                    Warnings = new() { "Observed ratings retain match tactic effects; home midfield is normalized by the public home advantage multiplier." }
                };
            }
        }
        catch (ChppApiException ex)
        {
            _logger.LogWarning(ex, "Skauting przeciwnika {TeamId} nie powiodl sie — fallback do ostatniego meczu.", teamId);
        }

        return await _api.GetOpponentRatingsAsync(teamId);
    }

    private async Task<OpponentScoutReport> BuildReportAsync(int teamId, int count, bool includeLineups = true, bool leagueOnly = false)
    {
        var matchesDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matches" }, { "teamId", teamId.ToString() }, { "version", "2.8" }
        }, $"matches teamId={teamId}");

        var played = matchesDoc.Descendants("Match")
            .Where(m =>
            {
            var status = m.Element("Status")?.Value ?? "";
                var matchTypeKnown = int.TryParse(m.Element("MatchType")?.Value, out var matchType);
                bool typeOk = leagueOnly ? matchType == 1 : matchType >= 1 && matchType <= 12;
                return matchTypeKnown && status.Equals("FINISHED", StringComparison.OrdinalIgnoreCase) && typeOk;
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
        if (report.MatchesAnalyzed < played.Count)
            report.Warnings.Add($"{played.Count - report.MatchesAnalyzed} candidate match(es) were excluded because public match details or ratings were unavailable/invalid.");
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

        report.WeightedRatingsPrecise = ComputeWeightedRatingsPrecise(report.Matches);
        report.WeightedRatings = report.WeightedRatingsPrecise.ToTeamRatings();
        report.RatingUncertainty = ComputeUncertainty(report.Matches);
        report.UncertaintyAvailable = report.Matches.Count >= 2;
        report.EffectiveSampleSize = ComputeEffectiveSampleSize(report.Matches);
        report.AverageSampleAgeDays = report.Matches.Where(m => m.MatchDate.HasValue)
            .Select(m => Math.Max(0, (DateTime.UtcNow - m.MatchDate!.Value.ToUniversalTime()).TotalDays)).DefaultIfEmpty().Average();
        report.CompetitionCounts = report.Matches.GroupBy(m => m.MatchType)
            .ToDictionary(g => CompetitionName(g.Key), g => g.Count());
        report.RatingsScopeNote = "Ratings are observed historical match ratings; home midfield is normalized once by dividing out the home multiplier. Historical tactic effects remain embedded and are not treated as future scenarios. Aggregation uses an explicit 45-day exponential half-life multiplied by competition weights (league/cup/Masters 1.00, qualification 0.95, friendlies 0.60-0.65, reserved/national competitions 0.90); these weights are a stated scouting heuristic, not an official Hattrick formula.";
        if (!report.UncertaintyAvailable) report.Warnings.Add("At least two observed matches are required for rating dispersion; the reported zero placeholders are unavailable, not zero uncertainty.");
        report.Sources = report.Matches.Select(m => new ScoutSource
        {
            MatchId = m.MatchId, MatchDate = m.MatchDate, MatchType = m.MatchType,
            IsHomeMatch = m.IsHomeMatch, Source = "CHPP matchdetails"
        }).ToList();
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

        if (!TryParseObservedRatings(myElement, out var ratings) || ratings.MidfieldRating <= 0)
        {
            _logger.LogWarning("Scout excluded match {MatchId}: public rating sectors were missing or invalid.", matchId);
            return null;
        }
        // Remove the public home-field midfield multiplier once, retaining fractional ratings
        // until the final weighted aggregate. Other observed tactical effects remain in place.
        var neutral = ScoutRatingVector.From(ratings);
        if (isHome) neutral.Midfield /= FormationData.TacticModifiers.HomeAdvantage;

        var homeGoals = ParseIntOrZero(matchElement, "HomeGoals");
        var awayGoals = ParseIntOrZero(matchElement, "AwayGoals");
        var opponentName = isHome
            ? (matchElement.Element("AwayTeam")?.Element("AwayTeamName")?.Value ?? "")
            : (matchElement.Element("HomeTeam")?.Element("HomeTeamName")?.Value ?? "");

        var entry = new ScoutMatchSummary
        {
            MatchId = long.Parse(matchId),
            MatchDate = ParseDate(matchElement.Element("MatchDate")?.Value),
            MatchType = int.TryParse(matchElement.Element("MatchType")?.Value, out var matchType) ? matchType : 0,
            IsHomeMatch = isHome,
            Opponent = opponentName,
            GoalsFor = isHome ? homeGoals : awayGoals,
            GoalsAgainst = isHome ? awayGoals : homeGoals,
            Formation = myElement.Element("Formation")?.Value ?? "",
            Tactic = CalibrationService.MapTacticCode(ParseIntOrZero(myElement, "TacticType")),
            Ratings = ratings,
            NeutralRatings = neutral,
            CompetitionWeight = CompetitionWeight(ParseIntOrZero(matchElement, "MatchType"))
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

    private static ScoutRatingVector ComputeWeightedRatingsPrecise(List<ScoutMatchSummary> matches)
    {
        double wSum = 0;
        double mid = 0, rd = 0, cd = 0, ld = 0, ra = 0, ca = 0, la = 0, ispAtt = 0, ispDef = 0;
        var now = DateTime.UtcNow;
        foreach (var match in matches)
        {
            var ageDays = match.MatchDate.HasValue ? Math.Max(0, (now - match.MatchDate.Value.ToUniversalTime()).TotalDays) : 3650;
            double w = Math.Exp(-Math.Log(2) * ageDays / RecencyHalfLifeDays) * match.CompetitionWeight;
            var r = match.NeutralRatings;
            wSum += w;
            mid += w * r.Midfield; rd += w * r.RightDefense; cd += w * r.CentralDefense;
            ld += w * r.LeftDefense; ra += w * r.RightAttack; ca += w * r.CentralAttack;
            la += w * r.LeftAttack; ispAtt += w * r.IndirectSetPiecesAtt; ispDef += w * r.IndirectSetPiecesDef;
        }
        return wSum <= 0 ? new ScoutRatingVector() : new ScoutRatingVector
        {
            Midfield = mid/wSum, RightDefense = rd/wSum, CentralDefense = cd/wSum, LeftDefense = ld/wSum,
            RightAttack = ra/wSum, CentralAttack = ca/wSum, LeftAttack = la/wSum,
            IndirectSetPiecesAtt = ispAtt/wSum, IndirectSetPiecesDef = ispDef/wSum
        };
    }

    private static double CompetitionWeight(int matchType) => matchType switch
    {
        1 or 3 or 7 => 1.0, // league, cup, Hattrick Masters
        2 => 0.95, // qualification
        4 or 8 => 0.65, // friendly
        5 or 9 or 12 => 0.60, // friendly with cup rules / national friendly
        6 or 10 or 11 => 0.90,
        _ => 0.75
    };

    private static string CompetitionName(int type) => type switch
    {
        1 => "league", 2 => "qualification", 3 => "cup", 4 => "friendly", 5 => "friendlyCupRules",
        6 => "internationalCompetitionReserved", 7 => "hattrickMasters", 8 => "internationalFriendly",
        9 => "internationalFriendlyCupRules", 10 => "nationalCompetition", 11 => "nationalCompetitionCupRules",
        12 => "nationalFriendly", _ => $"unknown:{type}"
    };

    private static double ComputeEffectiveSampleSize(List<ScoutMatchSummary> matches)
    {
        var now = DateTime.UtcNow;
        var weights = matches.Select(m =>
        {
            var age = m.MatchDate.HasValue ? Math.Max(0, (now - m.MatchDate.Value.ToUniversalTime()).TotalDays) : 3650;
            return Math.Exp(-Math.Log(2) * age / RecencyHalfLifeDays) * m.CompetitionWeight;
        }).ToList();
        var sum = weights.Sum();
        var squares = weights.Sum(w => w * w);
        return squares > 0 ? sum * sum / squares : 0;
    }

    private static TeamRatings ComputeUncertainty(List<ScoutMatchSummary> matches)
    {
        var now = DateTime.UtcNow;
        static double Sd(IEnumerable<(double Value, double Weight)> values)
        {
            var sample = values.Where(v => v.Weight > 0).ToList();
            if (sample.Count < 2) return 0;
            var total = sample.Sum(v => v.Weight);
            var mean = sample.Sum(v => v.Value * v.Weight) / total;
            return Math.Sqrt(sample.Sum(v => v.Weight * Math.Pow(v.Value - mean, 2)) / total);
        }
        var weighted = matches.Select(m =>
        {
            var age = m.MatchDate.HasValue ? Math.Max(0, (now - m.MatchDate.Value.ToUniversalTime()).TotalDays) : 3650;
            return (m, Math.Exp(-Math.Log(2) * age / RecencyHalfLifeDays) * m.CompetitionWeight);
        }).ToList();
        return new TeamRatings
        {
            MidfieldRating = Sd(weighted.Select(x => (x.m.NeutralRatings.Midfield, x.Item2))),
            RightDefenseRating = Sd(weighted.Select(x => (x.m.NeutralRatings.RightDefense, x.Item2))),
            CentralDefenseRating = Sd(weighted.Select(x => (x.m.NeutralRatings.CentralDefense, x.Item2))),
            LeftDefenseRating = Sd(weighted.Select(x => (x.m.NeutralRatings.LeftDefense, x.Item2))),
            RightAttackRating = Sd(weighted.Select(x => (x.m.NeutralRatings.RightAttack, x.Item2))),
            CentralAttackRating = Sd(weighted.Select(x => (x.m.NeutralRatings.CentralAttack, x.Item2))),
            LeftAttackRating = Sd(weighted.Select(x => (x.m.NeutralRatings.LeftAttack, x.Item2)))
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

    private static bool TryParseObservedRatings(System.Xml.Linq.XElement team, out TeamRatings ratings)
    {
        ratings = new TeamRatings();
        var names = new[] { "RatingMidfield", "RatingRightDef", "RatingMidDef", "RatingLeftDef",
            "RatingRightAtt", "RatingMidAtt", "RatingLeftAtt", "RatingIndirectSetPiecesAtt", "RatingIndirectSetPiecesDef" };
        var values = new double[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            if (!double.TryParse(team.Element(names[i])?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || !double.IsFinite(values[i]) || values[i] < 0) return false;
        }
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
    public ScoutRatingVector WeightedRatingsPrecise { get; set; } = new();
    public TeamRatings RatingUncertainty { get; set; } = new();
    public bool UncertaintyAvailable { get; set; }
    public double EffectiveSampleSize { get; set; }
    public double AverageSampleAgeDays { get; set; }
    public Dictionary<string, int> CompetitionCounts { get; set; } = new();
    public string RatingsScopeNote { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
    public List<ScoutSource> Sources { get; set; } = new();
    public List<ScoutMatchSummary> Matches { get; set; } = new();
    public List<ScoutLikelyStarter> LikelyStarters { get; set; } = new();
}

public class ScoutMatchSummary
{
    public long MatchId { get; set; }
    public DateTime? MatchDate { get; set; }
    public int MatchType { get; set; }
    public bool IsHomeMatch { get; set; }
    public string Opponent { get; set; } = string.Empty;
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public string Formation { get; set; } = string.Empty;
    public string Tactic { get; set; } = "Normal";
    public TeamRatings Ratings { get; set; } = new();
    public ScoutRatingVector NeutralRatings { get; set; } = new();
    public double CompetitionWeight { get; set; }
}

public class ScoutRatingVector
{
    public double Midfield { get; set; }
    public double RightDefense { get; set; }
    public double CentralDefense { get; set; }
    public double LeftDefense { get; set; }
    public double RightAttack { get; set; }
    public double CentralAttack { get; set; }
    public double LeftAttack { get; set; }
    public double IndirectSetPiecesAtt { get; set; }
    public double IndirectSetPiecesDef { get; set; }
    public static ScoutRatingVector From(TeamRatings r) => new()
    {
        Midfield = r.MidfieldRating, RightDefense = r.RightDefenseRating, CentralDefense = r.CentralDefenseRating,
        LeftDefense = r.LeftDefenseRating, RightAttack = r.RightAttackRating, CentralAttack = r.CentralAttackRating,
        LeftAttack = r.LeftAttackRating, IndirectSetPiecesAtt = r.IndirectSetPiecesAttRating,
        IndirectSetPiecesDef = r.IndirectSetPiecesDefRating
    };
    public TeamRatings ToTeamRatings() => new()
    {
        MidfieldRating = Midfield, RightDefenseRating = RightDefense,
        CentralDefenseRating = CentralDefense, LeftDefenseRating = LeftDefense,
        RightAttackRating = RightAttack, CentralAttackRating = CentralAttack,
        LeftAttackRating = LeftAttack, IndirectSetPiecesAttRating = IndirectSetPiecesAtt,
        IndirectSetPiecesDefRating = IndirectSetPiecesDef
    };
}

public class ScoutSource
{
    public long MatchId { get; set; }
    public DateTime? MatchDate { get; set; }
    public int MatchType { get; set; }
    public bool IsHomeMatch { get; set; }
    public string Source { get; set; } = "";
}

public class ScoutLikelyStarter
{
    public string Slot { get; set; } = string.Empty;
    public int PlayerId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int Appearances { get; set; }
}
