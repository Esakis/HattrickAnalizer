using System.Net;
using System.Net.Http.Json;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests;

public sealed class HistoryAndReviewApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hattrick-history-api-tests", Guid.NewGuid().ToString("N"));
    private readonly ApiTestFactory _factory = new();

    [Fact]
    public async Task Live_history_and_capture_require_authenticated_team_and_incomplete_context_is_not_saved()
    {
        using var app = Configure(mock: false);
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/playerhistory/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/playerhistory/recent")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/playerhistory/sync", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/calibration/snapshots/capture", new { })).StatusCode);

        app.Services.GetRequiredService<TokenStore>().Save("history-test", new StoredToken { AccessToken = "fixture", AccessTokenSecret = "fixture", OwnTeamId = 55 });
        client.DefaultRequestHeaders.Add("Cookie", "ht_session=history-test");
        var response = await client.PostAsJsonAsync("/api/calibration/snapshots/capture", new CalibrationMatchContext { MatchId = 123 });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.False(body.GetProperty("saved").GetBoolean());
        Assert.False(body.GetProperty("complete").GetBoolean());
        Assert.Contains(body.GetProperty("missingFields").EnumerateArray(), item => item.GetString() == "weatherId");
        Assert.Empty(await app.Services.GetRequiredService<CalibrationSnapshotStore>().GetAsync(55));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/playerhistory/recent")).StatusCode);
    }

    [Fact]
    public async Task Synchronization_ignores_body_skills_and_arbitrary_team_ids()
    {
        using var app = Configure(mock: true);
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/playerhistory/check-and-save", new
        {
            teamId = 999,
            players = new[] { new { playerId = 42, skills = new { keeper = 999 } } }
        });
        response.EnsureSuccessStatusCode();
        var changes = await response.Content.ReadFromJsonAsync<List<PlayerChangeResult>>();
        Assert.NotNull(changes);
        Assert.NotEmpty(changes);
        Assert.All(changes, change => Assert.Equal(1001, change.TeamId));
        var service = app.Services.GetRequiredService<PlayerHistoryService>();
        Assert.Empty(await service.GetRecentChangesAsync(999));
        var history = await service.GetPlayerHistoryAsync(changes[0].PlayerId, 1001);
        Assert.NotEqual(999, Assert.Single(history).Keeper);
        var repeat = await client.PostAsJsonAsync("/api/playerhistory/sync", new { });
        repeat.EnsureSuccessStatusCode();
        Assert.All((await repeat.Content.ReadFromJsonAsync<List<PlayerChangeResult>>())!, change => Assert.False(change.Changed));
    }

    [Theory]
    [InlineData("Normal", true)]
    [InlineData("Normal", false)]
    [InlineData("AttackInMiddle", true)]
    [InlineData("AttackOnWings", false)]
    public async Task Optimizer_forecast_matches_server_capture_and_tampered_forecast_is_rejected(string tactic, bool isHome)
    {
        using var app = Configure(mock: true);
        using var client = app.CreateClient();
        var kickoff = DateTime.UtcNow.Date.AddDays(1).AddHours(18);
        var request = new OptimizerRequest
        {
            MyTeamId = 1001, OpponentTeamId = 1002, MatchId = 100199, MatchDate = kickoff,
            IsHomeMatch = isHome, PreferredFormation = "4-4-2", PreferredTactic = tactic,
            TeamAttitude = "Normal", CoachType = "Neutral", TeamSpiritLevel = 5, ConfidenceLevel = 5,
            FormationExperience = new() { ["4-4-2"] = 8 }, AssistantManagerLevel = 2
        };
        var optimize = await client.PostAsJsonAsync("/api/optimizer/optimize", request);
        optimize.EnsureSuccessStatusCode();
        var result = (await optimize.Content.ReadFromJsonAsync<OptimizerResponse>())!;
        var lineup = result.OptimalLineup;
        var context = new CalibrationMatchContext
        {
            MatchId = 100199, MatchDate = kickoff, IsHomeMatch = isHome, Tactic = lineup.TacticType,
            Attitude = result.Alternatives[0].Attitude, CoachType = "Neutral", TeamSpiritLevel = 5, ConfidenceLevel = 5,
            Formation = lineup.Formation, FormationExperience = 8, WeatherId = result.Weather!.WeatherId, AssistantManagerLevel = 2,
            PlayerIdsBySlot = lineup.Positions.ToDictionary(p => p.Key, p => p.Value.Player!.PlayerId),
            BehavioursBySlot = lineup.Positions.ToDictionary(p => p.Key, p => p.Value.Behavior),
            PredictedRatings = result.Comparison.MyTeamRatings, ModelVersion = result.ModelVersion
        };
        var capture = await client.PostAsJsonAsync("/api/calibration/snapshots/capture", context);
        capture.EnsureSuccessStatusCode();
        var body = await capture.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(body.GetProperty("saved").GetBoolean(), body.ToString());
        var stored = Assert.Single(await app.Services.GetRequiredService<CalibrationSnapshotStore>().GetAsync(1001));
        Assert.Equal(context.PredictedRatings.Midfield, stored.PredictedRatings!.Midfield);
        Assert.Equal(context.PredictedRatings.CentralDefense, stored.PredictedRatings.CentralDefense);
        Assert.Equal(MatchPredictionModel.Version, stored.ModelVersion);
        Assert.Equal("captured-before-match", stored.PredictionSource);

        context.PredictedRatings.Midfield += 1;
        var tampered = await client.PostAsJsonAsync("/api/calibration/snapshots/capture", context);
        tampered.EnsureSuccessStatusCode();
        var rejected = await tampered.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.False(rejected.GetProperty("saved").GetBoolean());
        Assert.Single(await app.Services.GetRequiredService<CalibrationSnapshotStore>().GetAsync(1001));
    }

    private WebApplicationFactory<Program> Configure(bool mock) => _factory.WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UseMockData"] = mock.ToString(),
            ["PlayerHistory:StoragePath"] = Path.Combine(_root, "history"),
            ["Calibration:StoragePath"] = Path.Combine(_root, "calibration"),
            ["ConnectionStrings:HattrickDb"] = ""
        })));

    public void Dispose()
    {
        _factory.Dispose();
        var resolved = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hattrick-history-api-tests"))
            || !Guid.TryParse(Path.GetFileName(resolved), out _)) throw new InvalidOperationException("Unsafe cleanup path.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
