using Microsoft.Data.SqlClient;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

public record PlayerChangeResult(int PlayerId, string PlayerName, bool Changed, List<string> ChangedFields);

public class PlayerHistoryService
{
    private readonly string _connectionString;

    public PlayerHistoryService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("HattrickDb")
            ?? "Server=DESKTOP-UMO1TMH\\SQLEXPRESS;Database=HattrickAnalizer;Trusted_Connection=True;TrustServerCertificate=True;";
    }

    private async Task<PlayerSkillHistory?> GetLastSnapshotAsync(SqlConnection conn, int playerId)
    {
        const string sql = """
            SELECT TOP 1
                Id, PlayerId, TeamId, RecordedDate,
                Keeper, Defending, Playmaking, Winger, Passing, Scoring, SetPieces,
                Form, Stamina, Age, TSI, Experience, Loyalty, Leadership, InjuryLevel,
                TotalMatches, Goals, Assists, YellowCards, RedCards, AverageRating, AverageForm, MinutesPlayed
            FROM PlayerSkillHistory
            WHERE PlayerId = @pid
            ORDER BY RecordedDate DESC
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@pid", playerId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new PlayerSkillHistory
        {
            Id           = r.GetInt32(0),
            PlayerId     = r.GetInt32(1),
            TeamId       = r.GetInt32(2),
            RecordedDate = r.GetDateTime(3),
            Keeper       = r.GetInt32(4),
            Defending    = r.GetInt32(5),
            Playmaking   = r.GetInt32(6),
            Winger       = r.GetInt32(7),
            Passing      = r.GetInt32(8),
            Scoring      = r.GetInt32(9),
            SetPieces    = r.GetInt32(10),
            Form         = r.GetInt32(11),
            Stamina      = r.GetInt32(12),
            Age          = r.GetInt32(13),
            TSI          = r.GetInt32(14),
            Experience   = r.GetInt32(15),
            Loyalty      = r.GetInt32(16),
            Leadership   = r.GetInt32(17),
            InjuryLevel  = r.GetInt32(18),
            TotalMatches = r.GetInt32(19),
            Goals        = r.GetInt32(20),
            Assists      = r.GetInt32(21),
            YellowCards  = r.GetInt32(22),
            RedCards     = r.GetInt32(23),
            AverageRating = r.GetDouble(24),
            AverageForm  = r.GetDouble(25),
            MinutesPlayed = r.GetInt32(26)
        };
    }

    private static List<string> FindChanges(PlayerSkillHistory last, Player p)
    {
        var c = new List<string>();
        var s = p.Skills;
        var m = p.MatchStats;

        if (last.Keeper      != s.Keeper)      c.Add($"Bramkarz: {last.Keeper}→{s.Keeper}");
        if (last.Defending   != s.Defending)   c.Add($"Obrona: {last.Defending}→{s.Defending}");
        if (last.Playmaking  != s.Playmaking)  c.Add($"Rozgrywanie: {last.Playmaking}→{s.Playmaking}");
        if (last.Winger      != s.Winger)      c.Add($"Skrzydło: {last.Winger}→{s.Winger}");
        if (last.Passing     != s.Passing)     c.Add($"Podania: {last.Passing}→{s.Passing}");
        if (last.Scoring     != s.Scoring)     c.Add($"Skuteczność: {last.Scoring}→{s.Scoring}");
        if (last.SetPieces   != s.SetPieces)   c.Add($"Stałe fr.: {last.SetPieces}→{s.SetPieces}");
        if (last.Form        != p.Form)        c.Add($"Forma: {last.Form}→{p.Form}");
        if (last.Stamina     != p.Stamina)     c.Add($"Kondycja: {last.Stamina}→{p.Stamina}");
        if (last.Age         != p.Age)         c.Add($"Wiek: {last.Age}→{p.Age}");
        if (last.TSI         != p.TSI)         c.Add($"TSI: {last.TSI}→{p.TSI}");
        if (last.Experience  != p.Experience)  c.Add($"Doświadczenie: {last.Experience}→{p.Experience}");
        if (last.Loyalty     != p.Loyalty)     c.Add($"Lojalność: {last.Loyalty}→{p.Loyalty}");
        if (last.Leadership  != p.Leadership)  c.Add($"Przywództwo: {last.Leadership}→{p.Leadership}");
        if (last.InjuryLevel != p.InjuryLevel) c.Add($"Kontuzja: {last.InjuryLevel}→{p.InjuryLevel}");

        if (m != null)
        {
            if (last.TotalMatches  != m.TotalMatches)  c.Add($"Mecze: {last.TotalMatches}→{m.TotalMatches}");
            if (last.Goals         != m.Goals)         c.Add($"Gole: {last.Goals}→{m.Goals}");
            if (last.Assists       != m.Assists)       c.Add($"Asysty: {last.Assists}→{m.Assists}");
            if (last.YellowCards   != m.YellowCards)   c.Add($"Żółte kartki: {last.YellowCards}→{m.YellowCards}");
            if (last.RedCards      != m.RedCards)      c.Add($"Czerwone kartki: {last.RedCards}→{m.RedCards}");
            if (Math.Abs(last.AverageRating - m.AverageRating) > 0.01) c.Add($"Śr. ocena: {last.AverageRating:F2}→{m.AverageRating:F2}");
            if (Math.Abs(last.AverageForm   - m.AverageForm)   > 0.01) c.Add($"Śr. forma: {last.AverageForm:F2}→{m.AverageForm:F2}");
            if (last.MinutesPlayed != m.MinutesPlayed) c.Add($"Minuty: {last.MinutesPlayed}→{m.MinutesPlayed}");
        }

        return c;
    }

    private async Task InsertSnapshotAsync(SqlConnection conn, Player p, int teamId)
    {
        var m = p.MatchStats;
        const string sql = """
            INSERT INTO PlayerSkillHistory (
                PlayerId, TeamId, RecordedDate,
                Keeper, Defending, Playmaking, Winger, Passing, Scoring, SetPieces,
                Form, Stamina, Age, TSI, Experience, Loyalty, Leadership, InjuryLevel,
                TotalMatches, Goals, Assists, YellowCards, RedCards, AverageRating, AverageForm, MinutesPlayed
            ) VALUES (
                @pid, @tid, @date,
                @keeper, @defending, @playmaking, @winger, @passing, @scoring, @setPieces,
                @form, @stamina, @age, @tsi, @experience, @loyalty, @leadership, @injuryLevel,
                @totalMatches, @goals, @assists, @yellowCards, @redCards, @averageRating, @averageForm, @minutesPlayed
            )
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@pid",           p.PlayerId);
        cmd.Parameters.AddWithValue("@tid",           teamId);
        cmd.Parameters.AddWithValue("@date",          DateTime.UtcNow.Date);
        cmd.Parameters.AddWithValue("@keeper",        p.Skills.Keeper);
        cmd.Parameters.AddWithValue("@defending",     p.Skills.Defending);
        cmd.Parameters.AddWithValue("@playmaking",    p.Skills.Playmaking);
        cmd.Parameters.AddWithValue("@winger",        p.Skills.Winger);
        cmd.Parameters.AddWithValue("@passing",       p.Skills.Passing);
        cmd.Parameters.AddWithValue("@scoring",       p.Skills.Scoring);
        cmd.Parameters.AddWithValue("@setPieces",     p.Skills.SetPieces);
        cmd.Parameters.AddWithValue("@form",          p.Form);
        cmd.Parameters.AddWithValue("@stamina",       p.Stamina);
        cmd.Parameters.AddWithValue("@age",           p.Age);
        cmd.Parameters.AddWithValue("@tsi",           p.TSI);
        cmd.Parameters.AddWithValue("@experience",    p.Experience);
        cmd.Parameters.AddWithValue("@loyalty",       p.Loyalty);
        cmd.Parameters.AddWithValue("@leadership",    p.Leadership);
        cmd.Parameters.AddWithValue("@injuryLevel",   p.InjuryLevel);
        cmd.Parameters.AddWithValue("@totalMatches",  m?.TotalMatches  ?? 0);
        cmd.Parameters.AddWithValue("@goals",         m?.Goals         ?? 0);
        cmd.Parameters.AddWithValue("@assists",       m?.Assists       ?? 0);
        cmd.Parameters.AddWithValue("@yellowCards",   m?.YellowCards   ?? 0);
        cmd.Parameters.AddWithValue("@redCards",      m?.RedCards      ?? 0);
        cmd.Parameters.AddWithValue("@averageRating", m?.AverageRating ?? 0.0);
        cmd.Parameters.AddWithValue("@averageForm",   m?.AverageForm   ?? 0.0);
        cmd.Parameters.AddWithValue("@minutesPlayed", m?.MinutesPlayed ?? 0);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<PlayerChangeResult>> CheckAndSaveChangesAsync(List<Player> players, int teamId)
    {
        var results = new List<PlayerChangeResult>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        foreach (var player in players)
        {
            try
            {
                var last = await GetLastSnapshotAsync(conn, player.PlayerId);
                var name = $"{player.FirstName} {player.LastName}";

                if (last == null)
                {
                    await InsertSnapshotAsync(conn, player, teamId);
                    results.Add(new PlayerChangeResult(player.PlayerId, name, true, ["Pierwsze zapisanie zawodnika"]));
                    continue;
                }

                var changes = FindChanges(last, player);
                if (changes.Count > 0)
                {
                    await InsertSnapshotAsync(conn, player, teamId);
                    results.Add(new PlayerChangeResult(player.PlayerId, name, true, changes));
                }
                else
                {
                    results.Add(new PlayerChangeResult(player.PlayerId, name, false, []));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerHistory] Błąd dla zawodnika {player.PlayerId}: {ex.Message}");
            }
        }

        return results;
    }

    public async Task<List<PlayerSkillHistory>> GetPlayerHistoryAsync(int playerId)
    {
        const string sql = """
            SELECT
                Id, PlayerId, TeamId, RecordedDate,
                Keeper, Defending, Playmaking, Winger, Passing, Scoring, SetPieces,
                Form, Stamina, Age, TSI, Experience, Loyalty, Leadership, InjuryLevel,
                TotalMatches, Goals, Assists, YellowCards, RedCards, AverageRating, AverageForm, MinutesPlayed
            FROM PlayerSkillHistory
            WHERE PlayerId = @pid
            ORDER BY RecordedDate ASC
            """;

        var result = new List<PlayerSkillHistory>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@pid", playerId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            result.Add(new PlayerSkillHistory
            {
                Id            = r.GetInt32(0),
                PlayerId      = r.GetInt32(1),
                TeamId        = r.GetInt32(2),
                RecordedDate  = r.GetDateTime(3),
                Keeper        = r.GetInt32(4),
                Defending     = r.GetInt32(5),
                Playmaking    = r.GetInt32(6),
                Winger        = r.GetInt32(7),
                Passing       = r.GetInt32(8),
                Scoring       = r.GetInt32(9),
                SetPieces     = r.GetInt32(10),
                Form          = r.GetInt32(11),
                Stamina       = r.GetInt32(12),
                Age           = r.GetInt32(13),
                TSI           = r.GetInt32(14),
                Experience    = r.GetInt32(15),
                Loyalty       = r.GetInt32(16),
                Leadership    = r.GetInt32(17),
                InjuryLevel   = r.GetInt32(18),
                TotalMatches  = r.GetInt32(19),
                Goals         = r.GetInt32(20),
                Assists       = r.GetInt32(21),
                YellowCards   = r.GetInt32(22),
                RedCards      = r.GetInt32(23),
                AverageRating = r.GetDouble(24),
                AverageForm   = r.GetDouble(25),
                MinutesPlayed = r.GetInt32(26)
            });
        }
        return result;
    }
}
