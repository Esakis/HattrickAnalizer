using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class CalibrationReplayTests
{
    [Fact]
    public async Task Complete_pre_kickoff_snapshot_replays_through_mocked_CHPP_fixtures()
    {
        var storage = Path.Combine(Path.GetTempPath(), "hattrick-calibration-replay", Guid.NewGuid().ToString("N"));
        try
        {
            var kickoff = DateTimeOffset.UtcNow.AddDays(-3);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Calibration:StoragePath"] = storage,
                ["HattrickApi:ConsumerKey"] = "test-key",
                ["HattrickApi:ConsumerSecret"] = "test-secret"
            }).Build();
            var snapshotStore = new CalibrationSnapshotStore(configuration, new TestEnvironment(storage));
            var snapshot = CreateSnapshot(kickoff);
            Assert.True(snapshot.Context.IsComplete);
            await snapshotStore.SaveAsync(snapshot);
            var storedSnapshot = Assert.Single(await snapshotStore.GetAsync(55));
            Assert.True(storedSnapshot.Context.IsComplete);
            Assert.True(storedSnapshot.Players.All(player => player.SkillsAvailable));

            var handler = new ChppFixtureHandler(PlayersXml(), MatchesXml(kickoff), MatchLineupXml(), MatchDetailsXml(kickoff));
            using var httpClient = new HttpClient(handler);
            var oauth = new OAuthService(configuration, httpClient);
            var tokenStore = new TokenStore(configuration, new EphemeralDataProtectionProvider(), NullLogger<TokenStore>.Instance);
            tokenStore.Save("replay-session", new StoredToken { AccessToken = "replay-test-token", AccessTokenSecret = "test-token-secret", OwnTeamId = 55 });
            var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
            accessor.HttpContext!.Request.Headers.Cookie = "ht_session=replay-session";
            var api = new HattrickApiService(httpClient, configuration, oauth, tokenStore, accessor, NullLogger<HattrickApiService>.Instance);
            var calibration = new CalibrationService(api, NullLogger<CalibrationService>.Instance, snapshotStore, new RatingEngine());

            var report = await calibration.CompareOwnMatchesAsync(55, 5);

            Assert.True(report.Matches.Count == 1,
                $"Expected one valid replay; excluded={report.ExcludedMatches}, reasons={string.Join(" | ", report.ExclusionReasons)}");
            var match = Assert.Single(report.Matches);
            Assert.Equal(9001, match.MatchId);
            Assert.Equal(11, match.PlayersMatched);
            Assert.Equal(kickoff.UtcDateTime, match.MatchDate);
            Assert.Equal(1, report.TrainingSampleCount);
            Assert.Equal(0, report.HeldOutSampleCount);
            var snapshotLineup = new Lineup { Formation = snapshot.Context.Formation!, TacticType = snapshot.Context.Tactic! };
            foreach (var (slot, playerId) in snapshot.Context.PlayerIdsBySlot)
            {
                snapshotLineup.Positions[slot] = new LineupPosition
                {
                    Position = slot,
                    Player = snapshot.Players.Single(player => player.PlayerId == playerId),
                    Behavior = snapshot.Context.BehavioursBySlot[slot]
                };
            }
            var expectedRatings = new RatingEngine().ComputeContextualRatings(snapshotLineup, snapshot.Context.WeatherId!.Value,
                snapshot.Context.Attitude!, snapshot.Context.CoachType!, AdvancedLineupOptimizer.ComputeDisorderRisk(snapshot.Context.FormationExperience!.Value),
                snapshot.Context.TeamSpiritLevel, snapshot.Context.ConfidenceLevel, applyHomeAdvantage: true);
            new RatingEngine().ApplyTactic(expectedRatings, snapshot.Context.Tactic!);
            AssertSevenSectorsEqual(expectedRatings, match.Predicted);
            var awayRatings = new RatingEngine().ComputeContextualRatings(snapshotLineup, snapshot.Context.WeatherId!.Value,
                snapshot.Context.Attitude!, snapshot.Context.CoachType!, AdvancedLineupOptimizer.ComputeDisorderRisk(snapshot.Context.FormationExperience!.Value),
                snapshot.Context.TeamSpiritLevel, snapshot.Context.ConfidenceLevel, applyHomeAdvantage: false);
            new RatingEngine().ApplyTactic(awayRatings, snapshot.Context.Tactic!);
            Assert.Equal(awayRatings.Midfield * FormationData.TacticModifiers.HomeAdvantage, expectedRatings.Midfield, 10);
            Assert.Equal(awayRatings.RightDefense, expectedRatings.RightDefense, 10);
            Assert.Equal(awayRatings.CentralDefense, expectedRatings.CentralDefense, 10);
            Assert.Equal(awayRatings.LeftDefense, expectedRatings.LeftDefense, 10);
            Assert.Equal(awayRatings.RightAttack, expectedRatings.RightAttack, 10);
            Assert.Equal(awayRatings.CentralAttack, expectedRatings.CentralAttack, 10);
            Assert.Equal(awayRatings.LeftAttack, expectedRatings.LeftAttack, 10);
            var currentRoster = XDocument.Parse(PlayersXml()).Descendants("Player").Select(api.ParsePlayer)
                .ToDictionary(player => player.PlayerId);
            var currentRosterLineup = new Lineup { Formation = snapshot.Context.Formation!, TacticType = snapshot.Context.Tactic! };
            foreach (var (slot, playerId) in snapshot.Context.PlayerIdsBySlot)
            {
                currentRosterLineup.Positions[slot] = new LineupPosition
                {
                    Position = slot, Player = currentRoster[playerId], Behavior = snapshot.Context.BehavioursBySlot[slot]
                };
            }
            var currentRosterRatings = new RatingEngine().ComputeContextualRatings(currentRosterLineup, snapshot.Context.WeatherId!.Value,
                snapshot.Context.Attitude!, snapshot.Context.CoachType!, AdvancedLineupOptimizer.ComputeDisorderRisk(snapshot.Context.FormationExperience!.Value),
                snapshot.Context.TeamSpiritLevel, snapshot.Context.ConfidenceLevel, applyHomeAdvantage: true);
            new RatingEngine().ApplyTactic(currentRosterRatings, snapshot.Context.Tactic!);
            Assert.NotEqual(currentRosterRatings.Midfield, match.Predicted.Midfield);
            Assert.Contains("players", handler.RequestedFiles);
            Assert.Contains("matches", handler.RequestedFiles);
            Assert.Contains("matchdetails", handler.RequestedFiles);
            Assert.Contains("matchlineup", handler.RequestedFiles);
        }
        finally
        {
            DeleteOnlyUniqueTempDirectory(storage, "hattrick-calibration-replay");
        }
    }

    [Fact]
    public async Task Persisted_snapshot_captured_after_kickoff_is_excluded_from_replay()
    {
        var storage = Path.Combine(Path.GetTempPath(), "hattrick-calibration-future", Guid.NewGuid().ToString("N"));
        try
        {
            var kickoff = DateTimeOffset.UtcNow.AddDays(-3);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Calibration:StoragePath"] = storage,
                ["HattrickApi:ConsumerKey"] = "test-key",
                ["HattrickApi:ConsumerSecret"] = "test-secret"
            }).Build();
            Directory.CreateDirectory(storage);
            var afterKickoff = CreateSnapshot(kickoff);
            afterKickoff.RecordedAt = kickoff.AddMinutes(5);
            await File.WriteAllTextAsync(Path.Combine(storage, "team-55.json"),
                JsonSerializer.Serialize(new[] { afterKickoff }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var handler = new ChppFixtureHandler(PlayersXml(), MatchesXml(kickoff), MatchLineupXml(), MatchDetailsXml(kickoff));
            using var httpClient = new HttpClient(handler);
            var oauth = new OAuthService(configuration, httpClient);
            var tokenStore = new TokenStore(configuration, new EphemeralDataProtectionProvider(), NullLogger<TokenStore>.Instance);
            tokenStore.Save("future-session", new StoredToken { AccessToken = "future-test-token", AccessTokenSecret = "test-secret", OwnTeamId = 55 });
            var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
            accessor.HttpContext!.Request.Headers.Cookie = "ht_session=future-session";
            var api = new HattrickApiService(httpClient, configuration, oauth, tokenStore, accessor, NullLogger<HattrickApiService>.Instance);
            var store = new CalibrationSnapshotStore(configuration, new TestEnvironment(storage));
            var calibration = new CalibrationService(api, NullLogger<CalibrationService>.Instance, store, new RatingEngine());

            var report = await calibration.CompareOwnMatchesAsync(55, 5);

            Assert.Empty(report.Matches);
            Assert.Equal(1, report.ExcludedMatches);
            Assert.Contains(report.ExclusionReasons, reason => reason.Contains("captured by kickoff", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.RequestedFiles, file => file == "matchdetails");
        }
        finally
        {
            DeleteOnlyUniqueTempDirectory(storage, "hattrick-calibration-future");
        }
    }

    private static CalibrationSnapshot CreateSnapshot(DateTimeOffset kickoff)
    {
        var slots = FormationData.Formations["4-4-2"].Positions;
        var ids = slots.Select((slot, i) => (slot, id: i + 1)).ToDictionary(x => x.slot, x => x.id, StringComparer.Ordinal);
        return new CalibrationSnapshot
        {
            TeamId = 55, RecordedAt = kickoff.AddDays(-1), Source = "fixture",
            Players = slots.Select((_, i) => new Player
            {
                PlayerId = i + 1, FirstName = $"P{i + 1}", Age = 22 + i, Form = 7, Stamina = 7, StaminaAvailable = true,
                InjuryLevel = -1, InjuryStatusKnown = true, IsSuspended = false, SuspensionStatusKnown = true,
                Skills = new PlayerSkills
                {
                    Keeper = 1, KeeperAvailable = true, Defending = 2, DefendingAvailable = true,
                    Playmaking = 2, PlaymakingAvailable = true, Winger = 3, WingerAvailable = true,
                    Passing = 2, PassingAvailable = true, Scoring = 2, ScoringAvailable = true,
                    SetPieces = 1, SetPiecesAvailable = true
                }
            }).ToList(),
            Context = new CalibrationMatchContext
            {
                MatchId = 9001, MatchDate = kickoff, IsHomeMatch = true, Tactic = "Normal", Attitude = "Normal",
                CoachType = "Neutral", TeamSpiritLevel = 5, ConfidenceLevel = 5, Formation = "4-4-2",
                FormationExperience = 8, WeatherId = 1, AssistantManagerLevel = 0,
                PlayerIdsBySlot = ids, BehavioursBySlot = ids.Keys.ToDictionary(slot => slot, slot => slot, StringComparer.Ordinal)
            }
        };
    }

    private static string PlayersXml() => new XDocument(new XElement("HattrickData",
        new XElement("Team", Enumerable.Range(1, 11).Select(i => new XElement("Player",
            new XElement("PlayerID", i), new XElement("FirstName", $"P{i}"), new XElement("LastName", "Fixture"),
            new XElement("Age", 22 + i), new XElement("TSI", 1000), new XElement("PlayerForm", 7),
            new XElement("StaminaSkill", 7), new XElement("Experience", 5), new XElement("PlayerNumber", i),
            new XElement("InjuryLevel", -1), new XElement("Cards", 0), new XElement("MotherClubBonus", "True"),
            new XElement("KeeperSkill", 5), new XElement("DefenderSkill", 7), new XElement("PlaymakerSkill", 8),
            new XElement("WingerSkill", 6), new XElement("PassingSkill", 7), new XElement("ScorerSkill", 6),
            new XElement("SetPiecesSkill", 5)))))).ToString(SaveOptions.DisableFormatting);

    private static string MatchesXml(DateTimeOffset kickoff) => new XDocument(new XElement("HattrickData",
        new XElement("Match", new XElement("MatchID", 9001), new XElement("MatchDate", kickoff.ToString("O")),
            new XElement("Status", "FINISHED"), new XElement("MatchType", 1),
            new XElement("HomeTeam", new XElement("HomeTeamID", 55)),
            new XElement("AwayTeam", new XElement("AwayTeamID", 99))))).ToString(SaveOptions.DisableFormatting);

    private static string MatchLineupXml() => new XDocument(new XElement("HattrickData",
        new XElement("Team", new XElement("TeamID", 55), new XElement("Lineup",
            FormationData.Formations["4-4-2"].Positions.Select((slot, i) => new XElement("Player",
                new XElement("PlayerID", i + 1), new XElement("RoleID", MatchOrdersService.SlotToRoleId(slot)),
                new XElement("Behaviour", 0), new XElement("PositionCode", MatchOrdersService.SlotToRoleId(slot)),
                new XElement("PlayedMinutes", 90), new XElement("Rating", "6.0"))))))).ToString(SaveOptions.DisableFormatting);

    private static string MatchDetailsXml(DateTimeOffset kickoff)
    {
        static XElement Team(string name, int id) => new(name,
            new XElement(name == "HomeTeam" ? "HomeTeamID" : "AwayTeamID", id),
            new XElement("Formation", "4-4-2"), new XElement("TacticType", 0), new XElement("TeamAttitude", 0),
            new XElement("RatingMidfield", 45), new XElement("RatingRightDef", 42), new XElement("RatingMidDef", 44),
            new XElement("RatingLeftDef", 43), new XElement("RatingRightAtt", 41), new XElement("RatingMidAtt", 46),
            new XElement("RatingLeftAtt", 40), new XElement("RatingIndirectSetPiecesAtt", 12),
            new XElement("RatingIndirectSetPiecesDef", 13));
        return new XDocument(new XElement("HattrickData", new XElement("MatchDate", kickoff.ToString("O")),
            new XElement("WeatherID", 1), Team("HomeTeam", 55), Team("AwayTeam", 99))).ToString(SaveOptions.DisableFormatting);
    }

    private static void AssertSevenSectorsEqual(LineupRatings expected, LineupRatings actual)
    {
        Assert.Equal(expected.Midfield, actual.Midfield, 10);
        Assert.Equal(expected.RightDefense, actual.RightDefense, 10);
        Assert.Equal(expected.CentralDefense, actual.CentralDefense, 10);
        Assert.Equal(expected.LeftDefense, actual.LeftDefense, 10);
        Assert.Equal(expected.RightAttack, actual.RightAttack, 10);
        Assert.Equal(expected.CentralAttack, actual.CentralAttack, 10);
        Assert.Equal(expected.LeftAttack, actual.LeftAttack, 10);
    }

    private static void DeleteOnlyUniqueTempDirectory(string path, string expectedFolder)
    {
        if (!Directory.Exists(path)) return;
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(Path.Combine(Path.GetTempPath(), expectedFolder)), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(Path.GetFileName(resolved), out _))
            throw new InvalidOperationException("Refusing to remove a calibration fixture path outside its unique temp folder.");
        Directory.Delete(resolved, recursive: true);
    }

    private sealed class ChppFixtureHandler(string players, string matches, string lineup, string details) : HttpMessageHandler
    {
        public List<string> RequestedFiles { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
                    pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "", StringComparer.OrdinalIgnoreCase);
            var file = query["file"];
            RequestedFiles.Add(file);
            var xml = file switch
            {
                "players" => players,
                "matches" => matches,
                "matchlineup" => lineup,
                "matchdetails" => details,
                _ => throw new InvalidOperationException($"Unexpected CHPP fixture request: {file}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") });
        }
    }

    private sealed class TestEnvironment(string root) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Backend.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
