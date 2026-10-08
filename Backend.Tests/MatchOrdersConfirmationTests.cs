using System.Net;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class MatchOrdersConfirmationTests
{
    [Theory]
    [InlineData("<HattrickData><MatchData OrdersSet=\"True\"><Reason>confirmed</Reason></MatchData></HattrickData>", true, "")]
    [InlineData("<HattrickData><MatchData OrdersSet=\"False\"><Reason>orders closed</Reason></MatchData></HattrickData>", false, "orders closed")]
    [InlineData("<HattrickData><MatchData><Reason>missing state</Reason></MatchData></HattrickData>", false, "missing state")]
    [InlineData("<HattrickData><MatchData OrdersSet=\"maybe\"><Reason>malformed flag</Reason></MatchData></HattrickData>", false, "malformed flag")]
    [InlineData("<HattrickData><MatchData OrdersSet=\"True\"></HattrickData>", false, "invalid response")]
    public async Task Match_order_result_requires_a_valid_orders_set_attribute(string responseXml, bool expectedSuccess, string expectedError)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HattrickApi:ConsumerKey"] = "fixture-consumer",
            ["HattrickApi:ConsumerSecret"] = "fixture-secret",
            ["ConnectionStrings:HattrickDb"] = ""
        }).Build();
        var handler = new FixtureHandler(responseXml);
        using var client = new HttpClient(handler);
        var oauth = new OAuthService(configuration, client);
        var tokenStore = new TokenStore(configuration, new EphemeralDataProtectionProvider(), NullLogger<TokenStore>.Instance);
        tokenStore.Save("test-session", new StoredToken { AccessToken = "fixture-token", AccessTokenSecret = "fixture-token-secret" });
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "ht_session=test-session";
        var service = new MatchOrdersService(oauth, tokenStore, new HttpContextAccessor { HttpContext = context },
            NullLogger<MatchOrdersService>.Instance);

        var result = await service.SendLineupAsync(ValidRequest());

        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("file=matchorders", handler.LastRequestUri, StringComparison.Ordinal);
        if (!expectedSuccess) Assert.Contains(expectedError, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static MatchOrdersRequest ValidRequest() => new()
    {
        MatchId = 24680,
        Tactic = "Normal",
        Attitude = "Normal",
        AssistantManagerLevel = 0,
        Positions = FormationData.Formations["4-4-2"].Positions.ToDictionary(slot => slot, slot => new MatchOrderSlot
        {
            PlayerId = Array.IndexOf(FormationData.Formations["4-4-2"].Positions, slot) + 1,
            Behaviour = slot
        }, StringComparer.Ordinal),
        CaptainId = 1, SetPiecesTakerId = 1
    };

    private sealed class FixtureHandler(string responseXml) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string LastRequestUri { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseXml, System.Text.Encoding.UTF8, "application/xml")
            });
        }
    }
}
