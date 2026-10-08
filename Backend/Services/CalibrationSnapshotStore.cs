using System.Text.Json;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>
/// Small per-team JSON snapshot store. Register this service as a DI singleton: its semaphore
/// serializes updates and reads for the process. Each write uses a unique same-directory temp
/// file followed by atomic replacement so readers never observe partial JSON.
/// </summary>
public sealed class CalibrationSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CalibrationSnapshotStore(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration["Calibration:StoragePath"];
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "calibration")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        _root = Path.GetFullPath(path);
    }

    public async Task<IReadOnlyList<CalibrationSnapshot>> GetAsync(int teamId, CancellationToken cancellationToken = default)
    {
        if (teamId <= 0) throw new ArgumentOutOfRangeException(nameof(teamId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = TeamPath(teamId);
            if (!File.Exists(path)) return Array.Empty<CalibrationSnapshot>();
            await using var stream = File.OpenRead(path);
            var snapshots = await JsonSerializer.DeserializeAsync<List<CalibrationSnapshot>>(stream, JsonOptions, cancellationToken)
                ?? new List<CalibrationSnapshot>();
            foreach (var snapshot in snapshots) Normalize(snapshot);
            return snapshots;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(CalibrationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Validate(snapshot);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_root);
            var path = TeamPath(snapshot.TeamId);
            var existing = new List<CalibrationSnapshot>();
            if (File.Exists(path))
            {
                await using var read = File.OpenRead(path);
                existing = await JsonSerializer.DeserializeAsync<List<CalibrationSnapshot>>(read, JsonOptions, cancellationToken)
                    ?? new List<CalibrationSnapshot>();
            }
            if (snapshot.Context.MatchId.HasValue)
                existing.RemoveAll(s => s.Context.MatchId == snapshot.Context.MatchId && s.RecordedAt == snapshot.RecordedAt);
            existing.Add(snapshot);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var write = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                    await JsonSerializer.SerializeAsync(write, existing.OrderBy(s => s.RecordedAt).ToList(), JsonOptions, cancellationToken);
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _gate.Release(); }
    }

    private string TeamPath(int teamId) => Path.Combine(_root, $"team-{teamId}.json");

    private static void Normalize(CalibrationSnapshot snapshot)
    {
        snapshot.Players ??= new List<Player>();
        snapshot.Context ??= new CalibrationMatchContext();
        snapshot.Context.PlayerIdsBySlot ??= new Dictionary<string, int>();
        snapshot.Context.BehavioursBySlot ??= new Dictionary<string, string>();
        snapshot.Warnings ??= new List<string>();
    }

    private static void Validate(CalibrationSnapshot snapshot)
    {
        if (snapshot.TeamId <= 0) throw new ArgumentException("A positive own-team id is required.");
        if (snapshot.RecordedAt == default || snapshot.RecordedAt > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new ArgumentException("Snapshot timestamp must be present and cannot be in the future.");
        if (snapshot.Players.Select(p => p.PlayerId).Distinct().Count() != snapshot.Players.Count)
            throw new ArgumentException("Snapshot player ids must be unique.");
        var c = snapshot.Context ?? throw new ArgumentException("Snapshot match context must not be null.");
        var hasAnyContext = c.MatchId.HasValue || c.MatchDate.HasValue || c.IsHomeMatch.HasValue || c.Tactic != null
            || c.Attitude != null || c.CoachType != null || c.TeamSpiritLevel.HasValue || c.ConfidenceLevel.HasValue
            || c.Formation != null || c.FormationExperience.HasValue || c.WeatherId.HasValue
            || c.AssistantManagerLevel.HasValue
            || c.PlayerIdsBySlot.Count > 0 || c.BehavioursBySlot.Count > 0;
        if (hasAnyContext)
        {
            if (!snapshot.Context.IsComplete)
                throw new ArgumentException("A match snapshot must include complete, valid context and exactly 11 legal unique slots including GK.");
            if (snapshot.Context.MatchDate.HasValue && snapshot.Context.MatchDate.Value < snapshot.RecordedAt)
                throw new ArgumentException("A pre-match snapshot cannot be recorded after kickoff.");
            var rosterIds = snapshot.Players.Select(p => p.PlayerId).ToHashSet();
            if (snapshot.Context.PlayerIdsBySlot.Values.Any(id => !rosterIds.Contains(id)))
                throw new ArgumentException("Every match player must be present in the captured roster.");
            if (snapshot.Context.PlayerIdsBySlot.Values.Any(id => snapshot.Players.First(p => p.PlayerId == id) is not { SkillsAvailable: true }))
                throw new ArgumentException("Every captured lineup player must have all private skills available.");
        }
    }
}
