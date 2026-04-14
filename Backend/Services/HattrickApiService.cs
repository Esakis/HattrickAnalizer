using System.Globalization;
using System.Xml.Linq;
using System.Diagnostics;
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
            return players;
        }
        catch
        {
            return GenerateMockPlayers();
        }
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
        return new Player
        {
            PlayerId = int.Parse(element.Element("PlayerID")?.Value ?? "0"),
            FirstName = element.Element("FirstName")?.Value ?? "",
            LastName = element.Element("LastName")?.Value ?? "",
            Age = int.Parse(element.Element("Age")?.Value ?? "17"),
            TSI = int.Parse(element.Element("TSI")?.Value ?? "1000"),
            Form = int.Parse(element.Element("PlayerForm")?.Value ?? "5"),
            Stamina = int.Parse(element.Element("StaminaSkill")?.Value ?? "5"),
            Experience = int.Parse(element.Element("Experience")?.Value ?? "3"),
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
                Skills = new PlayerSkills
                {
                    Keeper = i == 1 ? random.Next(5, 15) : 0,
                    Defending = i <= 6 ? random.Next(4, 14) : random.Next(1, 8),
                    Playmaking = random.Next(2, 12),
                    Winger = (i >= 3 && i <= 5) || (i >= 9 && i <= 11) ? random.Next(4, 13) : random.Next(1, 7),
                    Passing = random.Next(2, 11),
                    Scoring = i >= 9 && i <= 13 ? random.Next(5, 15) : random.Next(1, 8),
                    SetPieces = random.Next(1, 10)
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

    private async Task<MatchDetails> GetMatchDetails(string? matchId, int teamId, string accessToken, string accessTokenSecret)
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
            
            // DEBUG: Poka cal struktur XML
            Debug.WriteLine($"DEBUG - Full XML for match {matchId}:");
            Debug.WriteLine(response.Substring(0, Math.Min(1000, response.Length)));
            
            // Znajd zespoy w meczu
            var teams = doc.Descendants("Team").ToList();
            Debug.WriteLine($"DEBUG - Found {teams.Count} teams in match {matchId}");
            
            // Przetwarzaj oba zespo - mój i przeciwnika
            foreach (var team in teams)
            {
                var isMyTeam = int.Parse(team.Element("TeamID")?.Value ?? "0") == teamId;
                Debug.WriteLine($"DEBUG - Processing team: {team.Element("TeamName")?.Value}, IsMyTeam: {isMyTeam}");
                
                // Pobierz oceny zespou
                var ratings = team.Element("Rating");
                var midfieldRating = int.Parse(ratings?.Element("Midfield")?.Value ?? "0");
                var rightDefense = int.Parse(ratings?.Element("RightDefense")?.Value ?? "0");
                var centralDefense = int.Parse(ratings?.Element("CentralDefense")?.Value ?? "0");
                var leftDefense = int.Parse(ratings?.Element("LeftDefense")?.Value ?? "0");
                var rightAttack = int.Parse(ratings?.Element("RightAttack")?.Value ?? "0");
                var centralAttack = int.Parse(ratings?.Element("CentralAttack")?.Value ?? "0");
                var leftAttack = int.Parse(ratings?.Element("LeftAttack")?.Value ?? "0");
                
                // Pobierz pozycje i wykryj formacj
                var lineupElement = team.Element("Lineup");
                Debug.WriteLine($"DEBUG - Lineup element found: {lineupElement != null}");
                var formation = "4-4-2"; // domylna
                
                if (lineupElement != null)
                {
                    var positions = lineupElement.Descendants("Player").ToList();
                    Debug.WriteLine($"DEBUG - Found {positions.Count} players in lineup");
                    
                    // DEBUG: Poka wszystkie RoleID do logowania
                    var roleIds = positions.Select(p => p.Element("RoleID")?.Value ?? "null").ToList();
                    Debug.WriteLine($"DEBUG - Match {matchId} - Team {teamId} - RoleIDs: [{string.Join(", ", roleIds)}]");
                    
                    // DEBUG: Poka wszystkie zawodników z ich pozycjami
                    for (int i = 0; i < positions.Count; i++)
                    {
                        var player = positions[i];
                        var roleId = player.Element("RoleID")?.Value ?? "null";
                        var positionName = GetPositionName(roleId);
                        Debug.WriteLine($"DEBUG - Player {i}: ID={player.Element("PlayerID")?.Value}, Name={player.Element("FirstName")?.Value} {player.Element("LastName")?.Value}, RoleID={roleId}, Position={positionName}");
                    }
                    
                    // DEBUG: Poka ca struktur lineup
                    Debug.WriteLine($"DEBUG - Total players in lineup: {positions.Count}");
                    Debug.WriteLine($"DEBUG - Lineup XML: {lineupElement}");
                    
                    // Prawidlowa detekcja formacji na podstawie RoleID z Hattrick - tylko glowni gracze z rating > 0
                    var mainPlayers = positions.Where(p => IsMainPlayer(p.Element("RoleID")?.Value ?? "") && IsRealPlayer(p)).ToList();
                    
                    var defenders = mainPlayers.Count(p => 
                    {
                        var roleId = p.Element("RoleID")?.Value ?? "";
                        return roleId == "101" || roleId == "102" || roleId == "103" || roleId == "104" || roleId == "105"; // Right back, central defenders, left back
                    });
                    
                    var midfielders = mainPlayers.Count(p => 
                    {
                        var roleId = p.Element("RoleID")?.Value ?? "";
                        return roleId == "106" || roleId == "107" || roleId == "108" || roleId == "109"; // Right winger, inner midfield, left winger
                    });
                    
                    var forwards = mainPlayers.Count(p => 
                    {
                        var roleId = p.Element("RoleID")?.Value ?? "";
                        return roleId == "110" || roleId == "111" || roleId == "113"; // Right forward, central forward, left forward
                    });
                    
                    var wingers = mainPlayers.Count(p => 
                    {
                        var roleId = p.Element("RoleID")?.Value ?? "";
                        return roleId == "106" || roleId == "109"; // Right winger, left winger
                    });
                    
                    // DEBUG: Poka liczniki pozycji
                    Debug.WriteLine($"DEBUG - Formation counts: Defenders={defenders}, Midfielders={midfielders}, Forwards={forwards}, Wingers={wingers}");
                    Debug.WriteLine($"DEBUG - Main players count: {mainPlayers.Count}");
                    
                    // Ulepszona logika wykrywania formacji - dopasowana do rzeczywistych danych Hattrick
                    // Specyficzne reguly z wingers - musza byc pierwsze!
                    if (defenders == 2 && midfielders == 4 && forwards == 3 && wingers == 2) formation = "2-5-3"; // Twoja konfiguracja!
                    else if (defenders == 3 && midfielders == 3 && forwards == 2 && wingers == 2) formation = "3-3-2";
                    else if (defenders == 4 && midfielders == 3 && forwards == 3 && wingers == 2) formation = "4-3-3";
                    else if (defenders == 5 && midfielders == 1 && forwards == 3 && wingers == 1) formation = "5-1-3";
                    else if (defenders == 4 && midfielders == 2 && forwards == 3 && wingers == 2) formation = "4-2-3";
                    else if (defenders == 3 && midfielders == 4 && forwards == 3 && wingers == 2) formation = "3-4-3";
                    else if (defenders == 1 && midfielders == 3 && forwards == 5 && wingers == 2) formation = "1-3-5-2";
                    else if (defenders == 1 && midfielders == 4 && forwards == 4 && wingers == 1) formation = "1-4-4-1";
                    else if (defenders == 1 && midfielders == 5 && forwards == 3 && wingers == 1) formation = "1-5-3-1";
                    else if (defenders == 2 && midfielders == 3 && forwards == 5 && wingers == 0) formation = "2-3-5";
                    else if (defenders == 2 && midfielders == 5 && forwards == 3 && wingers == 0) formation = "2-5-3";
                    else if (defenders == 3 && midfielders == 2 && forwards == 5 && wingers == 0) formation = "3-2-5";
                    else if (defenders == 3 && midfielders == 5 && forwards == 2 && wingers == 0) formation = "3-5-2";
                    
                    // Ogolne reguly - bez wingers
                    else if (defenders == 4 && midfielders == 4 && forwards == 2) formation = "4-4-2";
                    else if (defenders == 3 && midfielders == 3 && forwards == 2) formation = "3-3-2";
                    else if (defenders == 4 && midfielders == 3 && forwards == 3) formation = "4-3-3";
                    else if (defenders == 3 && midfielders == 4 && forwards == 3) formation = "3-4-3";
                    else if (defenders == 5 && midfielders == 3 && forwards == 2) formation = "5-3-2";
                    else if (defenders == 2 && midfielders == 4 && forwards == 3) formation = "2-4-5";
                    else if (defenders == 2 && midfielders == 4 && forwards == 4) formation = "2-4-4";
                    else if (defenders == 5 && midfielders == 1 && forwards == 3) formation = "5-1-3";
                    else if (defenders == 5 && midfielders == 1 && forwards == 4) formation = "5-1-4";
                    else if (defenders == 3 && midfielders == 5 && forwards == 2) formation = "3-5-2";
                    else if (defenders == 4 && midfielders == 5 && forwards == 1) formation = "4-5-1";
                    else if (defenders == 5 && midfielders == 4 && forwards == 1) formation = "5-4-1";
                    else formation = $"{defenders}-{midfielders}-{forwards}"; // fallback
                    
                    Debug.WriteLine($"DEBUG - Formation detected: {formation}");
                }
                
                // Pobierz pozycje (possession)
                var arenaElement = doc.Descendants("Arena").FirstOrDefault();
                var possession = int.Parse(arenaElement?.Element("Possession")?.Value ?? "0");
                
                // Pobierz attitude i team spirit
                var attitude = team.Element("Attitude")?.Value ?? "Normal";
                var teamSpirit = team.Element("TeamSpirit")?.Value ?? "calm";
                
                // DEBUG: Pokaz formacje dla obu zespolow
                Debug.WriteLine($"DEBUG - Team {team.Element("TeamName")?.Value} - Formation: {formation}");
                
                // Zwró dane dla mojego zespoju (tylko dla niego potrzebujemy szczególow)
                if (isMyTeam)
                {
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
            }
        }
        catch
        {
            // W razie bdu zwr null
        }
        
        return null;
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
            "102" => "Central Defender", 
            "103" => "Central Defender",
            "104" => "Central Defender",
            "105" => "Left Back",
            "106" => "Right Winger",
            "107" => "Inner Midfield",
            "108" => "Inner Midfield", 
            "109" => "Left Winger",
            "110" => "Right Forward",
            "111" => "Central Forward",
            "112" => "Central Forward",
            "113" => "Left Forward",
            "17" => "Captain",
            "18" => "Set Pieces",
            "19" => "Substitute",
            "33" => "Substitute",
            "34" => "Substitute",
            _ => "Unknown"
        };
    }

    private bool IsMainPlayer(string roleId)
    {
        // Filtrowanie tylko roli specjalnych - kapitan, set pieces, zastepcy
        return roleId != "17" && roleId != "18" && roleId != "19" && roleId != "33" && roleId != "34";
    }

    private bool IsRealPlayer(XElement player)
    {
        // Sprawdz czy gracz ma rating > 0 (prawdziwy zawodnik, a nie rezerwa)
        var ratingStars = player.Element("RatingStars")?.Value ?? "0";
        return !string.IsNullOrEmpty(ratingStars) && ratingStars != "0";
    }
}
