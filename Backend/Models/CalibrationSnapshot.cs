namespace HattrickAnalizer.Models;

/// <summary>A point-in-time own-team capture. Context stays nullable until observed or supplied.</summary>
public class CalibrationSnapshot
{
    public int TeamId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string Source { get; set; } = "CHPP own roster";
    public List<Player> Players { get; set; } = new();
    public CalibrationMatchContext Context { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class CalibrationMatchContext
{
    public long? MatchId { get; set; }
    public DateTimeOffset? MatchDate { get; set; }
    public bool? IsHomeMatch { get; set; }
    public string? Tactic { get; set; }
    public string? Attitude { get; set; }
    public string? CoachType { get; set; }
    public int? TeamSpiritLevel { get; set; }
    public int? ConfidenceLevel { get; set; }
    public string? Formation { get; set; }
    public int? FormationExperience { get; set; }
    public int? WeatherId { get; set; }
    public int? AssistantManagerLevel { get; set; }
    public Dictionary<string, int> PlayerIdsBySlot { get; set; } = new();
    public Dictionary<string, string> BehavioursBySlot { get; set; } = new();

    public bool IsComplete
    {
        get
        {
            var slots = PlayerIdsBySlot.Keys.ToHashSet(StringComparer.Ordinal);
            var formationMatchesSlots = Formation != null
                && HattrickAnalizer.Services.FormationData.Formations.TryGetValue(Formation, out var definition)
                && definition.Positions.ToHashSet(StringComparer.Ordinal).SetEquals(slots);
            return MatchId is > 0 && MatchDate.HasValue && IsHomeMatch.HasValue
                && Tactic is ("Normal" or "Pressing" or "Counter" or "AttackInMiddle" or "AttackOnWings" or "PlayCreatively" or "LongShots")
                && Attitude is ("Normal" or "PIC" or "MOTS")
                && CoachType is ("Neutral" or "Offensive" or "Defensive")
                && TeamSpiritLevel is >= 0 and <= 10 && ConfidenceLevel is >= 0 and <= 10
                && formationMatchesSlots
                && FormationExperience is >= 3 and <= 10 && WeatherId is >= 0 and <= 3
                && double.IsFinite(HattrickAnalizer.Services.AdvancedLineupOptimizer.ComputeDisorderRisk(FormationExperience.Value))
                && AssistantManagerLevel is >= 0 and <= 5
                && PlayerIdsBySlot.Count == 11 && PlayerIdsBySlot.Values.All(id => id > 0)
                && PlayerIdsBySlot.Values.Distinct().Count() == 11 && slots.Contains("GK")
                && slots.SetEquals(BehavioursBySlot.Keys)
                && BehavioursBySlot.All(kv => HattrickAnalizer.Services.CalibrationService.IsValidSlotBehaviour(kv.Key, kv.Value));
        }
    }
}
