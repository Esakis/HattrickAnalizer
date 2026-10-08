using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests;

public class ApiSmokeTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public ApiSmokeTests(ApiTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Mocked_startup_resolves_services_and_optimizer_endpoint_returns_a_real_XI()
    {
        using var scope = _factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AdvancedLineupOptimizer>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CalibrationService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CalibrationSnapshotStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TrainingService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MatchOrdersService>());

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/optimizer/optimize", new OptimizerRequest
        {
            MyTeamId = 10,
            OpponentTeamId = 20,
            PreferredFormation = "4-4-2",
            PreferredTactic = "Normal",
            FormationExperience = new Dictionary<string, int> { ["4-4-2"] = 8 },
            TeamSpiritLevel = 5,
            ConfidenceLevel = 5,
            OpponentScenario = new OpponentScenario
            {
                Tactic = "Normal",
                Ratings = new TeamRatings
                {
                    MidfieldRating = 8, RightDefenseRating = 8, CentralDefenseRating = 8, LeftDefenseRating = 8,
                    RightAttackRating = 8, CentralAttackRating = 8, LeftAttackRating = 8
                }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(11, json.GetProperty("optimalLineup").GetProperty("positions").EnumerateObject().Count());
        Assert.Equal(1, json.GetProperty("alternatives").GetArrayLength());
    }

    [Fact]
    public async Task Domain_validation_errors_are_structured_400_responses()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/optimizer/optimize", new OptimizerRequest
        {
            MyTeamId = 1, OpponentTeamId = 2, Objective = "not-an-objective"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Contains("Objective", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Invalid_orders_are_rejected_as_400_without_CHPP_access()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/matchorders", new MatchOrdersRequest
        {
            MatchId = 123,
            Positions = new Dictionary<string, MatchOrderSlot>(),
            Tactic = "Normal",
            Attitude = "Normal"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("validation", body.GetProperty("rawResponse").GetString());
    }
}
