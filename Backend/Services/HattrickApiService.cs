using System.Xml.Linq;
using HattrickAnalizer.Models;
using HattrickAnalizer.Controllers;

namespace HattrickAnalizer.Services;

public class HattrickApiService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly OAuthService _oauthService;
    private readonly string _baseUrl;

    public HattrickApiService(HttpClient httpClient, IConfiguration configuration, OAuthService oauthService)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _oauthService = oauthService;
        _baseUrl = _configuration["HattrickApi:BaseUrl"] ?? "https://chpp.hattrick.org/chppxml.ashx";
    }

    public async Task<Team> GetTeamDetailsAsync(int teamId, string? sessionId = null)
    {
        var team = new Team
        {
            TeamId = teamId,
            TeamName = $"Team {teamId}"
        };

        var players = await GetTeamPlayersAsync(teamId, sessionId);
        team.Players = players;

        return team;
    }

    public async Task<List<Player>> GetTeamPlayersAsync(int teamId, string? sessionId = null)
    {
        try
        {
            string response;
            
            if (!string.IsNullOrEmpty(sessionId))
            {
                var session = OAuthController.GetSession(sessionId);
                if (session?.AccessToken != null && session?.AccessTokenSecret != null)
                {
                    var queryParams = new Dictionary<string, string>
                    {
                        { "file", "players" },
                        { "teamId", teamId.ToString() },
                        { "version", "2.8" }
                    };
                    response = await _oauthService.MakeAuthenticatedRequestAsync(
                        session.AccessToken,
                        session.AccessTokenSecret,
                        queryParams
                    );
                }
                else
                {
                    return GenerateMockPlayers();
                }
            }
            else
            {
                return GenerateMockPlayers();
            }
            
            var doc = XDocument.Parse(response);
            var players = new List<Player>();

            var playerElements = doc.Descendants("Player");
            foreach (var playerElement in playerElements)
            {
                var player = ParsePlayer(playerElement);
                players.Add(player);
            }

            return players;
        }
        catch (Exception ex)
        {
            return GenerateMockPlayers();
        }
    }

    public async Task<TeamRatings> GetOpponentRatingsAsync(int teamId, int matchId, string? sessionId = null)
    {
        try
        {
            string response;
            
            if (!string.IsNullOrEmpty(sessionId))
            {
                var session = OAuthController.GetSession(sessionId);
                if (session?.AccessToken != null && session?.AccessTokenSecret != null)
                {
                    var queryParams = new Dictionary<string, string>
                    {
                        { "file", "matchdetails" },
                        { "matchId", matchId.ToString() },
                        { "version", "3.1" }
                    };
                    response = await _oauthService.MakeAuthenticatedRequestAsync(
                        session.AccessToken,
                        session.AccessTokenSecret,
                        queryParams
                    );
                }
                else
                {
                    return GenerateMockRatings();
                }
            }
            else
            {
                return GenerateMockRatings();
            }
            
            var doc = XDocument.Parse(response);
            return ParseTeamRatings(doc, teamId);
        }
        catch (Exception ex)
        {
            return GenerateMockRatings();
        }
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
            return GenerateMockRatings();

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

    private TeamRatings GenerateMockRatings()
    {
        var random = new Random();
        return new TeamRatings
        {
            MidfieldRating = random.Next(30, 80),
            RightDefenseRating = random.Next(20, 60),
            CentralDefenseRating = random.Next(25, 70),
            LeftDefenseRating = random.Next(20, 60),
            RightAttackRating = random.Next(15, 50),
            CentralAttackRating = random.Next(20, 60),
            LeftAttackRating = random.Next(15, 50)
        };
    }
}
