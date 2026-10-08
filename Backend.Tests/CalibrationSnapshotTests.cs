using System.Reflection;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Backend.Tests;

public class CalibrationSnapshotTests
{
    [Fact]
    public async Task Snapshot_round_trip_keeps_capture_immutable_and_rejects_post_kickoff_capture()
    {
        var root = Path.Combine(Path.GetTempPath(), "hattrick-snapshot-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = CreateStore(root);
            var recordedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var snapshot = CompleteSnapshot(55, recordedAt, DateTimeOffset.UtcNow.AddMinutes(1));

            await store.SaveAsync(snapshot);
            snapshot.Players[0].Skills.Keeper = 19;
            var read = Assert.Single(await store.GetAsync(55));

            Assert.Equal(1, read.Players[0].Skills.Keeper);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
                CompleteSnapshot(56, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(-1))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Relative_storage_path_is_resolved_from_application_content_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "hattrick-snapshot-tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Calibration:StoragePath"] = Path.Combine("App_Data", "calibration")
        }).Build();
        var environment = new TestHostEnvironment { ContentRootPath = root };

        try
        {
            var store = new CalibrationSnapshotStore(configuration, environment);
            var path = typeof(CalibrationSnapshotStore).GetField("_root", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert.Equal(Path.GetFullPath(Path.Combine(root, "App_Data", "calibration")), path.GetValue(store));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Calibration_temporal_holdout_does_not_leak_future_actuals_into_training_ratios()
    {
        var aggregate = typeof(CalibrationService).GetMethod("ComputeAggregates", BindingFlags.NonPublic | BindingFlags.Static)!;
        var report = new CalibrationReport
        {
            Matches = Enumerable.Range(1, 10).Select(i => new CalibrationMatchEntry
            {
                MatchId = i,
                MatchDate = new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc),
                Predicted = new LineupRatings { Midfield = 10 },
                Actual = new LineupRatings { Midfield = i <= 8 ? 10 : 100_000 }
            }).ToList()
        };

        aggregate.Invoke(null, new object[] { report });

        Assert.Equal(8, report.TrainingSampleCount);
        Assert.Equal(2, report.HeldOutSampleCount);
        Assert.Equal(1, report.MeanActualToPredictedRatio!.Midfield, 12);
        Assert.Equal(99_990, report.HeldOutMeanAbsoluteError!.Midfield, 8);
    }

    private static CalibrationSnapshotStore CreateStore(string root)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Calibration:StoragePath"] = root
        }).Build();
        var environment = new TestHostEnvironment { ContentRootPath = root };
        return new CalibrationSnapshotStore(configuration, environment);
    }

    private static CalibrationMatchContext Context(long matchId, DateTimeOffset kickoff, Dictionary<string, int> ids) => new()
    {
        MatchId = matchId, MatchDate = kickoff, IsHomeMatch = true, Tactic = "Normal", Attitude = "Normal",
        CoachType = "Neutral", TeamSpiritLevel = 5, ConfidenceLevel = 5, Formation = "4-4-2",
        FormationExperience = 8, WeatherId = 1, AssistantManagerLevel = 0,
        PlayerIdsBySlot = ids, BehavioursBySlot = ids.Keys.ToDictionary(k => k, k => k, StringComparer.Ordinal)
    };

    private static Player CompletePlayer(int id) => new()
    {
        PlayerId = id,
        StaminaAvailable = true,
        Skills = new PlayerSkills
        {
            Keeper = 1, KeeperAvailable = true, Defending = 1, DefendingAvailable = true,
            Playmaking = 1, PlaymakingAvailable = true, Winger = 1, WingerAvailable = true,
            Passing = 1, PassingAvailable = true, Scoring = 1, ScoringAvailable = true,
            SetPieces = 1, SetPiecesAvailable = true
        }
    };

    private static CalibrationSnapshot CompleteSnapshot(int teamId, DateTimeOffset recordedAt, DateTimeOffset kickoff)
    {
        var slots = FormationData.Formations["4-4-2"].Positions;
        var players = slots.Select((_, i) => CompletePlayer(i + 1)).ToList();
        var ids = slots.Select((slot, i) => (slot, id: i + 1)).ToDictionary(x => x.slot, x => x.id, StringComparer.Ordinal);
        return new CalibrationSnapshot
        {
            TeamId = teamId, RecordedAt = recordedAt, Players = players,
            Context = new CalibrationMatchContext
            {
                MatchId = teamId + 1000, MatchDate = kickoff, IsHomeMatch = true, Tactic = "Normal", Attitude = "Normal",
                CoachType = "Neutral", TeamSpiritLevel = 5, ConfidenceLevel = 5, Formation = "4-4-2",
                FormationExperience = 8, WeatherId = 1, AssistantManagerLevel = 0,
                PlayerIdsBySlot = ids, BehavioursBySlot = ids.Keys.ToDictionary(k => k, k => k, StringComparer.Ordinal)
            }
        };
    }

    private sealed class TestHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Backend.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
