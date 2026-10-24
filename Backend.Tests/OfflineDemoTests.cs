using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HattrickAnalizer.Services;
using Microsoft.Extensions.Configuration;

namespace Backend.Tests;

public class OfflineDemoTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    public OfflineDemoTests(ApiTestFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/oauth/current")]
    [InlineData("/api/oauth/my-teams")]
    [InlineData("/api/team/next-opponent")]
    [InlineData("/api/team/upcoming-matches")]
    [InlineData("/api/team/1001")]
    [InlineData("/api/team/1002/scout")]
    [InlineData("/api/training/summary")]
    [InlineData("/api/league/simulation")]
    public async Task Demo_read_endpoints_work_without_cookies_or_live_credentials(string route)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "ht_session=real-session-must-not-change");
        var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (route.EndsWith("current"))
        {
            Assert.True(json.GetProperty("authorized").GetBoolean());
            Assert.True(json.GetProperty("mockMode").GetBoolean());
            Assert.Equal(1001, json.GetProperty("ownTeamId").GetInt32());
        }
        if (route.EndsWith("scout")) Assert.Equal(5, json.GetProperty("matchesAnalyzed").GetInt32());
        if (route.EndsWith("upcoming-matches"))
        {
            Assert.Equal(2, json.GetArrayLength());
            Assert.Equal("1", json[0].GetProperty("matchType").GetString());
            Assert.Equal("4", json[1].GetProperty("matchType").GetString());
            Assert.NotEqual(json[0].GetProperty("matchId").GetInt64(), json[1].GetProperty("matchId").GetInt64());
        }
        if (route.EndsWith("summary"))
        {
            Assert.NotEmpty(json.GetProperty("players").EnumerateArray());
            Assert.True(json.GetProperty("nextTrainingAt").GetDateTime() > DateTime.UtcNow);
        }
    }

    [Fact]
    public async Task Demo_transport_never_calls_HTTP_for_reads_writes_auth_or_unsupported_files()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["UseMockData"] = "true" }).Build();
        using var client = new HttpClient(new FailOnHttp());
        var oauth = new OAuthService(config, client);
        var xml = await oauth.MakeAuthenticatedRequestAsync("real-token", "real-secret", new() { ["file"] = "training" });
        Assert.Contains("MockData", xml);
        await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.MakeAuthenticatedRequestAsync("real-token", "real-secret",
            new() { ["file"] = "matchorders", ["actionType"] = "setmatchorder" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.MakeAuthenticatedRequestAsync("real-token", "real-secret",
            new() { ["file"] = "unknown-resource" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.GetRequestTokenAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.ExchangeRequestTokenAsync("a", "b", "c"));
    }

    private sealed class FailOnHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Offline demo attempted a network request.");
    }
}
