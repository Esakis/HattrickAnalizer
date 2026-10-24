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
    public LineupRatings? PredictedRatings { get; set; }
    public string? ModelVersion { get; set; }
    public string PredictionSource { get; set; } = "legacy-replay";
    public bool IsComplete => Context.IsComplete;
    public List<string> MissingFields => Context.MissingFields;
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
    // Optional forecast already displayed to the manager, frozen with this pre-match capture.
    public LineupRatings? PredictedRatings { get; set; }
    public string? ModelVersion { get; set; }

    public List<string> MissingFields
    {
        get
        {
            var missing = new List<string>();
            if (MatchId is not > 0) missing.Add("matchId");
            if (!MatchDate.HasValue) missing.Add("matchDate");
            if (!IsHomeMatch.HasValue) missing.Add("isHomeMatch");
            if (Tactic is not ("Normal" or "Pressing" or "Counter" or "AttackInMiddle" or "AttackOnWings" or "PlayCreatively" or "LongShots")) missing.Add("tactic");
            if (Attitude is not ("Normal" or "PIC" or "MOTS")) missing.Add("attitude");
            if (CoachType is not ("Neutral" or "Offensive" or "Defensive")) missing.Add("coachType");
            if (TeamSpiritLevel is not (>= 0 and <= 10)) missing.Add("teamSpiritLevel");
            if (ConfidenceLevel is not (>= 0 and <= 10)) missing.Add("confidenceLevel");
            if (FormationExperience is not (>= 3 and <= 10)) missing.Add("formationExperience");
            if (WeatherId is not (>= 0 and <= 3)) missing.Add("weatherId");
            if (AssistantManagerLevel is not (>= 0 and <= 5)) missing.Add("assistantManagerLevel");
            var ids = PlayerIdsBySlot ?? new();
            if (ids.Count != 11 || ids.Values.Any(id => id <= 0) || ids.Values.Distinct().Count() != 11) missing.Add("playerIdsBySlot");
            if (Formation == null || !HattrickAnalizer.Services.FormationData.Formations.TryGetValue(Formation, out var definition)
                || !definition.Positions.ToHashSet(StringComparer.Ordinal).SetEquals(ids.Keys)) missing.Add("formation");
            if (BehavioursBySlot == null || !ids.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(BehavioursBySlot.Keys)
                || BehavioursBySlot.Any(kv => !HattrickAnalizer.Services.CalibrationService.IsValidSlotBehaviour(kv.Key, kv.Value))) missing.Add("behavioursBySlot");
            return missing;
        }
    }

    public bool IsComplete
    {
        get
        {
            if (PlayerIdsBySlot == null || BehavioursBySlot == null) return false;
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
