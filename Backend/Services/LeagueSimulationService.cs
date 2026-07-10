using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>
/// Symulator tabeli ligowej: pobiera tabele (leaguedetails) i terminarz (leaguefixtures)
/// ligi uzytkownika, ocenia sile kazdej druzyny skautem (wazone oceny z ostatnich meczow)
/// i symuluje pozostale mecze sezonu Monte Carlo tym samym modelem Poissona co optymalizator.
///
/// Uproszczenia: taktyka Normal dla wszystkich, atut gospodarza tylko na srodku pola,
/// tie-break punkty -> roznica bramek -> bramki strzelone (bez regul head-to-head HT).
/// </summary>
public class LeagueSimulationService
{
    private const int ScoutMatchCount = 3;
    private const int Iterations = 5000;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (DateTime At, LeagueSimulationReport Report)> Cache = new();

    private readonly HattrickApiService _api;
    private readonly OpponentScoutService _scout;
    private readonly ILogger<LeagueSimulationService> _logger;

    public LeagueSimulationService(HattrickApiService api, OpponentScoutService scout, ILogger<LeagueSimulationService> logger)
    {
        _api = api;
        _scout = scout;
        _logger = logger;
    }

    /// <param name="fromFirstRound">true = symulacja calego sezonu od 1. kolejki (wyniki
    /// rozegranych meczow ignorowane, punkty od zera); false = od stanu obecnego.</param>
    public async Task<LeagueSimulationReport> SimulateAsync(int ownTeamId, bool fromFirstRound = false)
    {
        var leagueUnitId = await GetLeagueUnitIdAsync(ownTeamId);
        var cacheKey = $"{leagueUnitId}:{fromFirstRound}";
        if (Cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
        {
            return cached.Report;
        }

        var report = await BuildReportAsync(ownTeamId, leagueUnitId, fromFirstRound);
        Cache[cacheKey] = (DateTime.UtcNow, report);
        return report;
    }

    private async Task<int> GetLeagueUnitIdAsync(int teamId)
    {
        var doc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "teamdetails" }, { "teamId", teamId.ToString() }, { "version", "3.6" }
        }, $"teamdetails teamId={teamId}");

        // teamdetails moze zwrocic wiele druzyn uzytkownika — wybierz wlasciwa.
        var team = doc.Descendants("Team")
            .FirstOrDefault(t => t.Element("TeamID")?.Value == teamId.ToString())
            ?? doc.Descendants("Team").FirstOrDefault();
        var unitId = int.Parse(team?.Element("LeagueLevelUnit")?.Element("LeagueLevelUnitID")?.Value ?? "0");
        if (unitId == 0)
        {
            throw new ChppApiException($"Nie znaleziono LeagueLevelUnitID dla druzyny {teamId}.");
        }
        return unitId;
    }

    private async Task<LeagueSimulationReport> BuildReportAsync(int ownTeamId, int leagueUnitId, bool fromFirstRound)
    {
        // Tabela ligi.
        var standingsDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "leaguedetails" }, { "leagueLevelUnitID", leagueUnitId.ToString() }
        }, $"leaguedetails unit={leagueUnitId}");

        var report = new LeagueSimulationReport
        {
            LeagueLevelUnitId = leagueUnitId,
            LeagueName = standingsDoc.Descendants("LeagueLevelUnitName").FirstOrDefault()?.Value ?? "",
            Iterations = Iterations,
            FromFirstRound = fromFirstRound
        };

        var teams = new List<LeagueTeamForecast>();
        foreach (var t in standingsDoc.Descendants("Team"))
        {
            var id = int.Parse(t.Element("TeamID")?.Value ?? "0");
            if (id == 0) continue;
            teams.Add(new LeagueTeamForecast
            {
                TeamId = id,
                TeamName = t.Element("TeamName")?.Value ?? "",
                CurrentPosition = int.Parse(t.Element("Position")?.Value ?? "0"),
                Matches = int.Parse(t.Element("Matches")?.Value ?? "0"),
                GoalsFor = int.Parse(t.Element("GoalsFor")?.Value ?? "0"),
                GoalsAgainst = int.Parse(t.Element("GoalsAgainst")?.Value ?? "0"),
                Points = int.Parse(t.Element("Points")?.Value ?? "0"),
                IsOwnTeam = id == ownTeamId
            });
        }
        if (teams.Count == 0)
        {
            throw new ChppApiException($"leaguedetails nie zwrocilo druzyn dla ligi {leagueUnitId}.");
        }
        teams = teams.OrderBy(t => t.CurrentPosition).ToList();
        var teamIndex = teams.Select((t, i) => (t.TeamId, Index: i)).ToDictionary(x => x.TeamId, x => x.Index);

        // Terminarz — mecze bez wyniku traktujemy jako pozostale do rozegrania.
        var fixturesDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "leaguefixtures" }, { "leagueLevelUnitID", leagueUnitId.ToString() }
        }, $"leaguefixtures unit={leagueUnitId}");

        var remaining = new List<(int HomeIdx, int AwayIdx)>();
        foreach (var m in fixturesDoc.Descendants("Match"))
        {
            var homeId = int.Parse(m.Element("HomeTeam")?.Element("HomeTeamID")?.Value ?? "0");
            var awayId = int.Parse(m.Element("AwayTeam")?.Element("AwayTeamID")?.Value ?? "0");
            if (!teamIndex.TryGetValue(homeId, out var hi) || !teamIndex.TryGetValue(awayId, out var ai)) continue;

            if (!fromFirstRound)
            {
                bool played = m.Element("HomeGoals") != null && m.Element("AwayGoals") != null;
                if (played) continue;
                var date = ParseDate(m.Element("MatchDate")?.Value);
                if (date != null && date < DateTime.UtcNow.AddHours(-3)) continue; // mecz trwa / brak wyniku w cache HT
            }

            remaining.Add((hi, ai));
        }
        report.RemainingMatches = remaining.Count;

        // Sila kazdej druzyny: wazone oceny skauta (bez skladow — mniej zapytan CHPP).
        var ratings = new TeamRatings[teams.Count];
        for (int i = 0; i < teams.Count; i++)
        {
            try
            {
                // Tylko mecze ligowe — towarzyskie (rezerwy) zanizaja szacunek sily.
                var scout = await _scout.GetScoutReportAsync(teams[i].TeamId, ScoutMatchCount, includeLineups: false, leagueOnly: true);
                if (scout.MatchesAnalyzed > 0)
                {
                    ratings[i] = scout.WeightedRatings;
                    teams[i].RatingsSource = "scout";
                    teams[i].TypicalTactic = scout.MostCommonTactic;
                }
            }
            catch (ChppApiException ex)
            {
                _logger.LogWarning(ex, "Symulacja ligi: brak ocen dla druzyny {TeamId}", teams[i].TeamId);
            }
            ratings[i] ??= NeutralRatings();
            if (teams[i].RatingsSource != "scout") teams[i].RatingsSource = "default";
            teams[i].Ratings = ratings[i];
        }

        // Prawdopodobienstwa lambd per mecz liczone raz, potem Monte Carlo.
        var lambdas = remaining
            .Select(f => ComputeLambdas(
                ratings[f.HomeIdx], ratings[f.AwayIdx],
                teams[f.HomeIdx].TypicalTactic, teams[f.AwayIdx].TypicalTactic))
            .ToList();

        RunMonteCarlo(teams, remaining, lambdas, report);
        report.Teams = teams;
        return report;
    }

    private static void RunMonteCarlo(
        List<LeagueTeamForecast> teams,
        List<(int HomeIdx, int AwayIdx)> remaining,
        List<(double Home, double Away)> lambdas,
        LeagueSimulationReport report)
    {
        int n = teams.Count;
        var positionCounts = new int[n, n]; // [team, pozycja-1]
        var pointsSum = new double[n];
        // Staly seed: wynik stabilny miedzy wywolaniami dla tego samego stanu ligi.
        var rng = new Random(report.LeagueLevelUnitId * 397 + remaining.Count * 2 + (report.FromFirstRound ? 1 : 0));
        // Od 1. kolejki: punkty i bramki liczone od zera, wyniki rozegranych meczow ignorowane.
        bool fromZero = report.FromFirstRound;

        var simPoints = new int[n];
        var simGf = new int[n];
        var simGa = new int[n];
        var order = new int[n];

        for (int it = 0; it < Iterations; it++)
        {
            for (int i = 0; i < n; i++)
            {
                simPoints[i] = fromZero ? 0 : teams[i].Points;
                simGf[i] = fromZero ? 0 : teams[i].GoalsFor;
                simGa[i] = fromZero ? 0 : teams[i].GoalsAgainst;
                order[i] = i;
            }

            for (int f = 0; f < remaining.Count; f++)
            {
                var (hi, ai) = remaining[f];
                int gh = SamplePoisson(rng, lambdas[f].Home);
                int ga = SamplePoisson(rng, lambdas[f].Away);
                simGf[hi] += gh; simGa[hi] += ga;
                simGf[ai] += ga; simGa[ai] += gh;
                if (gh > ga) simPoints[hi] += 3;
                else if (gh < ga) simPoints[ai] += 3;
                else { simPoints[hi] += 1; simPoints[ai] += 1; }
            }

            // Sortowanie tabeli: punkty, roznica bramek, bramki strzelone.
            Array.Sort(order, (a, b) =>
            {
                int cmp = simPoints[b].CompareTo(simPoints[a]);
                if (cmp != 0) return cmp;
                cmp = (simGf[b] - simGa[b]).CompareTo(simGf[a] - simGa[a]);
                if (cmp != 0) return cmp;
                return simGf[b].CompareTo(simGf[a]);
            });

            for (int pos = 0; pos < n; pos++)
            {
                positionCounts[order[pos], pos]++;
            }
            for (int i = 0; i < n; i++) pointsSum[i] += simPoints[i];
        }

        for (int i = 0; i < n; i++)
        {
            var probs = new double[n];
            double expectedPos = 0;
            for (int pos = 0; pos < n; pos++)
            {
                probs[pos] = (double)positionCounts[i, pos] / Iterations;
                expectedPos += probs[pos] * (pos + 1);
            }
            teams[i].PositionProbabilities = probs;
            teams[i].ExpectedPosition = Math.Round(expectedPos, 2);
            teams[i].ExpectedPoints = Math.Round(pointsSum[i] / Iterations, 1);
            teams[i].WinLeagueProbability = probs[0];
        }
    }

    /// <summary>
    /// Oczekiwane bramki obu druzyn tym samym modelem co RatingEngine.PredictOutcome
    /// (10 akcji dzielonych srodkiem pola ^2.75, finalizacja ^3.5, rozklad sektorow
    /// 35/25/25/15). Taktyki modyfikujace OCENY (AIM/AOW/kreatywnie/strzaly) sa juz
    /// zawarte w realnych ocenach z matchdetails; osobno modelujemy tylko kontre
    /// (dodatkowe szanse przy mniejszosci posiadania) i pressing (tlumi obie strony).
    /// </summary>
    internal static (double Home, double Away) ComputeLambdas(
        TeamRatings home, TeamRatings away, string homeTactic = "Normal", string awayTactic = "Normal")
    {
        double midHome = Math.Max(0.01, home.MidfieldRating * FormationData.TacticModifiers.HomeAdvantage);
        double midAway = Math.Max(0.01, away.MidfieldRating);
        const double midExp = 2.75;
        double hPow = Math.Pow(midHome, midExp);
        double aPow = Math.Pow(midAway, midExp);
        double homeShare = hPow / (hPow + aPow);
        double actionsHome = 10.0 * homeShare;
        double actionsAway = 10.0 * (1.0 - homeShare);

        double homeIspAtt = home.IndirectSetPiecesAttRating > 0 ? home.IndirectSetPiecesAttRating : 0.8 * home.CentralAttackRating;
        double homeIspDef = home.IndirectSetPiecesDefRating > 0 ? home.IndirectSetPiecesDefRating : 0.9 * home.CentralDefenseRating;
        double awayIspAtt = away.IndirectSetPiecesAttRating > 0 ? away.IndirectSetPiecesAttRating : 0.8 * away.CentralAttackRating;
        double awayIspDef = away.IndirectSetPiecesDefRating > 0 ? away.IndirectSetPiecesDefRating : 0.9 * away.CentralDefenseRating;

        double pGoalHome =
            0.35 * FinProb(home.CentralAttackRating, away.CentralDefenseRating) +
            0.25 * FinProb(home.RightAttackRating, away.LeftDefenseRating) +
            0.25 * FinProb(home.LeftAttackRating, away.RightDefenseRating) +
            0.15 * FinProb(homeIspAtt, awayIspDef);
        double pGoalAway =
            0.35 * FinProb(away.CentralAttackRating, home.CentralDefenseRating) +
            0.25 * FinProb(away.RightAttackRating, home.LeftDefenseRating) +
            0.25 * FinProb(away.LeftAttackRating, home.RightDefenseRating) +
            0.15 * FinProb(awayIspAtt, homeIspDef);

        double lamHome = actionsHome * pGoalHome;
        double lamAway = actionsAway * pGoalAway;

        // Kontra: dodatkowe szanse z przechwytow przy mniejszosci posiadania.
        // Konwersja ~0.2 (srodek przedzialu RatingEngine 0.05-0.35 — brak skilli obroncow).
        const double CounterConversion = 0.2;
        if (homeTactic == "Counter" && homeShare < 0.5) lamHome += actionsAway * CounterConversion * pGoalHome;
        if (awayTactic == "Counter" && homeShare > 0.5) lamAway += actionsHome * CounterConversion * pGoalAway;

        // Pressing: tlumi szanse OBU druzyn (srodek przedzialu RatingEngine 0.10-0.30).
        const double PressingSuppression = 0.2;
        if (homeTactic == "Pressing" || awayTactic == "Pressing")
        {
            lamHome *= 1 - PressingSuppression;
            lamAway *= 1 - PressingSuppression;
        }

        return (
            Math.Clamp(lamHome, 0.05, 8.0),
            Math.Clamp(lamAway, 0.05, 8.0)
        );
    }

    private static double FinProb(double att, double def)
    {
        const double finExp = 3.5;
        double a = Math.Pow(Math.Max(att, 0.01), finExp);
        double d = Math.Pow(Math.Max(def, 0.01), finExp);
        return a / (a + d);
    }

    private static int SamplePoisson(Random rng, double lambda)
    {
        // Algorytm Knutha — lambdy sa male (<= 8), wiec wystarczajaco szybki.
        double l = Math.Exp(-lambda);
        int k = 0;
        double p = 1.0;
        do
        {
            k++;
            p *= rng.NextDouble();
        } while (p > l);
        return k - 1;
    }

    private static TeamRatings NeutralRatings() => new()
    {
        MidfieldRating = 30,
        RightDefenseRating = 30,
        CentralDefenseRating = 30,
        LeftDefenseRating = 30,
        RightAttackRating = 30,
        CentralAttackRating = 30,
        LeftAttackRating = 30
    };

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
}

public class LeagueSimulationReport
{
    public int LeagueLevelUnitId { get; set; }
    public string LeagueName { get; set; } = string.Empty;
    public int RemainingMatches { get; set; }
    public int Iterations { get; set; }
    // true = symulacja calego sezonu od 1. kolejki (bez uwzgledniania rozegranych wynikow).
    public bool FromFirstRound { get; set; }
    public List<LeagueTeamForecast> Teams { get; set; } = new();
}

public class LeagueTeamForecast
{
    public int TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public bool IsOwnTeam { get; set; }
    public int CurrentPosition { get; set; }
    public int Matches { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public int Points { get; set; }
    public double ExpectedPoints { get; set; }
    public double ExpectedPosition { get; set; }
    public double WinLeagueProbability { get; set; }
    // Indeks 0 = pozycja 1. Suma = 1 dla kazdej druzyny.
    public double[] PositionProbabilities { get; set; } = Array.Empty<double>();
    public string RatingsSource { get; set; } = "default";
    // Najczestsza taktyka z ligowych meczow (skaut) — uzyta w modelu meczu.
    public string TypicalTactic { get; set; } = "Normal";
    public TeamRatings? Ratings { get; set; }
}
