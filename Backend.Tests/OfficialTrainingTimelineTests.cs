using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using HattrickAnalizer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class OfficialTrainingTimelineTests
{
    [Fact]
    public async Task Official_clean_ninety_minute_lineup_produces_physical_coverage_without_played_minutes()
    {
        var summary = await GetSummaryAsync(trainingType: 5, cleanOnly: true);
        Assert.Equal(90, Assert.Single(summary.Players, p => p.PlayerId == 1).CoverageMinutes);
        Assert.Equal(90, Assert.Single(summary.Players, p => p.PlayerId == 5).CoverageMinutes);
        Assert.DoesNotContain(summary.Warnings, warning => warning.Contains("PlayedMinutes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Official_lineup_reconstructs_clean_substitution_swap_red_card_and_extra_time_without_played_minutes()
    {
        var summary = await GetSummaryAsync(trainingType: 5);
        var player3 = Assert.Single(summary.Players, p => p.PlayerId == 3);
        var player4 = Assert.Single(summary.Players, p => p.PlayerId == 4);
        var player5 = Assert.Single(summary.Players, p => p.PlayerId == 5);
        var player12 = Assert.Single(summary.Players, p => p.PlayerId == 12);

        Assert.Equal(213, Assert.Single(summary.Players, p => p.PlayerId == 1).CoverageMinutes);
        Assert.Equal(150, player3.CoverageMinutes);
        Assert.Equal(63, player12.CoverageMinutes);
        Assert.Equal(120, player4.CoverageMinutes);
        Assert.Equal(213, player5.CoverageMinutes);
        Assert.Equal(135, player5.ObservedFullMinutes);
        Assert.Equal(78, player5.ObservedHalfMinutes);
        Assert.Equal(90, player5.FullTrainingMinutes);
        Assert.Equal(0, player5.HalfTrainingMinutes);
        Assert.Equal(90, player5.EffectiveTrainingMinutes);
        Assert.Empty(player5.Warnings);
    }

    [Fact]
    public async Task Official_set_piece_role_marker_produces_best90_special_bonus_segments()
    {
        var summary = await GetSummaryAsync(trainingType: 2);
        var taker = Assert.Single(summary.Players, p => p.PlayerId == 7);

        Assert.Equal(213, taker.CoverageMinutes);
        Assert.Equal(213, taker.ObservedFullMinutes);
        Assert.Equal(90, taker.FullTrainingMinutes);
        Assert.Equal(90, taker.SetPiecesBonusFullMinutes);
        Assert.Equal(22.5, taker.SetPiecesSpecialBonusMinutes);
        Assert.Equal(112.5, taker.EffectiveTrainingMinutes + taker.SetPiecesSpecialBonusMinutes);
    }

    private static async Task<TrainingSummary> GetSummaryAsync(int trainingType, bool cleanOnly = false)
    {
        var teamId = trainingType == 2 ? 56 : cleanOnly ? 57 : 55;
        var nextUpdateDay = DateTime.UtcNow.AddDays(1).DayOfWeek.ToString();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UseMockData"] = "false", ["HattrickApi:ConsumerKey"] = "fixture-consumer",
            ["HattrickApi:ConsumerSecret"] = "fixture-secret", ["ConnectionStrings:HattrickDb"] = "",
            ["Training:UpdateDay"] = nextUpdateDay, ["Training:UpdateHour"] = "0",
            ["Training:TimeZoneId"] = "UTC"
        }).Build();
        using var client = new HttpClient(new FixtureHandler(trainingType, cleanOnly, teamId));
        var oauth = new OAuthService(configuration, client);
        var tokenStore = new TokenStore(configuration, new EphemeralDataProtectionProvider(), NullLogger<TokenStore>.Instance);
        tokenStore.Save("official-training-session", new StoredToken
        { AccessToken = $"fixture-token-{Guid.NewGuid():N}", AccessTokenSecret = "fixture-secret", OwnTeamId = teamId });
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "ht_session=official-training-session";
        var api = new HattrickApiService(client, configuration, oauth, tokenStore, new HttpContextAccessor { HttpContext = context },
            NullLogger<HattrickApiService>.Instance);
        return await new TrainingService(api, NullLogger<TrainingService>.Instance, configuration).GetSummaryAsync(teamId);
    }

    private static string TrainingXml(int trainingType) => Xml(new XElement("HattrickData", new XElement("Team",
        new XElement("TrainingType", trainingType), new XElement("TrainingLevel", "90"),
        new XElement("StaminaTrainingPart", "10"), new XElement("Morale", "5"), new XElement("SelfConfidence", "5"))));

    private static string StaffXml() => Xml(new XElement("HattrickData", new XElement("StaffList",
        new XElement("Trainer", new XElement("TrainerSkillLevel", "4"), new XElement("TrainerType", "0")))));

    private static string PlayersXml() => Xml(new XElement("HattrickData", new XElement("Team",
        Enumerable.Range(1, 12).Select(i => new XElement("Player", new XElement("PlayerID", i),
            new XElement("FirstName", "Fixture"), new XElement("LastName", i), new XElement("Age", "25"),
            new XElement("TSI", "1"), new XElement("PlayerForm", "7"), new XElement("StaminaSkill", "7"),
            new XElement("Experience", "5"), new XElement("Loyalty", "1"), new XElement("Leadership", "1"),
            new XElement("InjuryLevel", "-1"), new XElement("Cards", "0"),
            new XElement("KeeperSkill", "5"), new XElement("DefenderSkill", "5"), new XElement("PlaymakerSkill", "5"),
            new XElement("WingerSkill", "5"), new XElement("PassingSkill", "5"), new XElement("ScorerSkill", "5"),
            new XElement("SetPiecesSkill", "5"))))));

    private static string MatchesXml(int teamId, bool cleanOnly)
    {
        string Date(int daysAgo) => DateTimeOffset.UtcNow.AddDays(-daysAgo).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var matches = new List<XElement> { new("Match", new XElement("MatchID", teamId + 9000),
            new XElement("MatchDate", Date(cleanOnly ? 1 : 2)), new XElement("Status", "FINISHED"), new XElement("MatchType", "1")) };
        if (!cleanOnly) matches.Add(new XElement("Match", new XElement("MatchID", teamId + 9001),
            new XElement("MatchDate", Date(1)), new XElement("Status", "FINISHED"), new XElement("MatchType", "1")));
        return Xml(new XElement("HattrickData", new XElement("Team",
            new XElement("MatchList", matches))));
    }

    private static string LineupXml(int teamId, int matchId)
    {
        var cleanMatch = matchId == teamId + 9000;
        var slotsByPlayer = new[] { (1, 100), (2, 101), (3, 102), (4, 104), (5, 106), (6, 105),
            (7, 107), (8, 109), (9, 110), (10, 111), (11, 113) };
        var starting = new XElement("StartingLineup", slotsByPlayer.Select(pair => new XElement("Player",
            new XElement("PlayerID", pair.Item1), new XElement("RoleID", pair.Item2), new XElement("Behaviour", "0")))
            .Append(new XElement("Player", new XElement("PlayerID", 7), new XElement("RoleID", 17), new XElement("Behaviour", "0"))));
        var orders = new XElement("Substitutions",
            new XElement("Substitution", new XElement("TeamID", teamId), new XElement("SubjectPlayerID", 3),
                new XElement("ObjectPlayerID", 12), new XElement("OrderType", 1), new XElement("NewPositionId", 102),
                new XElement("NewPositionBehaviour", 0), new XElement("MatchMinute", 60), new XElement("MatchPart", 2)),
            new XElement("Substitution", new XElement("TeamID", teamId), new XElement("SubjectPlayerID", 5),
                new XElement("ObjectPlayerID", 6), new XElement("OrderType", 3), new XElement("NewPositionId", 0),
                new XElement("NewPositionBehaviour", 0), new XElement("MatchMinute", 45), new XElement("MatchPart", 1)));
        if (cleanMatch) orders = new XElement("Substitutions");
        var finalSlots = slotsByPlayer.Where(pair => cleanMatch || pair.Item1 is not (3 or 5 or 6))
            .Append((5, 105)).Append((6, 106)).Append((12, 102));
        if (cleanMatch) finalSlots = slotsByPlayer;
        var final = new XElement("Lineup", finalSlots.Select(pair => new XElement("Player",
            new XElement("PlayerID", pair.Item1), new XElement("RoleID", pair.Item2)))
            .Append(new XElement("Player", new XElement("PlayerID", 7), new XElement("RoleID", 17))));
        return Xml(new XElement("HattrickData", new XElement("Team", new XElement("TeamID", teamId),
            starting, orders, final)));
    }

    private static string MatchDetailsXml(int teamId, int matchId) => Xml(new XElement("HattrickData", new XElement("Match",
        new XElement("MatchID", matchId), new XElement("FinishedDate", DateTimeOffset.UtcNow.AddHours(-1).ToString("O")),
        new XElement("AddedMinutes", matchId == teamId + 9000 ? "0" : "3"), new XElement("HomeTeam", new XElement("HomeTeamID", teamId)),
        new XElement("AwayTeam", new XElement("AwayTeamID", 66)),
        matchId == teamId + 9000 ? null : new XElement("Bookings", new XElement("Booking", new XElement("BookingPlayerID", 4),
            new XElement("BookingType", 2), new XElement("BookingMinute", 30), new XElement("MatchPart", 1))),
        matchId == teamId + 9000 ? null : new XElement("Injuries",
            new XElement("Injury", new XElement("InjuryPlayerID", 3), new XElement("InjuryTeamID", teamId),
                new XElement("InjuryType", 2), new XElement("InjuryMinute", 60), new XElement("MatchPart", 2)),
            new XElement("Injury", new XElement("InjuryPlayerID", 4), new XElement("InjuryTeamID", teamId),
                new XElement("InjuryType", 1), new XElement("InjuryMinute", 50), new XElement("MatchPart", 2)),
            new XElement("Injury", new XElement("InjuryPlayerID", 8), new XElement("InjuryTeamID", 66),
                new XElement("InjuryType", 2), new XElement("InjuryMinute", 60), new XElement("MatchPart", 2))),
        matchId == teamId + 9000 ? null : new XElement("EventList", new XElement("Event", new XElement("Minute", 105), new XElement("MatchPart", 3),
            new XElement("EventTypeID", 1))))));

    private static string Xml(XElement root) => root.ToString(SaveOptions.DisableFormatting);

    private sealed class FixtureHandler(int trainingType, bool cleanOnly, int fixtureTeamId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]),
                    part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : "", StringComparer.OrdinalIgnoreCase);
            var hasTeamId = int.TryParse(query.GetValueOrDefault("teamId"), out var teamId);
            var hasMatchId = int.TryParse(query.GetValueOrDefault("matchId"), out var matchId)
                || int.TryParse(query.GetValueOrDefault("matchID"), out matchId);
            if (!hasTeamId) teamId = fixtureTeamId;
            if (!hasMatchId) matchId = teamId + 9000;
            var xml = query["file"] switch
            {
                "training" => TrainingXml(trainingType), "stafflist" => StaffXml(), "players" => PlayersXml(),
                "matches" => MatchesXml(teamId, cleanOnly), "matchlineup" => LineupXml(teamId, matchId), "matchdetails" => MatchDetailsXml(teamId, matchId),
                _ => throw new InvalidOperationException($"Unexpected CHPP fixture request: {query["file"]}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(xml, Encoding.UTF8, "application/xml") });
        }
    }
}
