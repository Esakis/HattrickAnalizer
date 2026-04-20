using System.Diagnostics;
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
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string _baseUrl;
    private readonly Dictionary<int, TeamRatings> _mockRatingsCache = new();

    public HattrickApiService(HttpClient httpClient, IConfiguration configuration, OAuthService oauthService, TokenStore tokenStore, IHttpContextAccessor httpContextAccessor)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _oauthService = oauthService;
        _tokenStore = tokenStore;
        _httpContextAccessor = httpContextAccessor;
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
        Debug.WriteLine($"[GetTeamPlayersAsync] Called for team {teamId}");
        
        var (accessToken, accessTokenSecret) = ResolveTokens(sessionId);
        if (accessToken == null || accessTokenSecret == null)
        {
            Debug.WriteLine($"No OAuth tokens available - returning mock players");
            return GenerateMockPlayers();
        }

        // Pobierz graczy z API players (daje imiona, umiejętności, formę itd.)
        var playersFromApi = new Dictionary<int, Player>();
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
            foreach (var playerElement in doc.Descendants("Player"))
            {
                var p = ParsePlayer(playerElement);
                playersFromApi[p.PlayerId] = p;
            }
            Debug.WriteLine($"[GetTeamPlayersAsync] Got {playersFromApi.Count} players from API for team {teamId}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GetTeamPlayersAsync] Players API failed for team {teamId}: {ex.Message}");
        }

        // Pobierz graczy z matchlineup XML (daje prawdziwe PlayerID z meczów + oceny)
        try
        {
            var matchPlayers = await GetTeamPlayersFromMatchesAsync(teamId, accessToken!, accessTokenSecret!);
            Debug.WriteLine($"[GetTeamPlayersAsync] Got {matchPlayers.Count} players from matches for team {teamId}");
            
            if (matchPlayers.Count > 0)
            {
                // Usuń sprzedanych graczy — zostawiamy tylko tych, którzy są w aktualnym składzie
                if (playersFromApi.Count > 0)
                {
                    matchPlayers = matchPlayers.Where(mp => playersFromApi.ContainsKey(mp.PlayerId)).ToList();
                    Debug.WriteLine($"[GetTeamPlayersAsync] After filtering sold players: {matchPlayers.Count} players");
                }

                // Uzupełnij dane z API players (imiona, skille, forma) jeśli dostępne
                foreach (var mp in matchPlayers)
                {
                    if (playersFromApi.TryGetValue(mp.PlayerId, out var apiPlayer))
                    {
                        // Weź dane personalne z API, a statystyki z lineup
                        mp.Skills = apiPlayer.Skills;
                        mp.Form = apiPlayer.Form;
                        mp.Stamina = apiPlayer.Stamina;
                        mp.Experience = apiPlayer.Experience;
                        mp.Loyalty = apiPlayer.Loyalty;
                        mp.Leadership = apiPlayer.Leadership;
                        mp.Specialty = apiPlayer.Specialty;
                        mp.InjuryLevel = apiPlayer.InjuryLevel;
                        mp.ShirtNumber = apiPlayer.ShirtNumber;
                        mp.TSI = apiPlayer.TSI;
                        mp.Age = apiPlayer.Age;

                        if (mp.MatchStats != null && apiPlayer.MatchStats != null)
                        {
                            mp.MatchStats.Goals = apiPlayer.MatchStats.Goals;
                            mp.MatchStats.YellowCards = apiPlayer.MatchStats.YellowCards;
                            mp.MatchStats.AverageForm = apiPlayer.Form;
                        }
                    }
                }

                // Dodaj graczy z aktualnego składu, którzy nie grali w ostatnich meczach (np. nowi, kontuzjowani)
                if (playersFromApi.Count > 0)
                {
                    var matchPlayerIds = new HashSet<int>(matchPlayers.Select(p => p.PlayerId));
                    foreach (var apiPlayer in playersFromApi.Values)
                    {
                        if (!matchPlayerIds.Contains(apiPlayer.PlayerId))
                        {
                            matchPlayers.Add(apiPlayer);
                        }
                    }
                    Debug.WriteLine($"[GetTeamPlayersAsync] After adding non-played current players: {matchPlayers.Count} players");
                }

                return matchPlayers;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GetTeamPlayersAsync] Match lineup extraction failed for team {teamId}: {ex.Message}");
        }

        // Fallback: zwróć graczy z API bez statystyk meczowych
        if (playersFromApi.Count > 0)
        {
            return playersFromApi.Values.ToList();
        }

        return GenerateMockPlayers();
    }

    private async Task<List<Player>> GetTeamPlayersFromMatchesAsync(int teamId, string accessToken, string accessTokenSecret)
    {
        Debug.WriteLine($"[GetTeamPlayersFromMatchesAsync] Fetching players for team {teamId} from match data...");
        
        // Pobierz mecze drużyny (publiczne API)
        var matchQuery = new Dictionary<string, string>
        {
            { "file", "matches" },
            { "teamId", teamId.ToString() },
            { "version", "2.8" }
        };
        var matchesXml = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, matchQuery);
        var matchesDoc = XDocument.Parse(matchesXml);

        var finishedMatchIds = matchesDoc.Descendants("Match")
            .Where(m => 
            {
                var status = m.Element("Status")?.Value ?? "";
                var matchType = int.Parse(m.Element("MatchType")?.Value ?? "0");
                var homeTeamId = int.Parse(m.Element("HomeTeam")?.Element("HomeTeamID")?.Value ?? "0");
                var awayTeamId = int.Parse(m.Element("AwayTeam")?.Element("AwayTeamID")?.Value ?? "0");
                return status.Equals("FINISHED", StringComparison.OrdinalIgnoreCase) 
                    && matchType >= 1 && matchType <= 12
                    && (homeTeamId == teamId || awayTeamId == teamId);
            })
            .Select(m => m.Element("MatchID")?.Value)
            .Where(id => !string.IsNullOrEmpty(id))
            .Take(10)
            .ToList();

        Debug.WriteLine($"[GetTeamPlayersFromMatchesAsync] Found {finishedMatchIds.Count} senior matches for team {teamId}");
        if (finishedMatchIds.Count == 0) return new List<Player>();

        // Pobierz składy z meczów (publiczne API)
        var playerDict = new Dictionary<int, Player>();
        var appearances = new Dictionary<int, PlayerAppearanceAggregate>();

        foreach (var matchId in finishedMatchIds)
        {
            var lineupXml = await FetchMatchLineupAsync(matchId!, teamId, accessToken, accessTokenSecret);
            if (string.IsNullOrEmpty(lineupXml))
            {
                Debug.WriteLine($"[GetTeamPlayersFromMatchesAsync] Empty lineup XML for match {matchId}");
                continue;
            }

            try
            {
                ExtractPlayersFromLineup(lineupXml, teamId, playerDict, appearances);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetTeamPlayersFromMatchesAsync] EXCEPTION for match {matchId}: {ex.Message}");
            }
        }

        Debug.WriteLine($"[GetTeamPlayersFromMatchesAsync] Extracted {playerDict.Count} unique players");

        // Oblicz średnie oceny dla każdego gracza
        foreach (var player in playerDict.Values)
        {
            if (appearances.TryGetValue(player.PlayerId, out var agg) && agg.Matches > 0)
            {
                var stats = new PlayerMatchStats
                {
                    TotalMatches = agg.Matches,
                    Goals = agg.Goals,
                    MinutesPlayed = agg.Minutes,
                    AverageRating = Math.Round(agg.RatingSum / agg.Matches, 2),
                    AverageForm = player.Form,
                    GoalsPerMatch = agg.Matches > 0 ? Math.Round((double)agg.Goals / agg.Matches, 2) : 0,
                    MatchesPerGoal = agg.Goals > 0 ? Math.Round((double)agg.Matches / agg.Goals, 1) : 0
                };

                // Oblicz średnie oceny na pozycjach
                foreach (var kvp in agg.PositionRatings)
                {
                    if (kvp.Value.Count > 0)
                    {
                        stats.PositionRatings[kvp.Key] = Math.Round(kvp.Value.Average(), 2);
                        Debug.WriteLine($"[GetTeamPlayersFromMatchesAsync] Player {player.PlayerId} ({player.FirstName} {player.LastName}): {kvp.Key} = {stats.PositionRatings[kvp.Key]} (from {kvp.Value.Count} matches)");
                    }
                }

                player.MatchStats = stats;
            }
        }

        Debug.WriteLine($"[GetTeamPlayersFromMatchesAsync] Returning {playerDict.Values.Count} players with stats");
        return playerDict.Values.ToList();
    }

    private void ExtractPlayersFromLineup(string lineupXml, int teamId, Dictionary<int, Player> playerDict, Dictionary<int, PlayerAppearanceAggregate> appearances)
    {
        var doc = XDocument.Parse(lineupXml);
        
        // matchlineup XML structure:
        //   <HattrickData>
        //     <HomeTeam><HomeTeamID>...</HomeTeamID></HomeTeam>  (just header)
        //     <AwayTeam><AwayTeamID>...</AwayTeamID></AwayTeam>  (just header)
        //     <Team><TeamID>...</TeamID><StartingLineup><Player>...</Player></StartingLineup></Team>  (actual lineup)
        // Players are inside <Team>, NOT inside <HomeTeam>/<AwayTeam>
        
        var teamElement = doc.Descendants("Team")
            .FirstOrDefault(t => t.Element("TeamID")?.Value == teamId.ToString());
        
        if (teamElement == null)
        {
            // The API returns lineup for the requested teamId, so <Team> should always match
            Debug.WriteLine($"[ExtractPlayersFromLineup] <Team> with TeamID={teamId} not found");
            return;
        }

        // Use Lineup (has PlayedMinutes/Rating) rather than StartingLineup (only has PlayerID/RoleID)
        var lineupContainer = teamElement.Element("Lineup");
        var lineupPlayers = new List<XElement>();
        if (lineupContainer != null)
        {
            lineupPlayers.AddRange(lineupContainer.Elements("Player"));
        }
        // Fallback to StartingLineup + Substitutions if Lineup is empty
        if (lineupPlayers.Count == 0)
        {
            foreach (var containerName in new[] { "StartingLineup", "Substitutions", "Bench" })
            {
                var container = teamElement.Element(containerName);
                if (container != null)
                    lineupPlayers.AddRange(container.Elements("Player"));
            }
        }

        foreach (var playerEl in lineupPlayers)
        {
            if (!int.TryParse(playerEl.Element("PlayerID")?.Value, out int pid)) continue;

            // Skip bench/substitutes (RoleID >= 114) and substitution events (RoleID < 100)
            var roleIdStr = playerEl.Element("RoleID")?.Value ?? "0";
            if (int.TryParse(roleIdStr, out int roleIdInt) && (roleIdInt >= 114 || roleIdInt < 100)) continue;

            // Dodaj gracza do słownika jeśli jeszcze go nie ma
            if (!playerDict.ContainsKey(pid))
            {
                var player = new Player
                {
                    PlayerId = pid,
                    FirstName = playerEl.Element("FirstName")?.Value ?? "",
                    LastName = playerEl.Element("LastName")?.Value ?? "",
                    Age = int.Parse(playerEl.Element("Age")?.Value ?? "17"),
                    Form = int.Parse(playerEl.Element("PlayerForm")?.Value ?? "5"),
                    Stamina = int.Parse(playerEl.Element("StaminaSkill")?.Value ?? "5"),
                    Skills = new PlayerSkills()
                };
                playerDict[pid] = player;
                appearances[pid] = new PlayerAppearanceAggregate();
                Debug.WriteLine($"[ExtractPlayersFromLineup] Added new player: {player.FirstName} {player.LastName} (ID: {pid})");
            }

            var agg = appearances[pid];
            // Lineup element has no PlayedMinutes - if player is in Lineup, they played
            var minutes = int.Parse(playerEl.Element("PlayedMinutes")?.Value ?? "90");
            if (minutes <= 0) continue;

            // Use RatingStars (start-of-match) as primary - this matches what users see in match reports
            var ratingStr = playerEl.Element("RatingStars")?.Value 
                ?? playerEl.Element("RatingStarsEndOfMatch")?.Value 
                ?? playerEl.Element("RatingEndOfGame")?.Value 
                ?? playerEl.Element("Rating")?.Value ?? "0";
            var rating = double.Parse(ratingStr, System.Globalization.CultureInfo.InvariantCulture);
            var roleId = playerEl.Element("RoleID")?.Value ?? "";
            var position = MapRoleIdToPosition(roleId);

            Debug.WriteLine($"[ExtractPlayersFromLineup] Player {pid}: minutes={minutes}, rating={rating}, roleId={roleId}, position={position}");

            agg.Matches += 1;
            agg.Minutes += minutes;
            agg.RatingSum += rating;

            // Zapisz ocenę na pozycji
            if (!string.IsNullOrEmpty(position) && rating > 0)
            {
                if (!agg.PositionRatings.ContainsKey(position))
                {
                    agg.PositionRatings[position] = new List<double>();
                }
                agg.PositionRatings[position].Add(rating);
                Debug.WriteLine($"[ExtractPlayersFromLineup] Added rating {rating} for player {pid} at position {position}");
            }
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

        // Pobierz tylko mecze seniorskie (MatchType 1-3: League, Qualification, Cup)
        // Wyklucz mecze młodzieżówki (MatchType 50+)
        // Sortujemy po dacie malejaco i bierzemy 3 najnowsze - zeby ocena odzwierciedlala aktualna forme,
        // a nie usrednienie z dlugiego okresu (graczy rosnie/spada forma).
        var finishedMatchIds = matchesDoc.Descendants("Match")
            .Where(m =>
            {
                var status = m.Element("Status")?.Value ?? "";
                var matchType = int.Parse(m.Element("MatchType")?.Value ?? "0");
                var homeTeamId = int.Parse(m.Element("HomeTeam")?.Element("HomeTeamID")?.Value ?? "0");
                var awayTeamId = int.Parse(m.Element("AwayTeam")?.Element("AwayTeamID")?.Value ?? "0");

                // Tylko finished mecze seniorskie gdzie nasza drużyna gra
                return status.Equals("FINISHED", StringComparison.OrdinalIgnoreCase)
                    && matchType >= 1 && matchType <= 12  // Senior matches only
                    && (homeTeamId == teamId || awayTeamId == teamId);
            })
            .OrderByDescending(m =>
            {
                var dateStr = m.Element("MatchDate")?.Value ?? "";
                return DateTime.TryParse(dateStr, out var dt) ? dt : DateTime.MinValue;
            })
            .Select(m => m.Element("MatchID")?.Value)
            .Where(id => !string.IsNullOrEmpty(id))
            .Take(3)
            .ToList();
        
        Debug.WriteLine($"[EnrichPlayers] Found {finishedMatchIds.Count} senior matches for team {teamId}");

        if (finishedMatchIds.Count == 0) return;

        var playerIndex = players.ToDictionary(p => p.PlayerId);
        var appearances = players.ToDictionary(p => p.PlayerId, _ => new PlayerAppearanceAggregate());

        var lineupTasks = finishedMatchIds.Select(id => FetchMatchLineupAsync(id!, teamId, accessToken, accessTokenSecret));
        var lineups = await Task.WhenAll(lineupTasks);

        foreach (var lineupXml in lineups)
        {
            if (string.IsNullOrEmpty(lineupXml)) continue;
            try
            {
                AggregateLineup(lineupXml, teamId, playerIndex, appearances);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EnrichPlayers] EXCEPTION in AggregateLineup: {ex.Message}");
            }
        }

        foreach (var player in players)
        {
            if (!appearances.TryGetValue(player.PlayerId, out var agg) || agg.Matches == 0) continue;

            var stats = player.MatchStats ?? new PlayerMatchStats();
            stats.TotalMatches = agg.Matches;
            stats.MinutesPlayed = agg.Minutes;
            stats.AverageRating = agg.Matches > 0 ? Math.Round(agg.RatingSum / agg.Matches, 2) : 0;
            stats.AverageForm = player.Form;
            
            Debug.WriteLine($"[EnrichPlayers] Player {player.PlayerId}: {agg.Matches} matches, {agg.PositionRatings.Count} positions");
            
            // Calculate average ratings per position
            foreach (var kvp in agg.PositionRatings)
            {
                if (kvp.Value.Count > 0)
                {
                    stats.PositionRatings[kvp.Key] = Math.Round(kvp.Value.Average(), 2);
                }
            }
            
            player.MatchStats = stats;
        }
    }

    private async Task<string?> FetchMatchLineupAsync(string matchId, int teamId, string accessToken, string accessTokenSecret)
    {
        try
        {
            var queryParams = new Dictionary<string, string>
            {
                { "file", "matchlineup" },
                { "matchId", matchId },
                { "teamId", teamId.ToString() },
                { "version", "2.1" }
            };
            return await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error fetching match lineup for matchId {matchId}: {ex.Message}");
            return null;
        }
    }

    private void AggregateLineup(string lineupXml, int teamId, Dictionary<int, Player> playerIndex, Dictionary<int, PlayerAppearanceAggregate> appearances)
    {
        var doc = XDocument.Parse(lineupXml);
        
        // Find <Team> element with matching TeamID (contains <Lineup> with ratings)
        var teamElement = doc.Descendants("Team")
            .FirstOrDefault(t => t.Element("TeamID")?.Value == teamId.ToString());
        
        if (teamElement == null) return;

        // Use <Lineup> container which has RatingStars/RatingStarsEndOfMatch
        var lineupContainer = teamElement.Element("Lineup");
        if (lineupContainer == null) return;

        var lineupPlayers = lineupContainer.Elements("Player").ToList();
        int matched = 0;
        foreach (var playerEl in lineupPlayers)
        {
            if (!int.TryParse(playerEl.Element("PlayerID")?.Value, out int pid)) continue;
            // Skip bench/substitutes (RoleID >= 114) and substitution events (RoleID < 100)
            var roleIdStr = playerEl.Element("RoleID")?.Value ?? "0";
            if (int.TryParse(roleIdStr, out int roleIdInt) && (roleIdInt >= 114 || roleIdInt < 100)) continue;
            // Sprawdź czy ten gracz jest w naszym indeksie (czyli należy do szukanej drużyny)
            if (!playerIndex.ContainsKey(pid)) continue;
            if (!appearances.TryGetValue(pid, out var agg)) continue;

            var minutes = int.Parse(playerEl.Element("PlayedMinutes")?.Value ?? "90");
            if (minutes <= 0) continue;

            // Use RatingStars (start-of-match) as primary
            var rating = double.Parse(
                playerEl.Element("RatingStars")?.Value ?? 
                playerEl.Element("RatingStarsEndOfMatch")?.Value ??
                playerEl.Element("RatingEndOfGame")?.Value ?? 
                playerEl.Element("Rating")?.Value ?? "0", 
                CultureInfo.InvariantCulture);
            var roleId = playerEl.Element("RoleID")?.Value ?? "";
            var position = MapRoleIdToPosition(roleId);

            agg.Matches += 1;
            agg.Minutes += minutes;
            agg.RatingSum += rating;
            matched++;

            // Track rating for this position
            if (!string.IsNullOrEmpty(position) && rating > 0)
            {
                if (!agg.PositionRatings.ContainsKey(position))
                {
                    agg.PositionRatings[position] = new List<double>();
                }
                agg.PositionRatings[position].Add(rating);
            }
        }
        Debug.WriteLine($"[AggregateLineup] team={teamId}: {lineupPlayers.Count} in Lineup, {matched} matched");
    }

    private class PlayerAppearanceAggregate
    {
        public int Matches { get; set; }
        public int Minutes { get; set; }
        public int Goals { get; set; }
        public double RatingSum { get; set; }
        public Dictionary<string, List<double>> PositionRatings { get; set; } = new();
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
        // Try explicit sessionId first (from OAuthController sessions)
        if (!string.IsNullOrEmpty(sessionId))
        {
            var session = OAuthController.GetSession(sessionId);
            if (session?.AccessToken != null && session.AccessTokenSecret != null)
            {
                return (session.AccessToken, session.AccessTokenSecret);
            }
        }

        // Try cookie-based sessionId from TokenStore
        var cookieSessionId = _httpContextAccessor.HttpContext?.Request.Cookies["ht_session"] ?? sessionId ?? "";
        var stored = _tokenStore.Get(cookieSessionId);
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
            FormationUsed = matchDetails?.Formation ?? "N/A",
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
            // Użyj matchdetails zamiast matchlineup - matchdetails jest publiczne i zawiera formacje obu drużyn
            var queryParams = new Dictionary<string, string>
            {
                { "file", "matchdetails" },
                { "matchId", matchId },
                { "version", "3.0" }
            };

            var response = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);
            var doc = XDocument.Parse(response);

            // matchdetails zwraca <Match><HomeTeam> i <AwayTeam>
            var homeTeam = doc.Descendants("HomeTeam").FirstOrDefault();
            var awayTeam = doc.Descendants("AwayTeam").FirstOrDefault();
            
            var homeTeamId = int.Parse(homeTeam?.Element("HomeTeamID")?.Value ?? "0");
            var awayTeamId = int.Parse(awayTeam?.Element("AwayTeamID")?.Value ?? "0");
            
            // Wybierz odpowiednią drużynę
            var myTeamElement = homeTeamId == teamId ? homeTeam : awayTeam;
            if (myTeamElement == null) return null;

            // Formacja jest w elemencie <Formation>
            var formationStr = myTeamElement.Element("Formation")?.Value ?? "";
            
            // Oceny drużyny
            var midfieldRating = int.Parse(myTeamElement.Element("RatingMidfield")?.Value ?? "0");
            var rightDefense = int.Parse(myTeamElement.Element("RatingRightDef")?.Value ?? "0");
            var centralDefense = int.Parse(myTeamElement.Element("RatingMidDef")?.Value ?? "0");
            var leftDefense = int.Parse(myTeamElement.Element("RatingLeftDef")?.Value ?? "0");
            var rightAttack = int.Parse(myTeamElement.Element("RatingRightAtt")?.Value ?? "0");
            var centralAttack = int.Parse(myTeamElement.Element("RatingMidAtt")?.Value ?? "0");
            var leftAttack = int.Parse(myTeamElement.Element("RatingLeftAtt")?.Value ?? "0");

            // Posiadanie piłki i inne dane
            var matchElement = doc.Descendants("Match").FirstOrDefault();
            var possession = homeTeamId == teamId 
                ? int.Parse(matchElement?.Element("PossessionFirstHalfHome")?.Value ?? "0")
                : int.Parse(matchElement?.Element("PossessionFirstHalfAway")?.Value ?? "0");
            
            var attitude = myTeamElement.Element("TacticType")?.Value ?? "Normal";
            var teamSpirit = myTeamElement.Element("TeamSpirit")?.Value ?? "calm";

            return new MatchDetails
            {
                Formation = formationStr,
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
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting match details for matchId {matchId}, teamId {teamId}: {ex.Message}");
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

    private static string MapRoleIdToPosition(string roleId)
    {
        // Mapowanie RoleID z API Hattrick na pozycje używane w aplikacji
        // 100 = Keeper
        // 101 = Right back, 102 = Right CD, 103 = Middle CD, 104 = Left CD, 105 = Left back
        // 106 = Right winger, 107 = Right IM, 108 = Middle IM, 109 = Left IM, 110 = Left winger
        // 111 = Right forward, 112 = Middle forward, 113 = Left forward
        return roleId switch
        {
            "100" => "GK",
            "101" => "RWB",
            "102" => "RCD",
            "103" => "CD",
            "104" => "LCD",
            "105" => "LWB",
            "106" => "RW",
            "107" => "RIM",
            "108" => "IM",
            "109" => "LIM",
            "110" => "LW",
            "111" => "RFW",
            "112" => "FW",
            "113" => "LFW",
            _ => ""
        };
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
