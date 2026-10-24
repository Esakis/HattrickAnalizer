using System.Text.Json;
using Microsoft.Data.SqlClient;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

public record PlayerFieldChange(string Field, double Previous, double Current);
public record PlayerChangeResult(int PlayerId, string PlayerName, bool Changed, List<string> ChangedFields)
{
    public int TeamId { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public bool FirstSnapshot { get; init; }
    public List<PlayerFieldChange> Fields { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

/// <summary>Atomic per-team durable history; existing SQL rows are imported when available.</summary>
public class PlayerHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;
    private readonly string? _connectionString;
    private readonly ILogger<PlayerHistoryService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<int> _importedTeams = new();
    private DateTimeOffset _sqlRetryAfter;

    public PlayerHistoryService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<PlayerHistoryService> logger)
    {
        _connectionString = configuration.GetConnectionString("HattrickDb");
        _logger = logger;
        var configured = configuration["PlayerHistory:StoragePath"];
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "player-history")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
        if (configuration.GetValue<bool>("UseMockData"))
        {
            _root = Path.Combine(_root, "demo");
            _connectionString = null;
        }
    }

    public async Task<List<PlayerChangeResult>> CheckAndSaveChangesAsync(List<Player> players, int teamId)
    {
        ValidateTeam(teamId);
        if (players.Any(p => p.PlayerId <= 0) || players.Select(p => p.PlayerId).Distinct().Count() != players.Count)
            throw new ArgumentException("Player ids must be positive and unique.");
        await _gate.WaitAsync();
        try
        {
            var data = await LoadAsync(teamId);
            var results = new List<PlayerChangeResult>();
            var recordedAt = DateTimeOffset.UtcNow;
            foreach (var player in players)
            {
                var name = $"{player.FirstName} {player.LastName}".Trim();
                if (!player.SkillsAvailable)
                {
                    results.Add(new(player.PlayerId, name, false, new())
                    {
                        TeamId = teamId, RecordedAt = recordedAt,
                        Warnings = new() { "Private skills are incomplete; no snapshot was saved." }
                    });
                    continue;
                }
                var last = data.Snapshots.Where(s => s.PlayerId == player.PlayerId).OrderByDescending(s => s.RecordedDate).FirstOrDefault();
                var snapshot = Snapshot(player, teamId, recordedAt.UtcDateTime, last);
                var fields = last == null ? new List<PlayerFieldChange>() : Differences(last, snapshot);
                var gainedAvailability = last != null && ((!last.InjuryStatusKnown && snapshot.InjuryStatusKnown)
                    || (!last.MatchStatsAvailable && snapshot.MatchStatsAvailable));
                var result = new PlayerChangeResult(player.PlayerId, name, last == null || fields.Count > 0 || gainedAvailability,
                    fields.Select(f => $"{f.Field}: {f.Previous} → {f.Current}").ToList())
                {
                    TeamId = teamId, RecordedAt = recordedAt, FirstSnapshot = last == null, Fields = fields
                };
                results.Add(result);
                if (!result.Changed) continue;
                snapshot.Id = data.Snapshots.Count == 0 ? 1 : data.Snapshots.Max(s => s.Id) + 1;
                data.Snapshots.Add(snapshot);
                data.Changes.Add(result);
            }
            await SaveAsync(teamId, data);
            return results;
        }
        finally { _gate.Release(); }
    }

