using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HattrickAnalizer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Backend.Tests;

public sealed class OAuthPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hattrick-oauth-tests", Guid.NewGuid().ToString("N"));
    private string Sessions => Path.Combine(_root, "sessions");
    private IDataProtectionProvider Protection() => DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys")),
        builder => builder.SetApplicationName("OAuthPersistenceTests"));
    private TokenStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["OAuth:StorageProvider"] = "File", ["OAuth:StoragePath"] = Sessions,
        // File mode must not attempt SQL even when an old local connection string exists.
        ["ConnectionStrings:HattrickDb"] = "invalid connection string"
    }).Build(), Protection(), NullLogger<TokenStore>.Instance);
    private static StoredToken Token(int teamId = 42) => new()
    {
        AccessToken = "test-access-token", AccessTokenSecret = "test-access-secret",
        OwnTeamId = teamId, OwnTeamName = "Test team", AuthorizedAt = DateTime.UtcNow
    };

    [Fact]
    public void Local_development_uses_persistent_files_by_default_without_changing_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:HattrickDb"] = "invalid connection string"
        }).Build();
        var environment = new LocalEnvironment(_root);
        new TokenStore(config, Protection(), NullLogger<TokenStore>.Instance, environment).Save("one", Token());
        var restarted = new TokenStore(config, Protection(), NullLogger<TokenStore>.Instance, environment);
        Assert.True(restarted.IsAuthorized("one"));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "App_Data", "oauth"), "*.protected"));
    }

    [Fact]
    public void Tokens_are_encrypted_and_survive_new_store_and_key_provider_instances()
    {
        Store().Save("browser-one", Token());
        var saved = File.ReadAllText(Assert.Single(Directory.GetFiles(Sessions)));
        Assert.DoesNotContain("test-access", saved);
        Assert.DoesNotContain("Test team", saved);
        Assert.DoesNotContain("browser-one", Path.GetFileName(Assert.Single(Directory.GetFiles(Sessions))));
        var restored = Store().Get("browser-one");
        Assert.NotNull(restored);
        Assert.Equal("test-access-token", restored.AccessToken);
        Assert.Equal("test-access-secret", restored.AccessTokenSecret);
        Assert.Equal(42, restored.OwnTeamId);
        Assert.Null(Store().Get("another-browser"));
    }

    [Fact]
    public void Team_selection_and_logout_survive_restart_and_do_not_affect_other_sessions()
    {
        var first = Store();
        first.Save("one", Token());
        first.Save("two", Token(77));
        var restarted = Store();
        var token = restarted.Get("one")!;
        token.OwnTeamId = 99;
        restarted.Save("one", token);
        Assert.Equal(99, Store().Get("one")!.OwnTeamId);
        Store().Clear("one");
        Assert.False(Store().IsAuthorized("one"));
        Assert.True(Store().IsAuthorized("two"));
        Assert.Equal(77, Store().Get("two")!.OwnTeamId);
    }

    [Fact]
    public void Pending_authorization_survives_restart_and_is_removed_after_completion_or_clear()
    {
        Store().SavePending("one", new() { RequestToken = "request-token", RequestTokenSecret = "request-secret" });
        Assert.Equal("request-secret", Store().GetPending("one")!.RequestTokenSecret);
        Assert.False(Store().IsAuthorized("one"));
        Store().Save("one", Token());
        Assert.Null(Store().GetPending("one"));
        Store().SavePending("one", new() { RequestToken = "second", RequestTokenSecret = "second-secret" });
        Store().ClearPending("one");
        Assert.Null(Store().GetPending("one"));
        Assert.True(Store().IsAuthorized("one"));
    }

    [Fact]
    public void Expired_sessions_are_rejected_and_active_sessions_extend_their_persisted_expiry()
    {
        var files = new LocalOAuthStore(Sessions, Protection(), NullLogger.Instance);
        var expired = Token();
        expired.LastUsedAt = DateTime.UtcNow.AddDays(-31);
        files.Update("expired", session => session.Token = expired);
        Assert.False(Store().IsAuthorized("expired"));
        Assert.Null(Store().Get("expired"));
        var active = Token();
        active.LastUsedAt = DateTime.UtcNow.AddDays(-10);
        files.Update("active", session => session.Token = active);
        Assert.True(Store().IsAuthorized("active"));
        Assert.True(files.Read("active")!.Token!.LastUsedAt > DateTime.UtcNow.AddMinutes(-1));
        files.Update("pending", session => session.Pending = new PendingAuth { CreatedAt = DateTime.UtcNow.AddHours(-2) });
        Assert.Null(Store().GetPending("pending"));
        Assert.Null(files.Read("pending"));
    }

    [Fact]
    public void Corrupted_files_do_not_authenticate_and_can_be_replaced_by_a_new_authorization()
    {
        Store().Save("one", Token());
        File.WriteAllText(Assert.Single(Directory.GetFiles(Sessions)), "corrupted");
        Assert.False(Store().IsAuthorized("one"));
        Store().Save("one", Token());
        Assert.True(Store().IsAuthorized("one"));
    }

    [Fact]
    public void Failed_persistence_does_not_claim_successful_authorization()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Sessions, "this path is a file");
        var store = Store();
        Assert.ThrowsAny<IOException>(() => store.Save("one", Token()));
        Assert.False(store.IsAuthorized("one"));
    }

    [Theory]
    [InlineData("http://localhost", "Development", false, "lax")]
    [InlineData("http://127.0.0.1", "Development", false, "lax")]
    [InlineData("https://localhost", "Development", true, "none")]
    [InlineData("http://localhost", "Production", true, "none")]
    public async Task OAuth_survives_a_server_restart_with_correct_cookies_and_logout(string origin, string environment,
        bool secure, string sameSite)
    {
        string cookie;
        using (var app = new SessionFactory(_root, environment))
        using (var client = app.CreateClient(new() { BaseAddress = new Uri(origin), HandleCookies = false }))
        {
            var start = await client.GetAsync("/api/oauth/start");
            start.EnsureSuccessStatusCode();
            var setCookie = Assert.Single(start.Headers.GetValues("Set-Cookie"));
            Assert.Contains("httponly", setCookie);
            Assert.Equal(secure, setCookie.Contains("; secure"));
            Assert.Contains("samesite=" + sameSite, setCookie);
            Assert.Contains("max-age=2592000", setCookie);
            cookie = setCookie.Split(';')[0];
            var startBody = await start.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            var complete = await client.PostAsJsonAsync("/api/oauth/complete", new
            {
                sessionId = startBody.GetProperty("sessionId").GetString(), verifier = "test-pin"
            });
            complete.EnsureSuccessStatusCode();
            Assert.DoesNotContain("test-access", await complete.Content.ReadAsStringAsync());
        }

        using (var app = new SessionFactory(_root, environment))
        using (var client = app.CreateClient(new() { BaseAddress = new Uri(origin), HandleCookies = false }))
        {
            var anonymous = await client.GetFromJsonAsync<JsonElement>("/api/oauth/current");
            Assert.False(anonymous.GetProperty("authorized").GetBoolean());
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            var current = await client.GetAsync("/api/oauth/current");
            var body = await current.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("authorized").GetBoolean());
            Assert.False(body.GetProperty("mockMode").GetBoolean());
            Assert.Equal(42, body.GetProperty("ownTeamId").GetInt32());
            Assert.DoesNotContain("test-access", await current.Content.ReadAsStringAsync());
            Assert.Contains("max-age=2592000", Assert.Single(current.Headers.GetValues("Set-Cookie")));
            var logout = await client.PostAsJsonAsync("/api/oauth/logout", new { });
            logout.EnsureSuccessStatusCode();
            Assert.Contains("expires=Thu, 01 Jan 1970", Assert.Single(logout.Headers.GetValues("Set-Cookie")));
        }
        using (var app = new SessionFactory(_root, environment))
        using (var client = app.CreateClient(new() { BaseAddress = new Uri(origin), HandleCookies = false }))
        {
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            var current = await client.GetFromJsonAsync<JsonElement>("/api/oauth/current");
            Assert.False(current.GetProperty("authorized").GetBoolean());
        }
    }

    private sealed class SessionFactory(string root, string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UseMockData"] = "false", ["OAuth:StorageProvider"] = "File",
                ["OAuth:StoragePath"] = Path.Combine(root, "sessions"), ["ConnectionStrings:HattrickDb"] = "",
                ["HattrickApi:ConsumerKey"] = "test-key", ["HattrickApi:ConsumerSecret"] = "test-secret"
            }));
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")))
                    .SetApplicationName("OAuthPersistenceTests");
                services.AddHttpClient<OAuthService>().ConfigurePrimaryHttpMessageHandler(() => new OAuthHandler());
            });
        }
    }

    private sealed class OAuthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var text = request.RequestUri!.AbsolutePath switch
            {
                "/oauth/request_token.ashx" => "oauth_token=request-token&oauth_token_secret=request-secret",
                "/oauth/access_token.ashx" => "oauth_token=test-access-token&oauth_token_secret=test-access-secret",
                "/chppxml.ashx" => "<HattrickData><Team><TeamID>42</TeamID><TeamName>Test team</TeamName></Team></HattrickData>",
                _ => throw new InvalidOperationException("Unexpected network request in OAuth test")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
        }
    }

    private sealed class LocalEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "OAuthPersistenceTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hattrick-oauth-tests"));
        var path = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(path) != parent || !Guid.TryParse(Path.GetFileName(path), out _))
            throw new InvalidOperationException("Unexpected test directory.");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
