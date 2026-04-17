using System.Globalization;
using System.Xml.Linq;
using HattrickAnalizer.Models;
using HattrickAnalizer.Controllers;

namespace HattrickAnalizer.Services;

public class NextOpponentInfo
{
    public long MatchId { get; set; }
    public int OpponentTeamId { get; set; }
    public string OpponentTeamName { get; set; } = string.Empty;
    public DateTime? MatchDate { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public bool IsHomeMatch { get; set; }
}

public class HattrickApiService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly OAuthService _oauthService;
    private readonly TokenStore _tokenStore;
    private readonly string _baseUrl;
    private readonly Dictionary<int, TeamRatings> _mockRatingsCache = new();

    public HattrickApiService(HttpClient httpClient, IConfiguration configuration, OAuthService oauthService, TokenStore tokenStore)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _oauthService = oauthService;
        _tokenStore = tokenStore;
        _baseUrl = _configuration["HattrickApi:BaseUrl"] ?? "https://chpp.hattrick.org/chppxml.ashx";
    }

    public async Task<Team> GetTeamDetailsAsync(int teamId, string? sessionId = null)
    {
        var team = new Team
        {
            TeamId = teamId,
            TeamName = $"Team {teamId}"
        };

        var (accessToken, accessTokenSecret) = ResolveTokens(sessionId);
        if (accessToken != null && accessTokenSecret != null)
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "file", "teamdetails" },
                    { "teamId", teamId.ToString() },
                    { "version", "3.6" }
                };
                var xml = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);
                var doc = XDocument.Parse(xml);
                var teamElement = doc.Descendants("Team").FirstOrDefault(t => int.Parse(t.Element("TeamID")?.Value ?? "0") == teamId)
                                  ?? doc.Descendants("Team").FirstOrDefault();
                if (teamElement != null)
                {
                    team.TeamName = teamElement.Element("TeamName")?.Value ?? team.TeamName;
                }
            }
            catch
            {
            }
        }

        team.Players = await GetTeamPlayersAsync(teamId, sessionId);
        return team;
    }

    public async Task<List<Player>> GetTeamPlayersAsync(int teamId, string? sessionId = null)
    {
        var (accessToken, accessTokenSecret) = ResolveTokens(sessionId);
        if (accessToken == null || accessTokenSecret == null)
        {
            return GenerateMockPlayers();
        }

        try
        {
            var queryParams = new Dictionary<string, string>
            {
                { "file", "players" },
                { "teamId", teamId.ToString() },
                { "version", "2.8" }
            };
            var response = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);

            var doc = XDocument.Parse(response);
            var players = new List<Player>();
            foreach (var playerElement in doc.Descendants("Player"))
            {
                players.Add(ParsePlayer(playerElement));
            }

            try
            {
                await EnrichPlayersWithMatchStatsAsync(players, teamId, accessToken, accessTokenSecret);
            }
            catch
            {
                // enrichment jest best-effort - nie przerywaj ładowania gdy nie wyszło
            }

            return players;
        }
        catch
        {
            return GenerateMockPlayers();
        }
    }

    private async Task EnrichPlayersWithMatchStatsAsync(List<Player> players, int teamId, string accessToken, string accessTokenSecret)
    {
        if (players.Count == 0) return;

        var matchQuery = new Dictionary<string, string>
        {
            { "file", "matches" },
            { "teamId", teamId.ToString() },
            { "version", "2.8" }
        };
        var matchesXml = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, matchQuery);
        var matchesDoc = XDocument.Parse(matchesXml);

        var finishedMatchIds = matchesDoc.Descendants("Match")
            .Where(m => (m.Element("Status")?.Value ?? "").Equals("FINISHED", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Element("MatchID")?.Value)
            .Where(id => !string.IsNullOrEmpty(id))
            .Take(10)
            .ToList();

        if (finishedMatchIds.Count == 0) return;

        var playerIndex = players.ToDictionary(p => p.PlayerId);
        var appearances = players.ToDictionary(p => p.PlayerId, _ => new PlayerAppearanceAggregate());

        var lineupTasks = finishedMatchIds.Select(id => FetchMatchLineupAsync(id!, accessToken, accessTokenSecret));
        var lineups = await Task.WhenAll(lineupTasks);

        foreach (var lineupXml in lineups)
        {
            if (string.IsNullOrEmpty(lineupXml)) continue;
            try
            {
                AggregateLineup(lineupXml, teamId, playerIndex, appearances);
            }
            catch
            {
                // pomiń uszkodzony lineup
            }
        }

        foreach (var player in players)
        {
            if (!appearances.TryGetValue(player.PlayerId, out var agg) || agg.Matches == 0) continue;

            var stats = player.MatchStats ?? new PlayerMatchStats();
            stats.TotalMatches = agg.Matches;
            stats.Goals = agg.Goals;
            stats.MinutesPlayed = agg.Minutes;
            stats.AverageRating = agg.Matches > 0 ? Math.Round(agg.RatingSum / agg.Matches, 2) : 0;
            stats.AverageForm = agg.Matches > 0 ? Math.Round(agg.RatingSum / agg.Matches, 1) : player.Form;
            stats.GoalsPerMatch = agg.Matches > 0 ? Math.Round((double)agg.Goals / agg.Matches, 2) : 0;
            stats.MatchesPerGoal = agg.Goals > 0 ? Math.Round((double)agg.Matches / agg.Goals, 1) : 0;
            player.MatchStats = stats;
        }
    }

    private async Task<string?> FetchMatchLineupAsync(string matchId, string accessToken, string accessTokenSecret)
    {
        try
        {
            var queryParams = new Dictionary<string, string>
            {
                { "file", "matchlineup" },
                { "matchId", matchId },
                { "version", "2.1" }
            };
            return await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);
        }
        catch
        {
            return null;
        }
    }

    private void AggregateLineup(string lineupXml, int teamId, Dictionary<int, Player> playerIndex, Dictionary<int, PlayerAppearanceAggregate> appearances)
    {
        var doc = XDocument.Parse(lineupXml);
        var teamElement = doc.Descendants("Team")
            .FirstOrDefault(t => int.TryParse(t.Element("TeamID")?.Value, out int id) && id == teamId);
        if (teamElement == null) return;

        var scorerElements = teamElement.Descendants("Goal")
            .Concat(doc.Descendants("Goal"))
            .Distinct();
        var goalsByPlayer = new Dictionary<int, int>();
        foreach (var goal in scorerElements)
        {
            var teamIdEl = goal.Element("ScorerTeamID")?.Value;
            if (teamIdEl != null && int.TryParse(teamIdEl, out int scoringTeamId) && scoringTeamId != teamId) continue;
            if (int.TryParse(goal.Element("ScorerPlayerID")?.Value, out int scorerId))
            {
                goalsByPlayer[scorerId] = goalsByPlayer.GetValueOrDefault(scorerId, 0) + 1;
            }
        }

        var lineupPlayers = new List<XElement>();
        var starting = teamElement.Element("StartingLineup");
        if (starting != null) lineupPlayers.AddRange(starting.Elements("Player"));
        var subs = teamElement.Element("Substitutions");
        if (subs != null) lineupPlayers.AddRange(subs.Elements("Player"));
        var bench = teamElement.Element("Bench");
        if (bench != null) lineupPlayers.AddRange(bench.Elements("Player"));

        var seenPlayers = new HashSet<int>();
        foreach (var playerEl in lineupPlayers)
        {
            if (!int.TryParse(playerEl.Element("PlayerID")?.Value, out int pid)) continue;
            if (!playerIndex.ContainsKey(pid)) continue;
            if (!appearances.TryGetValue(pid, out var agg)) continue;

            var minutes = int.Parse(playerEl.Element("PlayedMinutes")?.Value ?? "0");
            if (minutes <= 0) continue;
            if (!seenPlayers.Add(pid)) continue;

            var rating = double.Parse(playerEl.Element("RatingEndOfGame")?.Value ?? playerEl.Element("Rating")?.Value ?? "0", CultureInfo.InvariantCulture);

            agg.Matches += 1;
            agg.Minutes += minutes;
            agg.RatingSum += rating;
            agg.Goals += goalsByPlayer.GetValueOrDefault(pid, 0);
        }
    }

    private class PlayerAppearanceAggregate
    {
        public int Matches { get; set; }
        public int Minutes { get; set; }
        public int Goals { get; set; }
        public double RatingSum { get; set; }
    }

    public async Task<NextOpponentInfo?> GetNextOpponentAsync(int teamId, string? sessionId = null)
    {
        var (accessToken, accessTokenSecret) = ResolveTokens(sessionId);
        if (accessToken == null || accessTokenSecret == null)
        {
            throw new InvalidOperationException("Brak autoryzacji OAuth — autoryzuj aplikację najpierw.");
        }

        var queryParams = new Dictionary<string, string>
        {
            { "file", "matches" },
            { "teamId", teamId.ToString() },
            { "version", "2.8" }
        };
        var xml = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);
        var doc = XDocument.Parse(xml);

        var upcoming = doc.Descendants("Match")
            .Select(m => new
            {
                Element = m,
                Status = m.Element("Status")?.Value ?? "",
                Date = ParseDate(m.Element("MatchDate")?.Value)
            })
            .Where(x => x.Status.Equals("UPCOMING", StringComparison.OrdinalIgnoreCase) || x.Date > DateTime.UtcNow)
            .OrderBy(x => x.Date)
            .FirstOrDefault();

        if (upcoming == null) return null;

        var home = upcoming.Element.Element("HomeTeam");
        var away = upcoming.Element.Element("AwayTeam");
        var homeId = int.Parse(home?.Element("HomeTeamID")?.Value ?? "0");
        var awayId = int.Parse(away?.Element("AwayTeamID")?.Value ?? "0");
        var isHome = homeId == teamId;
        var opponent = isHome ? away : home;
        var opponentId = isHome ? awayId : homeId;
        var opponentName = isHome
            ? (away?.Element("AwayTeamName")?.Value ?? "")
            : (home?.Element("HomeTeamName")?.Value ?? "");

        return new NextOpponentInfo
        {
            MatchId = long.Parse(upcoming.Element.Element("MatchID")?.Value ?? "0"),
            OpponentTeamId = opponentId,
            OpponentTeamName = opponentName,
            MatchDate = upcoming.Date,
            MatchType = upcoming.Element.Element("MatchType")?.Value ?? "",
            IsHomeMatch = isHome
        };
    }

    public async Task<TeamMatchStats> GetTeamMatchStatsAsync(int teamId, string? sessionId = null)
    {
        var (accessToken, accessTokenSecret) = ResolveTokens(sessionId);
        if (accessToken == null || accessTokenSecret == null)
        {
            return GenerateMockTeamMatchStats(teamId);
        }

        try
        {
            // Pobierz mecze druyny
            var matches = await GetTeamMatchesAsync(teamId, accessToken, accessTokenSecret);
            var teamStats = new TeamMatchStats
            {
                TeamId = teamId,
                TeamName = $"Team {teamId}",
                MatchHistory = matches,
                Statistics = new TeamStatisticsSummary()
            };

            // Oblicz statystyki
            teamStats.CalculateStatistics();
            return teamStats;
        }
        catch
        {
            return GenerateMockTeamMatchStats(teamId);
        }
    }

    public async Task<TeamRatings> GetOpponentRatingsAsync(int teamId, int matchId, string? sessionId = null)
    {
        var (accessToken, accessTokenSecret) = ResolveTokens(sessionId);
        if (accessToken == null || accessTokenSecret == null)
        {
            return GenerateMockRatings(teamId);
        }

        try
        {
            var queryParams = new Dictionary<string, string>
            {
                { "file", "matchdetails" },
                { "matchId", matchId.ToString() },
                { "version", "3.1" }
            };
            var response = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);
            var doc = XDocument.Parse(response);
            return ParseTeamRatings(doc, teamId);
        }
        catch
        {
            return GenerateMockRatings(teamId);
        }
    }

    private (string? AccessToken, string? AccessTokenSecret) ResolveTokens(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            var session = OAuthController.GetSession(sessionId);
            if (session?.AccessToken != null && session.AccessTokenSecret != null)
            {
                return (session.AccessToken, session.AccessTokenSecret);
            }
        }

        var stored = _tokenStore.Get();
        if (stored != null && !string.IsNullOrEmpty(stored.AccessToken))
        {
            return (stored.AccessToken, stored.AccessTokenSecret);
        }
        return (null, null);
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        return null;
    }

    private Player ParsePlayer(XElement element)
    {
        var playerId = int.Parse(element.Element("PlayerID")?.Value ?? "0");
        
        // Pobierz podstawowe dane zawodnika
        var player = new Player
        {
            PlayerId = playerId,
            FirstName = element.Element("FirstName")?.Value ?? "",
            LastName = element.Element("LastName")?.Value ?? "",
            Age = int.Parse(element.Element("Age")?.Value ?? "17"),
            TSI = int.Parse(element.Element("TSI")?.Value ?? "1000"),
            Form = int.Parse(element.Element("PlayerForm")?.Value ?? "5"),
            Stamina = int.Parse(element.Element("StaminaSkill")?.Value ?? "5"),
            Experience = int.Parse(element.Element("Experience")?.Value ?? "3"),
            ShirtNumber = int.Parse(element.Element("PlayerNumber")?.Value ?? "0"),
            InjuryLevel = int.Parse(element.Element("InjuryLevel")?.Value ?? "0"),
            Specialty = element.Element("Specialty")?.Value ?? "",
            Loyalty = int.Parse(element.Element("Loyalty")?.Value ?? "0"),
            Leadership = int.Parse(element.Element("Leadership")?.Value ?? "0"),
            Skills = new PlayerSkills
            {
                Keeper = int.Parse(element.Element("KeeperSkill")?.Value ?? "0"),
                Defending = int.Parse(element.Element("DefenderSkill")?.Value ?? "0"),
                Playmaking = int.Parse(element.Element("PlaymakerSkill")?.Value ?? "0"),
                Winger = int.Parse(element.Element("WingerSkill")?.Value ?? "0"),
                Passing = int.Parse(element.Element("PassingSkill")?.Value ?? "0"),
                Scoring = int.Parse(element.Element("ScorerSkill")?.Value ?? "0"),
                SetPieces = int.Parse(element.Element("SetPiecesSkill")?.Value ?? "0")
            }
        };

        // Podstawowe statystyki - career + bieżący sezon. Per-mecz dane są później
        // uzupełniane przez EnrichPlayersWithMatchStatsAsync (z matchlineup).
        var careerGoals = int.Parse(element.Element("CareerGoals")?.Value ?? "0");
        var leagueGoals = int.Parse(element.Element("LeagueGoals")?.Value ?? "0");
        var cupGoals = int.Parse(element.Element("CupGoals")?.Value ?? "0");
        var friendlyGoals = int.Parse(element.Element("FriendliesGoals")?.Value ?? "0");
        var seasonGoals = leagueGoals + cupGoals + friendlyGoals;
        var totalGoals = careerGoals > 0 ? careerGoals : seasonGoals;

        var lastMatchElement = element.Element("LastMatch");
        var lastMatchRating = lastMatchElement != null
            ? double.Parse(lastMatchElement.Element("Rating")?.Value ?? "0", CultureInfo.InvariantCulture)
            : 0;

        player.MatchStats = new PlayerMatchStats
        {
            TotalMatches = 0,
            Goals = totalGoals,
            Assists = 0, // API Hattrick nie udostępnia asyst
            YellowCards = int.Parse(element.Element("Cards")?.Value ?? "0"),
            RedCards = 0,
            AverageRating = lastMatchRating,
            AverageForm = player.Form,
            GoalsPerMatch = 0,
            MatchesPerGoal = 0,
            MinutesPlayed = 0
        };

        return player;
    }

    private TeamRatings ParseTeamRatings(XDocument doc, int teamId)
    {
        var teamElement = doc.Descendants("Team")
            .FirstOrDefault(t => int.Parse(t.Element("TeamID")?.Value ?? "0") == teamId);

        if (teamElement == null)
            return GenerateMockRatings(teamId);

        return new TeamRatings
        {
            MidfieldRating = int.Parse(teamElement.Element("MidfieldRating")?.Value ?? "0"),
            RightDefenseRating = int.Parse(teamElement.Element("RightDefense")?.Value ?? "0"),
            CentralDefenseRating = int.Parse(teamElement.Element("CentralDefense")?.Value ?? "0"),
            LeftDefenseRating = int.Parse(teamElement.Element("LeftDefense")?.Value ?? "0"),
            RightAttackRating = int.Parse(teamElement.Element("RightAttack")?.Value ?? "0"),
            CentralAttackRating = int.Parse(teamElement.Element("CentralAttack")?.Value ?? "0"),
            LeftAttackRating = int.Parse(teamElement.Element("LeftAttack")?.Value ?? "0")
        };
    }

    private List<Player> GenerateMockPlayers()
    {
        var random = new Random();
        var players = new List<Player>();

        for (int i = 1; i <= 18; i++)
        {
            var totalMatches = random.Next(10, 150);
            var goals = i >= 9 && i <= 13 ? random.Next(5, 50) : random.Next(0, 15);
            var assists = random.Next(0, 30);
            
            players.Add(new Player
            {
                PlayerId = i,
                FirstName = $"Player{i}",
                LastName = $"Surname{i}",
                Age = random.Next(17, 35),
                TSI = random.Next(1000, 100000),
                Form = random.Next(1, 9),
                Stamina = random.Next(1, 9),
                Experience = random.Next(1, 9),
                ShirtNumber = i,
                InjuryLevel = random.Next(0, 10) > 8 ? random.Next(1, 4) : 0,
                Skills = new PlayerSkills
                {
                    Keeper = i == 1 ? random.Next(5, 15) : 0,
                    Defending = i <= 6 ? random.Next(4, 14) : random.Next(1, 8),
                    Playmaking = random.Next(2, 12),
                    Winger = (i >= 3 && i <= 5) || (i >= 9 && i <= 11) ? random.Next(4, 13) : random.Next(1, 7),
                    Passing = random.Next(2, 11),
                    Scoring = i >= 9 && i <= 13 ? random.Next(5, 15) : random.Next(1, 8),
                    SetPieces = random.Next(1, 10)
                },
                MatchStats = new PlayerMatchStats
                {
                    TotalMatches = totalMatches,
                    Goals = goals,
                    Assists = assists,
                    YellowCards = random.Next(0, 15),
                    RedCards = random.Next(0, 3),
                    AverageRating = Math.Round(random.NextDouble() * 3 + 5, 1),
                    AverageForm = random.Next(1, 9),
                    GoalsPerMatch = totalMatches > 0 ? Math.Round((double)goals / totalMatches, 2) : 0,
                    MatchesPerGoal = goals > 0 ? Math.Round((double)totalMatches / goals, 1) : 0,
                    MinutesPlayed = totalMatches * random.Next(60, 90)
                }
            });
        }

        return players;
    }

    private async Task<List<MatchRecord>> GetTeamMatchesAsync(int teamId, string accessToken, string accessTokenSecret)
    {
        var queryParams = new Dictionary<string, string>
        {
            { "file", "matches" },
            { "teamId", teamId.ToString() },
            { "version", "2.8" }
        };
        
        var response = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);
        var doc = XDocument.Parse(response);
        var matches = new List<MatchRecord>();

        foreach (var matchElement in doc.Descendants("Match"))
        {
            var status = matchElement.Element("Status")?.Value ?? "";
            if (status.Equals("FINISHED", StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(await ParseMatchRecord(matchElement, teamId, accessToken, accessTokenSecret));
            }
        }

        return matches.OrderByDescending(m => m.MatchDate).ToList();
    }

    private async Task<MatchRecord> ParseMatchRecord(XElement element, int teamId, string accessToken, string accessTokenSecret)
    {
        var home = element.Element("HomeTeam");
        var away = element.Element("AwayTeam");
        var homeId = int.Parse(home?.Element("HomeTeamID")?.Value ?? "0");
        var awayId = int.Parse(away?.Element("AwayTeamID")?.Value ?? "0");
        var isHome = homeId == teamId;
        var opponent = isHome ? away : home;
        var opponentId = isHome ? awayId : homeId;
        var opponentName = isHome
            ? (away?.Element("AwayTeamName")?.Value ?? "")
            : (home?.Element("HomeTeamName")?.Value ?? "");

        var homeGoals = int.Parse(element.Element("HomeGoals")?.Value ?? "0");
        var awayGoals = int.Parse(element.Element("AwayGoals")?.Value ?? "0");
        var goalsFor = isHome ? homeGoals : awayGoals;
        var goalsAgainst = isHome ? awayGoals : homeGoals;
        var teamPoints = (goalsFor > goalsAgainst) ? 3 : (goalsFor == goalsAgainst) ? 1 : 0;

        // Pobierz formacje i inne statystyki z matchdetails API
        var matchDetails = await GetMatchDetails(element.Element("MatchID")?.Value, teamId, accessToken, accessTokenSecret);

        return new MatchRecord
        {
            MatchId = long.Parse(element.Element("MatchID")?.Value ?? "0"),
            MatchDate = DateTime.Parse(element.Element("MatchDate")?.Value ?? DateTime.MinValue.ToString()),
            MatchType = element.Element("MatchType")?.Value ?? "",
            OpponentTeam = opponentName,
            IsHomeMatch = isHome,
            GoalsFor = goalsFor,
            GoalsAgainst = goalsAgainst,
            TeamPoints = teamPoints,
            MatchResult = $"{goalsFor}-{goalsAgainst}",
            FormationUsed = matchDetails?.Formation ?? "4-4-2",
            // Prawdziwe oceny meczowe z matchdetails
            MidfieldRating = matchDetails?.MidfieldRating ?? 0,
            RightDefenseRating = matchDetails?.RightDefenseRating ?? 0,
            CentralDefenseRating = matchDetails?.CentralDefenseRating ?? 0,
            LeftDefenseRating = matchDetails?.LeftDefenseRating ?? 0,
            RightAttackRating = matchDetails?.RightAttackRating ?? 0,
            CentralAttackRating = matchDetails?.CentralAttackRating ?? 0,
            LeftAttackRating = matchDetails?.LeftAttackRating ?? 0,
            Possession = matchDetails?.Possession ?? 0,
            Attitude = matchDetails?.Attitude ?? "Normal",
            TeamSpirit = matchDetails?.TeamSpirit ?? "calm"
        };
    }

    private async Task<MatchDetails?> GetMatchDetails(string? matchId, int teamId, string accessToken, string accessTokenSecret)
    {
        if (string.IsNullOrEmpty(matchId)) return null;

        try
        {
            var queryParams = new Dictionary<string, string>
            {
                { "file", "matchlineup" },
                { "matchId", matchId },
                { "version", "2.1" }
            };

            var response = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);
            var doc = XDocument.Parse(response);

            // Hattrick matchlineup: <Team><TeamID> (dwa elementy - jeden na drużynę)
            var myTeamElement = doc.Descendants("Team")
                .FirstOrDefault(t => int.TryParse(t.Element("TeamID")?.Value, out int id) && id == teamId);

            if (myTeamElement == null) return null;

            // StartingLineup zawiera 11 graczy startowych
            var startingLineup = myTeamElement.Element("StartingLineup");
            var players = startingLineup?.Descendants("Player").ToList() ?? new List<XElement>();

            // Tylko główne pozycje (RoleID 100-113)
            var mainPlayers = players.Where(p => IsMainPlayer(p.Element("RoleID")?.Value ?? "")).ToList();

            // MatchRoleID (Hattrick API):
            // 101=Right back, 102=Right CD, 103=Middle CD, 104=Left CD, 105=Left back
            // 106=Right winger, 107=Right IM, 108=Middle IM, 109=Left IM, 110=Left winger
            // 111=Right forward, 112=Middle forward, 113=Left forward
            var defenders = mainPlayers.Count(p =>
            {
                var rId = p.Element("RoleID")?.Value ?? "";
                return rId is "101" or "102" or "103" or "104" or "105";
            });
            var midfielders = mainPlayers.Count(p =>
            {
                var rId = p.Element("RoleID")?.Value ?? "";
                return rId is "106" or "107" or "108" or "109" or "110";
            });
            var forwards = mainPlayers.Count(p =>
            {
                var rId = p.Element("RoleID")?.Value ?? "";
                return rId is "111" or "112" or "113";
            });
            var wingers = mainPlayers.Count(p =>
            {
                var rId = p.Element("RoleID")?.Value ?? "";
                return rId is "106" or "110";
            });

            var formation = DetectFormation(defenders, midfielders, wingers, forwards);

            // Oceny mogą być w elemencie Rating lub bezpośrednio w elemencie drużyny
            var ratings = myTeamElement.Element("Rating");
            var midfieldRating = int.Parse(ratings?.Element("Midfield")?.Value ?? "0");
            var rightDefense = int.Parse(ratings?.Element("RightDefense")?.Value ?? "0");
            var centralDefense = int.Parse(ratings?.Element("CentralDefense")?.Value ?? "0");
            var leftDefense = int.Parse(ratings?.Element("LeftDefense")?.Value ?? "0");
            var rightAttack = int.Parse(ratings?.Element("RightAttack")?.Value ?? "0");
            var centralAttack = int.Parse(ratings?.Element("CentralAttack")?.Value ?? "0");
            var leftAttack = int.Parse(ratings?.Element("LeftAttack")?.Value ?? "0");

            var arenaElement = doc.Descendants("Arena").FirstOrDefault();
            var possession = int.Parse(arenaElement?.Element("Possession")?.Value ?? "0");
            var attitude = myTeamElement.Element("Attitude")?.Value ?? "Normal";
            var teamSpirit = myTeamElement.Element("TeamSpirit")?.Value ?? "calm";

            return new MatchDetails
            {
                Formation = formation,
                MidfieldRating = midfieldRating,
                RightDefenseRating = rightDefense,
                CentralDefenseRating = centralDefense,
                LeftDefenseRating = leftDefense,
                RightAttackRating = rightAttack,
                CentralAttackRating = centralAttack,
                LeftAttackRating = leftAttack,
                Possession = possession,
                Attitude = attitude,
                TeamSpirit = teamSpirit
            };
        }
        catch
        {
            return null;
        }
    }

    private static string DetectFormation(int defenders, int midfielders, int wingers, int forwards)
    {
        // Po naprawieniu MatchRoleID (110=Left winger, nie forward) liczniki są poprawne.
        // Fallback $"{d}-{m}-{f}" zwraca poprawną nazwę dla wszystkich formacji Hattrick.
        return $"{defenders}-{midfielders}-{forwards}";
    }

    private TeamMatchStats GenerateMockTeamMatchStats(int teamId)
    {
        var random = new Random(teamId);
        var matches = new List<MatchRecord>();
        var formations = new[] { "4-4-2", "3-5-2", "4-3-3", "5-4-1", "3-4-3", "4-5-1", "5-3-2" };
        var matchTypes = new[] { "League", "Cup", "Friendly" };
        var attitudes = new[] { "PIC", "Normal", "MOTS" };
        var spirits = new[] { "calm", "composed", "content", "delirious", "satisfied", "walking on clouds" };

        // Generuj 20 ostatnich meczów
        for (int i = 0; i < 20; i++)
        {
            var isHome = random.Next(0, 2) == 1;
            var goalsFor = random.Next(0, 6);
            var goalsAgainst = random.Next(0, 6);
            var teamPoints = (goalsFor > goalsAgainst) ? 3 : (goalsFor == goalsAgainst) ? 1 : 0;
            var formation = formations[random.Next(formations.Length)];

            matches.Add(new MatchRecord
            {
                MatchId = 1000000 + i,
                MatchDate = DateTime.Now.AddDays(-i * 7),
                MatchType = matchTypes[random.Next(matchTypes.Length)],
                OpponentTeam = $"Opponent {i + 1}",
                IsHomeMatch = isHome,
                GoalsFor = goalsFor,
                GoalsAgainst = goalsAgainst,
                TeamPoints = teamPoints,
                MatchResult = $"{goalsFor}-{goalsAgainst}",
                FormationUsed = formation,
                MidfieldRating = random.Next(20, 80),
                RightDefenseRating = random.Next(15, 70),
                CentralDefenseRating = random.Next(20, 75),
                LeftDefenseRating = random.Next(15, 70),
                RightAttackRating = random.Next(10, 60),
                CentralAttackRating = random.Next(15, 65),
                LeftAttackRating = random.Next(10, 60),
                Possession = random.Next(30, 70),
                Attitude = attitudes[random.Next(attitudes.Length)],
                TeamSpirit = spirits[random.Next(spirits.Length)]
            });
        }

        var teamStats = new TeamMatchStats
        {
            TeamId = teamId,
            TeamName = $"Team {teamId}",
            MatchHistory = matches,
            Statistics = new TeamStatisticsSummary()
        };

        teamStats.CalculateStatistics();
        return teamStats;
    }

    private TeamRatings GenerateMockRatings(int teamId)
    {
        // Sprawd czy mamy ju wygenerowane wartooci dla tego teamId
        if (_mockRatingsCache.TryGetValue(teamId, out var cachedRatings))
        {
            return cachedRatings;
        }

        // Uyj teamId jako seed dla Random, aby wartooci byy deterministyczne
        var random = new Random(teamId);
        var ratings = new TeamRatings
        {
            MidfieldRating = random.Next(30, 80),
            RightDefenseRating = random.Next(20, 60),
            CentralDefenseRating = random.Next(25, 70),
            LeftDefenseRating = random.Next(20, 60),
            RightAttackRating = random.Next(15, 50),
            CentralAttackRating = random.Next(20, 60),
            LeftAttackRating = random.Next(15, 50)
        };

        // Cachuj wygenerowane wartooci
        _mockRatingsCache[teamId] = ratings;
        return ratings;
    }

    private string GetPositionName(string roleId)
    {
        return roleId switch
        {
            "100" => "Keeper",
            "101" => "Right Back",
            "102" => "Right Central Defender",
            "103" => "Middle Central Defender",
            "104" => "Left Central Defender",
            "105" => "Left Back",
            "106" => "Right Winger",
            "107" => "Right Inner Midfield",
            "108" => "Middle Inner Midfield",
            "109" => "Left Inner Midfield",
            "110" => "Left Winger",
            "111" => "Right Forward",
            "112" => "Middle Forward",
            "113" => "Left Forward",
            _ => "Unknown"
        };
    }

    private static bool IsMainPlayer(string roleId)
    {
        // Główne pozycje w meczu: 100 (GK) do 113 (Left forward)
        // Wszystkie inne (17,18,19-35,114-213) to role specjalne lub zastępcy
        return int.TryParse(roleId, out int id) && id >= 100 && id <= 113;
    }

    public async Task<Dictionary<string, int>> GetFormationExperienceAsync(int teamId, string? sessionId = null)
    {
        var (accessToken, accessTokenSecret) = ResolveTokens(sessionId);
        if (accessToken == null || accessTokenSecret == null)
        {
            return GetDefaultFormationExperience();
        }

        try
        {
            var queryParams = new Dictionary<string, string>
            {
                { "file", "training" },
                { "teamId", teamId.ToString() },
                { "version", "2.2" }
            };
            var response = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);
            var doc = XDocument.Parse(response);

            var teamElement = doc.Descendants("Team").FirstOrDefault();
            if (teamElement == null)
            {
                return GetDefaultFormationExperience();
            }

            var formationExperience = new Dictionary<string, int>();

            // Mapowanie nazw XML na nazwy formacji
            var formationMapping = new Dictionary<string, string>
            {
                { "Experience442", "4-4-2" },
                { "Experience433", "4-3-3" },
                { "Experience451", "4-5-1" },
                { "Experience352", "3-5-2" },
                { "Experience532", "5-3-2" },
                { "Experience343", "3-4-3" },
                { "Experience541", "5-4-1" },
                { "Experience523", "5-2-3" },
                { "Experience550", "5-5-0" },
                { "Experience253", "2-5-3" }
            };

            foreach (var mapping in formationMapping)
            {
                var expElement = teamElement.Element(mapping.Key);
                if (expElement != null && int.TryParse(expElement.Value, out int expValue))
                {
                    formationExperience[mapping.Value] = expValue;
                }
                else
                {
                    formationExperience[mapping.Value] = 6; // Domyślna wartość (znośne)
                }
            }

            return formationExperience;
        }
        catch
        {
            return GetDefaultFormationExperience();
        }
    }

    private static Dictionary<string, int> GetDefaultFormationExperience()
    {
        return new Dictionary<string, int>
        {
            { "5-5-0", 6 },
            { "5-4-1", 6 },
            { "5-3-2", 6 },
            { "4-5-1", 6 },
            { "4-4-2", 6 },
            { "4-3-3", 6 },
            { "3-5-2", 6 },
            { "3-4-3", 6 },
            { "2-5-3", 6 }
        };
    }

}