    public async Task<List<PlayerSkillHistory>> GetPlayerHistoryAsync(int playerId, int teamId)
    {
        ValidateTeam(teamId);
        if (playerId <= 0) throw new ArgumentException("A positive player id is required.");
        await _gate.WaitAsync();
        try { return (await LoadAsync(teamId)).Snapshots.Where(s => s.PlayerId == playerId).OrderBy(s => s.RecordedDate).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task<List<PlayerChangeResult>> GetRecentChangesAsync(int teamId, int count = 30)
    {
        ValidateTeam(teamId);
        await _gate.WaitAsync();
        try { return (await LoadAsync(teamId)).Changes.OrderByDescending(c => c.RecordedAt).Take(Math.Clamp(count, 1, 100)).ToList(); }
        finally { _gate.Release(); }
    }

    private async Task<HistoryData> LoadAsync(int teamId)
    {
        var path = TeamPath(teamId);
        var data = new HistoryData();
        if (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            data = await JsonSerializer.DeserializeAsync<HistoryData>(stream, JsonOptions) ?? new();
        }
        // Do not leak old or manually misfiled records across teams.
        data.Snapshots.RemoveAll(s => s.TeamId != teamId);
        data.Changes.RemoveAll(s => s.TeamId != teamId);
        if (!string.IsNullOrWhiteSpace(_connectionString) && !_importedTeams.Contains(teamId) && DateTimeOffset.UtcNow >= _sqlRetryAfter)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(_connectionString) { ConnectTimeout = 2 };
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using var command = new SqlCommand("SELECT * FROM PlayerSkillHistory WHERE TeamId = @team ORDER BY RecordedDate", connection) { CommandTimeout = 3 };
                command.Parameters.AddWithValue("@team", teamId);
                await using var reader = await command.ExecuteReaderAsync();
                var properties = typeof(PlayerSkillHistory).GetProperties().Where(p => p.CanWrite).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadAsync())
                {
                    var row = new PlayerSkillHistory();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (reader.IsDBNull(i) || !properties.TryGetValue(reader.GetName(i), out var property)) continue;
                        property.SetValue(row, Convert.ChangeType(reader.GetValue(i), property.PropertyType));
                    }
                    row.RecordedDate = DateTime.SpecifyKind(row.RecordedDate, DateTimeKind.Utc);
                    if (!data.Snapshots.Any(s => s.PlayerId == row.PlayerId && s.RecordedDate == row.RecordedDate)) data.Snapshots.Add(row);
                }
                _importedTeams.Add(teamId);
                await SaveAsync(teamId, data);
            }
            catch (Exception ex) when (ex is SqlException or ArgumentException or InvalidOperationException)
            {
                _sqlRetryAfter = DateTimeOffset.UtcNow.AddMinutes(5);
                _logger.LogWarning("Legacy SQL history unavailable; durable local history remains active ({ErrorType}).", ex.GetType().Name);
            }
        }
        return data;
    }

    private async Task SaveAsync(int teamId, HistoryData data)
    {
        Directory.CreateDirectory(_root);
        var path = TeamPath(teamId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string TeamPath(int teamId) => Path.Combine(_root, $"team-{teamId}.json");
    private static void ValidateTeam(int teamId) { if (teamId <= 0) throw new ArgumentException("A positive own team id is required."); }

    private static PlayerSkillHistory Snapshot(Player p, int teamId, DateTime recordedAt, PlayerSkillHistory? last) => new()
    {
        PlayerId = p.PlayerId, TeamId = teamId, RecordedDate = recordedAt,
        Keeper = p.Skills.Keeper, Defending = p.Skills.Defending, Playmaking = p.Skills.Playmaking,
        Winger = p.Skills.Winger, Passing = p.Skills.Passing, Scoring = p.Skills.Scoring, SetPieces = p.Skills.SetPieces,
        Form = p.Form, Stamina = p.Stamina, Age = p.Age, TSI = p.TSI, Experience = p.Experience,
        Loyalty = p.Loyalty, Leadership = p.Leadership, InjuryLevel = p.InjuryStatusKnown ? p.InjuryLevel : last?.InjuryLevel ?? -1,
        InjuryStatusKnown = p.InjuryStatusKnown, MatchStatsAvailable = p.MatchStats != null,
        TotalMatches = p.MatchStats?.TotalMatches ?? last?.TotalMatches ?? 0,
        Goals = p.MatchStats?.Goals ?? last?.Goals ?? 0, Assists = p.MatchStats?.Assists ?? last?.Assists ?? 0,
        YellowCards = p.MatchStats?.YellowCards ?? last?.YellowCards ?? 0, RedCards = p.MatchStats?.RedCards ?? last?.RedCards ?? 0,
        AverageRating = p.MatchStats?.AverageRating ?? last?.AverageRating ?? 0,
        AverageForm = p.MatchStats?.AverageForm ?? last?.AverageForm ?? 0,
        MinutesPlayed = p.MatchStats?.MinutesPlayed ?? last?.MinutesPlayed ?? 0
    };

    private static List<PlayerFieldChange> Differences(PlayerSkillHistory last, PlayerSkillHistory current)
    {
        var fields = new[] { "Keeper", "Defending", "Playmaking", "Winger", "Passing", "Scoring", "SetPieces", "Form", "Stamina", "Age", "TSI", "Experience", "Loyalty", "Leadership" }.ToList();
        if (last.InjuryStatusKnown && current.InjuryStatusKnown) fields.Add("InjuryLevel");
        if (last.MatchStatsAvailable && current.MatchStatsAvailable)
            fields.AddRange(new[] { "TotalMatches", "Goals", "Assists", "YellowCards", "RedCards", "AverageRating", "AverageForm", "MinutesPlayed" });
        return fields.Select(field => {
            var property = typeof(PlayerSkillHistory).GetProperty(field)!;
            return new PlayerFieldChange(field == "TSI" ? "tsi" : char.ToLowerInvariant(field[0]) + field[1..], Convert.ToDouble(property.GetValue(last)), Convert.ToDouble(property.GetValue(current)));
        }).Where(change => Math.Abs(change.Previous - change.Current) > 0.001).ToList();
    }

    private sealed class HistoryData
    {
        public List<PlayerSkillHistory> Snapshots { get; set; } = new();
        public List<PlayerChangeResult> Changes { get; set; } = new();
    }
}
