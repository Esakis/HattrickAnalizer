using HattrickAnalizer.Models;
using HattrickAnalizer.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public sealed class PlayerHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hattrick-history-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task No_database_history_survives_restart_deduplicates_and_keeps_utc_time()
    {
        var service = Create();
        var player = Player(1);
        var before = DateTime.UtcNow;
        var first = Assert.Single(await service.CheckAndSaveChangesAsync(new() { player }, 11));
        Assert.True(first.FirstSnapshot);
        Assert.False(Assert.Single(await service.CheckAndSaveChangesAsync(new() { player }, 11)).Changed);
        player.Skills.Playmaking++;
        var change = Assert.Single(await service.CheckAndSaveChangesAsync(new() { player }, 11));
        Assert.Equal(new PlayerFieldChange("playmaking", 7, 8), Assert.Single(change.Fields));

        var restarted = Create();
        var history = await restarted.GetPlayerHistoryAsync(1, 11);
        Assert.Equal(2, history.Count);
        Assert.All(history, snapshot => {
            Assert.Equal(DateTimeKind.Utc, snapshot.RecordedDate.Kind);
            Assert.InRange(snapshot.RecordedDate, before, DateTime.UtcNow);
        });
        Assert.Equal(2, (await restarted.GetRecentChangesAsync(11)).Count);
        Assert.Equal(8, history.Last().Playmaking);
    }

    [Fact]
    public async Task Same_player_is_isolated_by_team_and_parallel_sync_does_not_duplicate()
    {
        var service = Create();
        var player = Player(1);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.CheckAndSaveChangesAsync(new() { player }, 11)));
        Assert.Single(await service.GetPlayerHistoryAsync(1, 11));
        Assert.Empty(await service.GetPlayerHistoryAsync(1, 22));
        Assert.Empty(await service.GetRecentChangesAsync(22));
        player.Skills.Playmaking = 12;
        Assert.True(Assert.Single(await service.CheckAndSaveChangesAsync(new() { player }, 22)).FirstSnapshot);
        Assert.Equal(7, Assert.Single(await service.GetPlayerHistoryAsync(1, 11)).Playmaking);
        Assert.Equal(12, Assert.Single(await service.GetPlayerHistoryAsync(1, 22)).Playmaking);
    }

    [Fact]
    public async Task Unavailable_sql_uses_local_store_and_missing_skills_are_never_recorded_as_zero()
    {
        var service = Create("invalid connection string");
        var player = Player(1);
        player.Skills.KeeperAvailable = false;
        var result = Assert.Single(await service.CheckAndSaveChangesAsync(new() { player }, 11));
        Assert.False(result.Changed);
        Assert.NotEmpty(result.Warnings);
        Assert.Empty(await service.GetPlayerHistoryAsync(1, 11));
        player.Skills.KeeperAvailable = true;
        await service.CheckAndSaveChangesAsync(new() { player }, 11);
        Assert.Single(await Create().GetPlayerHistoryAsync(1, 11));
    }

    private PlayerHistoryService Create(string? connectionString = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PlayerHistory:StoragePath"] = _root,
            ["ConnectionStrings:HattrickDb"] = connectionString
        }).Build();
        return new PlayerHistoryService(config, new TestEnvironment(_root), NullLogger<PlayerHistoryService>.Instance);
    }

    private static Player Player(int id) => new()
    {
        PlayerId = id, FirstName = "Test", LastName = "Player", Stamina = 7, StaminaAvailable = true,
        Form = 7, InjuryLevel = -1, InjuryStatusKnown = true,
        Skills = new()
        {
            Keeper = 1, KeeperAvailable = true, Defending = 6, DefendingAvailable = true,
            Playmaking = 7, PlaymakingAvailable = true, Winger = 5, WingerAvailable = true,
            Passing = 6, PassingAvailable = true, Scoring = 5, ScoringAvailable = true,
            SetPieces = 3, SetPiecesAvailable = true
        }
    };

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hattrick-history-tests"));
        if (Path.GetDirectoryName(resolved) != parent || !Guid.TryParse(Path.GetFileName(resolved), out _))
            throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Backend.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
