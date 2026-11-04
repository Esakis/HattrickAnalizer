using System.Net;
using System.Text.Json;
using HattrickAnalizer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class CompleteMatchOrdersTests
{
    [Fact]
    public void Payload_uses_fixed_position_arrays_and_preserves_bench_kickers_and_conditional_order()
    {
        var request = Request();
        request.Bench = new() { new() { PlayerId = 12, PositionCode = 4 }, new() { PlayerId = 12, PositionCode = 13 } };
        request.Kickers = new() { 11, 12, 1 };
        request.Substitutions = new() { new() { PlayerOutId = 11, PlayerInId = 12, Minute = 65, Position = -1, Behaviour = -1, Standing = 2 } };
        using var json = JsonDocument.Parse(MatchOrdersService.BuildLineupJson(request));
        var root = json.RootElement;
        Assert.Equal(14, root.GetProperty("positions").GetArrayLength());
        Assert.Equal(11, root.GetProperty("positions").EnumerateArray().Count(p => p.GetProperty("id").GetInt32() > 0));
        Assert.Equal(14, root.GetProperty("bench").GetArrayLength());
        Assert.Equal(12, root.GetProperty("bench")[4].GetProperty("id").GetInt32());
        Assert.Equal(12, root.GetProperty("bench")[13].GetProperty("id").GetInt32());
        Assert.Equal(11, root.GetProperty("kickers").GetArrayLength());
        Assert.Equal(11, root.GetProperty("kickers")[0].GetProperty("id").GetInt32());
        Assert.Equal(12, root.GetProperty("kickers")[1].GetProperty("id").GetInt32());
        Assert.Equal("1", root.GetProperty("captain").GetString());
        var substitution = Assert.Single(root.GetProperty("substitutions").EnumerateArray());
        Assert.Equal("12", substitution.GetProperty("playerin").GetString());
        Assert.Equal("11", substitution.GetProperty("playerout").GetString());
        Assert.Equal("65", substitution.GetProperty("min").GetString());
        Assert.Equal("2", substitution.GetProperty("standing").GetString());
        Assert.Equal("-1", substitution.GetProperty("card").GetString());
    }

    [Theory]
    [InlineData("starter-on-bench")]
    [InlineData("duplicate-bench-slot")]
    [InlineData("foreign-kicker")]
    [InlineData("foreign-sub")]
    [InlineData("invalid-minute")]
    [InlineData("illegal-behaviour")]
    [InlineData("backup-without-primary")]
    public void Invalid_extended_orders_cannot_be_serialized(string invalidCase)
    {
        var request = Request();
        request.Bench.Add(new() { PlayerId = 12, PositionCode = 0 });
        switch (invalidCase)
        {
            case "starter-on-bench": request.Bench[0].PlayerId = 1; break;
            case "duplicate-bench-slot": request.Bench.Add(new() { PlayerId = 13, PositionCode = 0 }); break;
            case "foreign-kicker": request.Kickers.Add(99); break;
            case "foreign-sub": request.Substitutions.Add(new() { PlayerInId = 99, PlayerOutId = 1 }); break;
            case "invalid-minute": request.Substitutions.Add(new() { PlayerInId = 12, PlayerOutId = 1, Minute = 66 }); break;
            case "illegal-behaviour": request.Substitutions.Add(new() { PlayerInId = 12, PlayerOutId = 1, Position = 0, Behaviour = 1 }); break;
            case "backup-without-primary": request.Bench[0].PositionCode = 7; break;
        }
        Assert.NotNull(MatchOrdersService.ValidateRequest(request));
        Assert.Throws<ArgumentException>(() => MatchOrdersService.BuildLineupJson(request));
    }

    [Theory]
    [InlineData("mock", 0)]
    [InlineData("foreign-match", 1)]
    [InlineData("closed-match", 1)]
    [InlineData("foreign-player", 2)]
    [InlineData("injured", 2)]
    [InlineData("unknown-status", 2)]
    public async Task Failed_preflight_never_sends_a_write(string failure, int expectedReads)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UseMockData"] = (failure == "mock").ToString(), ["HattrickApi:ConsumerKey"] = "fixture",
            ["HattrickApi:ConsumerSecret"] = "fixture", ["ConnectionStrings:HattrickDb"] = ""
        }).Build();
        var handler = new PreflightHandler(failure);
        using var client = new HttpClient(handler);
        var oauth = new OAuthService(config, client);
        var store = new TokenStore(config, new EphemeralDataProtectionProvider(), NullLogger<TokenStore>.Instance);
        store.Save("fixture", new() { OwnTeamId = 10, AccessToken = "fixture", AccessTokenSecret = "fixture" });
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "ht_session=fixture";
        var accessor = new HttpContextAccessor { HttpContext = context };
        var api = new HattrickApiService(client, config, oauth, store, accessor, NullLogger<HattrickApiService>.Instance);
        var service = new MatchOrdersService(oauth, store, accessor, NullLogger<MatchOrdersService>.Instance, api);
        var result = await service.SendLineupAsync(Request());
        Assert.False(result.Success);
        Assert.Equal(expectedReads, handler.Reads);
        Assert.Equal(0, handler.Writes);
    }

    [Fact]
    public void Consecutive_substitutions_and_same_player_behaviour_changes_are_serialized()
    {
        var request = Request();
        request.Bench = new() { new() { PlayerId = 12, PositionCode = 0 }, new() { PlayerId = 13, PositionCode = 6 } };
        request.Substitutions = new() {
            new() { PlayerOutId=1, PlayerInId=12, Minute=46 },
            new() { PlayerOutId=12, PlayerInId=12, Minute=60, Behaviour=0 },
            new() { PlayerOutId=12, PlayerInId=13, Minute=75 }
        };
        Assert.Null(MatchOrdersService.ValidateRequest(request));
        using var json = JsonDocument.Parse(MatchOrdersService.BuildLineupJson(request));
        Assert.Equal("1", json.RootElement.GetProperty("substitutions")[1].GetProperty("orderType").GetString());
        request.Substitutions.Add(new() { PlayerOutId=13, PlayerInId=12, Minute=85 });
        Assert.NotNull(MatchOrdersService.ValidateRequest(request));
    }

    [Fact]
    public void Inherited_position_cannot_use_an_illegal_behaviour()
    {
        var request=Request();
        request.Substitutions.Add(new() { PlayerOutId=1, PlayerInId=1, Minute=46, Behaviour=1 });
        Assert.NotNull(MatchOrdersService.ValidateRequest(request));
    }

    private static MatchOrdersRequest Request() => new()
    {
        MatchId = 123, CaptainId = 1, SetPiecesTakerId = 1,
        Positions = FormationData.Formations["4-4-2"].Positions.Select((slot, i) => (slot, i))
            .ToDictionary(x => x.slot, x => new MatchOrderSlot { PlayerId = x.i + 1, Behaviour = x.slot })
    };

    private sealed class PreflightHandler(string failure) : HttpMessageHandler
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("setmatchorder", StringComparison.Ordinal)) Writes++; else Reads++;
            string xml;
            if (url.Contains("file=players", StringComparison.Ordinal))
                xml = "<HattrickData><PlayerList>" + string.Concat(Enumerable.Range(failure == "foreign-player" ? 2 : 1, 11).Select(i =>
                    $"<Player><PlayerID>{i}</PlayerID><InjuryLevel>{(failure == "injured" ? 2 : -1)}</InjuryLevel>{(failure == "unknown-status" ? "" : "<Cards>0</Cards>")}</Player>")) + "</PlayerList></HattrickData>";
            else
                xml = $"<HattrickData><MatchData Available=\"{(failure == "closed-match" ? "False" : "True")}\"><HomeTeam><HomeTeamID>{(failure == "foreign-match" ? 30 : 10)}</HomeTeamID></HomeTeam><AwayTeam><AwayTeamID>20</AwayTeamID></AwayTeam></MatchData></HattrickData>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) });
        }
    }
}
