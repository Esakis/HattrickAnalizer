using System.Globalization;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>
/// Podsumowanie treningu: typ/intensywnosc z pliku training, lista zawodnikow,
/// ktorzy dostali pelny trening (grali na trenowanych pozycjach w ostatnim meczu)
/// oraz ORIENTACYJNA prognoza tygodni do skoku trenowanego skilla.
///
/// Prognoza jest przyblizeniem (krzywa wykladnicza poziomu + spowolnienie wiekiem
/// + skalowanie intensywnoscia) — nie odtwarza pelnego modelu treningu HT.
/// </summary>
public class TrainingService
{
    private readonly HattrickApiService _api;
    private readonly ILogger<TrainingService> _logger;

    public TrainingService(HattrickApiService api, ILogger<TrainingService> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<TrainingSummary> GetSummaryAsync(int teamId)
    {
        var doc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "training" }, { "teamId", teamId.ToString() }, { "version", "2.2" }
        }, $"training teamId={teamId}");

        var team = doc.Descendants("Team").FirstOrDefault()
            ?? throw new ChppApiException($"training nie zwrocilo danych dla druzyny {teamId}.");

        var summary = new TrainingSummary
        {
            TeamId = teamId,
            TrainingTypeCode = ParseIntOrZero(team, "TrainingType"),
            TrainingLevel = ParseIntOrZero(team, "TrainingLevel"),
            StaminaTrainingPart = ParseIntOrZero(team, "StaminaTrainingPart"),
            TrainerName = team.Element("Trainer")?.Element("TrainerName")?.Value ?? ""
        };
        summary.TrainingTypeName = TrainingTypeName(summary.TrainingTypeCode);
        summary.TrainedSkill = TrainedSkillName(summary.TrainingTypeCode);

        // Kto gral na trenowanych pozycjach w ostatnim rozegranym meczu.
        var trainedSlots = TrainedSlots(summary.TrainingTypeCode);
        var lastLineup = await GetLastMatchSlotsAsync(teamId);
        summary.LastMatchId = lastLineup.MatchId;
        summary.LastMatchDate = lastLineup.MatchDate;

        var players = (await _api.GetTeamPlayersAsync(teamId)).ToDictionary(p => p.PlayerId);

        foreach (var (playerId, slot) in lastLineup.Slots)
        {
            if (!players.TryGetValue(playerId, out var player)) continue;
            bool full = trainedSlots.Count == 0 || trainedSlots.Contains(SlotGroup(slot));

            var skillValue = TrainedSkillValue(player, summary.TrainingTypeCode);
            summary.Players.Add(new TrainingPlayerEntry
            {
                PlayerId = playerId,
                PlayerName = $"{player.FirstName} {player.LastName}".Trim(),
                Age = player.Age,
                Slot = slot,
                FullTraining = full,
                TrainedSkillValue = skillValue,
                EstimatedWeeksToNextLevel = full && skillValue > 0
                    ? EstimateWeeksToNextLevel(skillValue, player.Age, summary.TrainingLevel)
                    : null
            });
        }

        summary.Players = summary.Players
            .OrderByDescending(p => p.FullTraining)
            .ThenBy(p => p.EstimatedWeeksToNextLevel ?? double.MaxValue)
            .ToList();
        return summary;
    }

    private async Task<(long MatchId, DateTime? MatchDate, Dictionary<int, string> Slots)> GetLastMatchSlotsAsync(int teamId)
    {
        var matchesDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matches" }, { "teamId", teamId.ToString() }, { "version", "2.8" }
        }, $"matches teamId={teamId}");

        var last = matchesDoc.Descendants("Match")
            .Where(m =>
            {
                var status = m.Element("Status")?.Value ?? "";
                var matchType = int.Parse(m.Element("MatchType")?.Value ?? "0");
                return status.Equals("FINISHED", StringComparison.OrdinalIgnoreCase)
                    && matchType >= 1 && matchType <= 12;
            })
            .OrderByDescending(m => ParseDate(m.Element("MatchDate")?.Value) ?? DateTime.MinValue)
            .FirstOrDefault();

        var slots = new Dictionary<int, string>();
        if (last == null) return (0, null, slots);

        var matchId = last.Element("MatchID")?.Value ?? "0";
        var lineupDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            { "file", "matchlineup" }, { "matchId", matchId }, { "teamId", teamId.ToString() }, { "version", "2.1" }
        }, $"matchlineup matchId={matchId}");

        var teamElement = lineupDoc.Descendants("Team")
            .FirstOrDefault(t => t.Element("TeamID")?.Value == teamId.ToString());
        var container = teamElement?.Element("StartingLineup") ?? teamElement?.Element("Lineup");
        foreach (var playerEl in container?.Elements("Player") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            if (!int.TryParse(playerEl.Element("PlayerID")?.Value, out int pid)) continue;
            var roleId = int.Parse(playerEl.Element("RoleID")?.Value ?? "0");
            if (roleId < 100 || roleId > 113) continue;
            var slot = CalibrationService.MapRoleIdToSlot(roleId);
            if (!string.IsNullOrEmpty(slot)) slots[pid] = slot;
        }

        return (long.Parse(matchId), ParseDate(last.Element("MatchDate")?.Value), slots);
    }

    // Grupy pozycji: GK / DEF / WING / IM / FW.
    private static string SlotGroup(string slot) => slot switch
    {
        "GK" => "GK",
        "RWB" or "RCD" or "CD" or "LCD" or "LWB" => "DEF",
        "RW" or "LW" => "WING",
        "RIM" or "IM" or "LIM" => "IM",
        _ => "FW"
    };

    // Pusty zbior = trening globalny (wszyscy dostaja pelny trening).
    private static HashSet<string> TrainedSlots(int type) => type switch
    {
        3 or 12 => new HashSet<string> { "DEF" },
        4 => new HashSet<string> { "FW" },
        5 or 13 => new HashSet<string> { "WING" },
        8 => new HashSet<string> { "IM", "WING", "FW" },
        9 => new HashSet<string> { "IM" },
        10 => new HashSet<string> { "GK" },
        11 => new HashSet<string> { "DEF", "IM" },
        _ => new HashSet<string>() // 0/1/2/7: ogolny, kondycja, SFG, strzaly — cala druzyna
    };

    private static string TrainingTypeName(int type) => type switch
    {
        0 => "General",
        1 => "Stamina",
        2 => "SetPieces",
        3 => "Defending",
        4 => "Scoring",
        5 => "CrossPass",
        7 => "Shooting",
        8 => "ShortPasses",
        9 => "Playmaking",
        10 => "Goaltending",
        11 => "ThroughPasses",
        12 => "DefensivePositions",
        13 => "WingAttacks",
        _ => $"Unknown({type})"
    };

    private static string TrainedSkillName(int type) => type switch
    {
        1 => "Stamina",
        2 => "SetPieces",
        3 or 12 => "Defending",
        4 or 7 => "Scoring",
        5 or 13 => "Winger",
        8 or 11 => "Passing",
        9 => "Playmaking",
        10 => "Keeper",
        _ => ""
    };

    private static int TrainedSkillValue(Player p, int type) => type switch
    {
        1 => p.Stamina,
        2 => p.Skills.SetPieces,
        3 or 12 => p.Skills.Defending,
        4 or 7 => p.Skills.Scoring,
        5 or 13 => p.Skills.Winger,
        8 or 11 => p.Skills.Passing,
        9 => p.Skills.Playmaking,
        10 => p.Skills.Keeper,
        _ => 0
    };

    /// <summary>
    /// ORIENTACYJNE tygodnie do nastepnego poziomu: koszt rosnie wykladniczo z poziomem,
    /// wiek spowalnia (~4.5%/rok po 17), intensywnosc treningu skaluje liniowo.
    /// </summary>
    internal static double EstimateWeeksToNextLevel(int currentLevel, int age, int trainingLevel)
    {
        double levelCost = 0.6 * Math.Exp(0.16 * currentLevel);
        double ageFactor = 1 + 0.045 * Math.Max(0, age - 17);
        double intensity = Math.Clamp(trainingLevel, 10, 100) / 100.0;
        return Math.Clamp(Math.Round(levelCost * ageFactor / intensity, 1), 0.5, 99);
    }

    private static int ParseIntOrZero(System.Xml.Linq.XElement parent, string name) =>
        int.TryParse(parent.Element(name)?.Value, out var v) ? v : 0;

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
}

public class TrainingSummary
{
    public int TeamId { get; set; }
    public int TrainingTypeCode { get; set; }
    public string TrainingTypeName { get; set; } = "";
    public string TrainedSkill { get; set; } = "";
    public int TrainingLevel { get; set; }
    public int StaminaTrainingPart { get; set; }
    public string TrainerName { get; set; } = "";
    public long LastMatchId { get; set; }
    public DateTime? LastMatchDate { get; set; }
    public List<TrainingPlayerEntry> Players { get; set; } = new();
}

public class TrainingPlayerEntry
{
    public int PlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public int Age { get; set; }
    public string Slot { get; set; } = "";
    public bool FullTraining { get; set; }
    public int TrainedSkillValue { get; set; }
    // Orientacyjna prognoza — null dla graczy bez pelnego treningu.
    public double? EstimatedWeeksToNextLevel { get; set; }
}
