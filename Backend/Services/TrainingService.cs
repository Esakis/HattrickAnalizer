using System.Globalization;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

public class TrainingService
{
    private readonly HattrickApiService _api;
    private readonly ILogger<TrainingService> _logger;
    private readonly IConfiguration _configuration;

    public TrainingService(HattrickApiService api, ILogger<TrainingService> logger, IConfiguration configuration)
    {
        _api = api;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<TrainingSummary> GetSummaryAsync(int teamId)
    {
        var trainingDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            ["file"] = "training", ["teamId"] = teamId.ToString(), ["version"] = "2.2"
        }, $"training teamId={teamId}");
        var team = trainingDoc.Descendants("Team").FirstOrDefault()
            ?? throw new ChppApiException($"training returned no team data for {teamId}.");
        var summary = new TrainingSummary
        {
            TeamId = teamId,
            TrainingTypeCode = TryParseNullableInt(team, "TrainingType") ?? -1,
            TrainingTypeKnown = TryParseNullableInt(team, "TrainingType") is int parsedTrainingType
                && IsSupportedTrainingTypeCode(parsedTrainingType),
            TrainingLevel = ParseInt(team, "TrainingLevel"),
            StaminaTrainingPart = ParseInt(team, "StaminaTrainingPart"),
            StaminaTrainingSharePercent = TryParseNullableInt(team, "StaminaTrainingPart"),
            StaminaTrainingPartKnown = TryParseNullableInt(team, "StaminaTrainingPart").HasValue,
            TrainingIntensityPercent = TryParseNullableInt(team, "TrainingLevel"),
            TeamSpiritLevel = TryParseNullableInt(team, "Morale"),
            ConfidenceLevel = TryParseNullableInt(team, "SelfConfidence"),
            TrainerName = team.Element("Trainer")?.Element("TrainerName")?.Value ?? "",
            CoachSkillRaw = TryParseNullableInt(team.Element("Trainer") ?? team, "TrainerSkill"),
            CoachSkillSource = TryParseNullableInt(team.Element("Trainer") ?? team, "TrainerSkill").HasValue
                ? "CHPP training 2.2 legacy fallback" : null,
            AssistantSkillTotal = TryParseNullableInt(team, "AssistantTrainerSkillTotal")
        };
        var legacyCoachId = TryParseNullableInt(team.Element("Trainer") ?? team, "TrainerID");
        var staff = await GetStaffListDataAsync(teamId);
        if (staff.Available)
        {
            summary.TrainerId = staff.TrainerId;
            if (!string.IsNullOrWhiteSpace(staff.TrainerName)) summary.TrainerName = staff.TrainerName;
            summary.TrainerIdentitySource = staff.TrainerId.HasValue || !string.IsNullOrWhiteSpace(staff.TrainerName)
                ? staff.IdentitySource : null;
            summary.CoachSkillRaw = staff.CoachSkill;
            summary.CoachSkillSource = staff.CoachSkill.HasValue ? staff.CoachSkillSource : null;
            summary.CoachType = staff.CoachType;
            summary.CoachTypeSource = staff.CoachType is null ? null : staff.CoachTypeSource;
            summary.AssistantSkillTotal = staff.AssistantSkillTotal;
            summary.AssistantSkillSource = staff.AssistantSkillTotal.HasValue ? staff.AssistantSkillSource : null;
            summary.Warnings.AddRange(staff.Warnings);
        }
        else
        {
            summary.TrainerId = legacyCoachId;
            summary.TrainerIdentitySource = legacyCoachId.HasValue || !string.IsNullOrWhiteSpace(summary.TrainerName)
                ? "CHPP training 2.2 legacy fallback" : null;
            summary.Warnings.Add("Current CHPP stafflist 1.2 was unavailable; legacy training-feed fields are used only as explicit fallbacks.");
            if (summary.AssistantSkillTotal.HasValue) summary.AssistantSkillSource = "CHPP training 2.2 legacy field";
            if (summary.CoachSkillRaw.HasValue) summary.Warnings.Add("Coach skill came from legacy training 2.2 TrainerSkill because current stafflist data was unavailable.");
            var coachDetails = await GetCoachDataAsync(legacyCoachId);
            summary.CoachSkillRaw ??= coachDetails.Skill;
            summary.CoachSkillSource ??= coachDetails.Skill.HasValue ? "CHPP playerdetails legacy fallback" : null;
            summary.CoachType = coachDetails.Type;
            summary.CoachTypeSource = coachDetails.Type is null ? null : "CHPP playerdetails legacy fallback";
        }
        if (!summary.AssistantSkillTotal.HasValue && int.TryParse(_configuration["Training:AssistantSkillTotal"], out var configuredAssistantSkill)
            && configuredAssistantSkill is >= 0 and <= 10)
        {
            summary.AssistantSkillTotal = configuredAssistantSkill;
            summary.AssistantSkillSource = "configuration fallback";
        }
        summary.TrainingTypeName = summary.TrainingTypeKnown ? TrainingTypeName(summary.TrainingTypeCode) : "Unknown";
        summary.TrainedSkill = summary.TrainingTypeKnown ? TrainedSkillName(summary.TrainingTypeCode) : "";
        summary.IntensityAndStaminaMultiplier = IntensityAndStaminaMultiplier(summary.TrainingIntensityPercent, summary.StaminaTrainingSharePercent);
        summary.AssistantSpeedMultiplier = AssistantSpeedMultiplier(summary.AssistantSkillTotal);
        summary.CoachSpeedMultiplier = GetConfiguredCoachSpeedMultiplier(summary.CoachSkillRaw, _configuration);
        summary.ApproximateTrainingSpeedMultiplier = ApproximateTrainingSpeedMultiplier(summary.TrainingIntensityPercent,
            summary.StaminaTrainingSharePercent, summary.CoachSpeedMultiplier, summary.AssistantSkillTotal);
        summary.TrainingSpeedMethod = "Approximate relative multiplier = (TrainingLevel/100) × (1-StaminaTrainingPart/100) × configured coach factor × (1+0.035×assistant skill sum, max 10). The coach factor is supplied under Training:CoachSpeedFactorBySkill:{rawSkill}, because the official manual states its direction but does not publish a numeric conversion. No level-up date is inferred.";
        if (!summary.ApproximateTrainingSpeedMultiplier.HasValue)
            summary.Warnings.Add("Training speed is partial: configure the coach factor and/or provide assistant levels; no total speed or exact skill-up date is inferred.");
        if (!summary.AssistantSkillTotal.HasValue)
            summary.Warnings.Add("Assistant-coach skill levels were unavailable from the training/stafflist feeds; set Training:AssistantSkillTotal to the known summed skill level (0..10) to include their official bonus.");
        if (!summary.TeamSpiritLevel.HasValue) summary.Warnings.Add("CHPP did not provide current Morale (team spirit).");
        if (!summary.ConfidenceLevel.HasValue) summary.Warnings.Add("CHPP did not provide current SelfConfidence.");
        if (!summary.TrainingTypeKnown) summary.Warnings.Add("CHPP did not provide a recognized TrainingType; position-specific training is not estimated.");
        if (!summary.TrainingIntensityPercent.HasValue || !summary.StaminaTrainingSharePercent.HasValue)
            summary.Warnings.Add("TrainingLevel or StaminaTrainingPart is unavailable; corresponding speed factors are omitted.");

        var (start, end, configured) = GetTrainingWindow(DateTimeOffset.UtcNow, _configuration);
        summary.WeekStart = start.UtcDateTime;
        summary.WeekEnd = end.UtcDateTime;
        summary.IsEstimate = true;
        if (!configured)
            summary.Warnings.Add("Training week uses the documented fallback Thursday 22:00 Europe/Warsaw; set Training:UpdateDay, Training:UpdateHour and Training:TimeZoneId to the Hattrick country's update schedule.");
        var roster = await _api.GetTeamPlayersAsync(teamId);
        var minutes = await GetWeekMinutesAsync(teamId, summary.WeekStart, summary.WeekEnd, summary);
        summary.Players = roster.Select(player =>
        {
            var entryMinutes = minutes.GetValueOrDefault(player.PlayerId) ?? new PlayerTrainingMinutes();
            var result = summary.TrainingTypeCode == 2
                ? ComputeSetPiecesBest90(entryMinutes.Full, entryMinutes.Half,
                    entryMinutes.SetPiecesBonusFull, entryMinutes.SetPiecesBonusHalf, entryMinutes.Small, entryMinutes.VerySmall)
                : ComputeBest90(entryMinutes.Full, entryMinutes.Half, entryMinutes.Small, entryMinutes.VerySmall);
            var full = result.FullMinutes;
            var halfUsed = result.HalfMinutes;
            var effective = result.EffectiveMinutes;
            var staminaFraction = StaminaTrainingFraction(player.InjuryStatusKnown && player.InjuryLevel > 0,
                entryMinutes.Coverage, player.InjuryStatusKnown);
            var trainedSkillAvailable = IsTrainedSkillAvailable(player, summary.TrainingTypeCode);
            return new TrainingPlayerEntry
            {
                PlayerId = player.PlayerId,
                PlayerName = $"{player.FirstName} {player.LastName}".Trim(),
                Age = player.Age,
                Slot = entryMinutes.LastSlot,
                FullTraining = full >= 90,
                TrainedSkillValue = trainedSkillAvailable ? TrainedSkillValue(player, summary.TrainingTypeCode) : 0,
                TrainedSkillAvailable = trainedSkillAvailable,
                CoverageMinutes = entryMinutes.Coverage,
                FullTrainingMinutes = full,
                HalfTrainingMinutes = halfUsed,
                ObservedFullMinutes = result.ObservedFullMinutes,
                ObservedHalfMinutes = result.ObservedHalfMinutes,
                ObservedSmallEffectMinutes = entryMinutes.Small,
                ObservedVerySmallEffectMinutes = entryMinutes.VerySmall,
                SmallEffectMinutes = result.SmallEffectMinutes,
                VerySmallEffectMinutes = result.VerySmallEffectMinutes,
                EffectiveTrainingMinutes = effective,
                SetPiecesBonusFullMinutes = result.BonusFullMinutes,
                SetPiecesBonusHalfMinutes = result.BonusHalfMinutes,
                SetPiecesBonusUnknownMinutes = entryMinutes.SetPiecesBonusUnknownMinutes,
                SetPiecesSpecialBonusMinutes = result.SpecialBonusMinutes,
                TrainingFraction = effective / 90.0,
                StaminaTrainingFraction = staminaFraction,
                IsEstimate = true,
                Warnings = entryMinutes.Warnings
                    .Concat(entryMinutes.SetPiecesBonusUnknownMinutes > 0
                        ? new[] { $"Set-piece taker is unknown for {entryMinutes.SetPiecesBonusUnknownMinutes} minutes; no special bonus was assigned to those minutes." }
                        : Array.Empty<string>())
                    .Concat(staminaFraction.HasValue ? Array.Empty<string>() : new[] { "Stamina contribution is unknown because injury/availability status is not known." })
                    .Concat(trainedSkillAvailable || string.IsNullOrEmpty(summary.TrainedSkill) ? Array.Empty<string>() : new[] { "Current trained skill was not available from CHPP." })
                    .Distinct().ToList(),
                EstimatedWeeksToNextLevel = null
            };
        }).OrderByDescending(p => p.EffectiveTrainingMinutes).ThenBy(p => p.PlayerName).ToList();
        return summary;
    }

    private async Task<Dictionary<int, PlayerTrainingMinutes>> GetWeekMinutesAsync(
        int teamId, DateTime start, DateTime end, TrainingSummary summary)
    {
        var matchesDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
        {
            ["file"] = "matches", ["teamId"] = teamId.ToString(), ["version"] = "2.8"
        }, $"matches teamId={teamId}");
        var matches = matchesDoc.Descendants("Match").Where(m =>
        {
            var status = m.Element("Status")?.Value ?? "";
            var type = ParseInt(m, "MatchType");
            var date = ParseDate(m.Element("MatchDate")?.Value);
            return status.Equals("FINISHED", StringComparison.OrdinalIgnoreCase)
                && type >= 1 && type <= 12 && date >= start && date <= end;
        }).OrderBy(m => ParseDate(m.Element("MatchDate")?.Value)).ToList();

        var newest = matches.LastOrDefault();
        if (newest != null && long.TryParse(newest.Element("MatchID")?.Value, out var newestId))
        {
            summary.LastMatchId = newestId;
            summary.LastMatchDate = ParseDate(newest.Element("MatchDate")?.Value);
        }

        var result = new Dictionary<int, PlayerTrainingMinutes>();
        foreach (var match in matches)
        {
            if (!long.TryParse(match.Element("MatchID")?.Value, out var matchId)) continue;
            var lineup = await _api.FetchChppXmlAsync(new Dictionary<string, string>
            {
                ["file"] = "matchlineup", ["matchId"] = matchId.ToString(),
                ["teamId"] = teamId.ToString(), ["version"] = "2.1"
            }, $"matchlineup matchId={matchId}");
            System.Xml.Linq.XDocument? details = null;
            try
            {
                details = await _api.FetchChppXmlAsync(new Dictionary<string, string>
                {
                    ["file"] = "matchdetails", ["matchID"] = matchId.ToString(), ["version"] = "3.1", ["matchEvents"] = "true"
                }, $"matchdetails matchId={matchId}");
            }
            catch (ChppApiException ex)
            {
                summary.Warnings.Add($"Match {matchId}: match details were unavailable; red-card and injury adjustments could not be checked ({ex.Message}).");
            }
            var team = lineup.Descendants("Team").FirstOrDefault(t => t.Element("TeamID")?.Value == teamId.ToString());
            var startingLineup = team?.Element("StartingLineup");
            var container = team?.Element("Lineup") ?? startingLineup;
            if (container == null)
            {
                summary.Warnings.Add($"Match {matchId}: lineup data unavailable.");
                continue;
            }
            var setPiecesTakerId = ReadRole17PlayerId(startingLineup) ?? ReadExplicitSetPiecesTakerId(team);
            var setPiecesTakerMarker = container.Elements("Player").FirstOrDefault(p => p.Element("RoleID")?.Value == "17");
            setPiecesTakerMarker ??= startingLineup?.Elements("Player").FirstOrDefault(p => p.Element("RoleID")?.Value == "17");
            if (!setPiecesTakerId.HasValue && startingLineup == null
                && int.TryParse(setPiecesTakerMarker?.Element("PlayerID")?.Value, out var aggregateTakerId))
                setPiecesTakerId = aggregateTakerId;
            var finalSetPiecesTakerId = ReadRole17PlayerId(team?.Element("Lineup"));
            var takerKnown = setPiecesTakerId.HasValue;
            if (summary.TrainingTypeCode == 2 && !takerKnown)
                summary.Warnings.Add($"Match {matchId}: CHPP did not identify the set-pieces taker; the 25% special bonus is omitted for this match.");
            if (startingLineup != null)
            {
                var timeline = ReconstructOfficialPositionMinutes(startingLineup, team?.Element("Substitutions"), details,
                    teamId, setPiecesTakerId, finalSetPiecesTakerId);
                if (!timeline.Known)
                {
                    summary.Warnings.Add($"Match {matchId}: official lineup timeline is ambiguous; no exact player minutes were assigned. {timeline.Warning}");
                    var unknownPlayerIds = startingLineup.Elements().Where(e => e.Name.LocalName == "Player")
                        .Select(p => int.TryParse(ChildValue(p, "PlayerID"), out var id) ? id : 0)
                        .Concat((team?.Element("Substitutions")?.Elements().Where(e => e.Name.LocalName == "Substitution")
                            ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                            .SelectMany(order => new[] { ChildValue(order, "SubjectPlayerID"), ChildValue(order, "ObjectPlayerID") })
                            .Select(id => int.TryParse(id, out var parsedId) ? parsedId : 0))
                        .Where(id => id > 0).Distinct();
                    foreach (var playerId in unknownPlayerIds)
                    {
                        if (!result.TryGetValue(playerId, out var unknownPlayer)) result[playerId] = unknownPlayer = new PlayerTrainingMinutes();
                        unknownPlayer.Warnings.Add($"Match {matchId}: exact played minutes are unknown because the official lineup timeline could not be reconstructed.");
                    }
                }
                else
                {
                    foreach (var (playerId, segments) in timeline.SetPiecesSegments)
                    {
                        if (!result.TryGetValue(playerId, out var playerMinutes)) result[playerId] = playerMinutes = new PlayerTrainingMinutes();
                        foreach (var segment in segments)
                        {
                            if (segment.Minutes <= 0) continue;
                            playerMinutes.Coverage += segment.Minutes;
                            AccumulateTrainingPosition(playerMinutes, summary.TrainingTypeCode, segment.Slot, segment.Minutes);
                            if (summary.TrainingTypeCode == 2)
                                AccumulateTimelineSetPiecesBonus(playerMinutes, segment);
                        }
                    }
                }
                if (details == null)
                    summary.Warnings.Add($"Match {matchId}: timeline minutes assume a standard 90-minute regulation match; match details were unavailable to verify match-specific duration or incidents.");
                continue;
            }

            // Compatibility for existing aggregate fixtures and older non-standard payloads.
            foreach (var p in container.Elements("Player"))
            {
                if (!int.TryParse(p.Element("PlayerID")?.Value, out var playerId)) continue;
                if (!int.TryParse(p.Element("RoleID")?.Value, out var role)) continue;
                if (role is 17 or 18 or 19 or 20 or 21) continue;
                if (!result.TryGetValue(playerId, out var playerMinutes)) result[playerId] = playerMinutes = new PlayerTrainingMinutes();
                var segments = ReadPositionMinuteSegments(p);
                var playedKnown = int.TryParse(p.Element("PlayedMinutes")?.Value, out var played);
                if (segments.Count > 0) { played = segments.Sum(s => s.Minutes); playedKnown = true; }
                if (!playedKnown && segments.Count == 0)
                {
                    playerMinutes.Warnings.Add($"Match {matchId}: CHPP omitted PlayedMinutes; match excluded for this player.");
                    continue;
                }
                if (played <= 0) continue;
                playerMinutes.Coverage += played;
                if (segments.Count == 0)
                {
                    var slot = MapChppPosition(p.Element("PositionCode")?.Value) ?? MapChppPosition(role.ToString()) ?? string.Empty;
                    AccumulateTrainingPosition(playerMinutes, summary.TrainingTypeCode, slot, played);
                    if (summary.TrainingTypeCode == 2)
                        AccumulateSetPiecesBonus(playerMinutes, playerId, setPiecesTakerId, takerKnown, slot, played);
                    if (p.Element("PositionCode") == null && team?.Element("Substitutions") == null)
                        playerMinutes.Warnings.Add("CHPP supplied no position-change timeline; the lineup role is used and role changes cannot be timed.");
                    else if (team?.Element("Substitutions") != null)
                        playerMinutes.Warnings.Add("CHPP included substitution data but no per-player position-minute segments; aggregate PlayedMinutes use the final role with explicit role-change uncertainty.");
                }
                else
                {
                    foreach (var segment in segments)
                    {
                        AccumulateTrainingPosition(playerMinutes, summary.TrainingTypeCode, segment.Slot, segment.Minutes);
                        if (summary.TrainingTypeCode == 2)
                            AccumulateSetPiecesBonus(playerMinutes, playerId, setPiecesTakerId, takerKnown, segment.Slot, segment.Minutes);
                    }
                }
            }
        }
        return result;
    }

    // Only the manual's explicitly stated half effects are numeric here. Parenthesized
    // and double-parenthesized effects stay separate because the manual gives no fraction.
    public static double TrainingFactor(int type, string slot) => type switch
    {
        0 or 1 => 0, // Deprecated CHPP legacy choices: general/stamina share is not a position allocation.
        2 => 1,
        3 => IsDefender(slot) ? 1 : -2,
        4 => IsForward(slot) ? 1 : -2,
        5 => IsWinger(slot) ? 1 : IsWingBack(slot) ? 0.5 : -2,
        6 => -1,
        7 => IsMidfielder(slot) || IsWinger(slot) || IsForward(slot) ? 1 : -2,
        8 => IsMidfielder(slot) ? 1 : IsWinger(slot) ? 0.5 : -2,
        9 => slot == "GK" ? 1 : 0,
        10 => IsDefender(slot) || IsMidfielder(slot) || IsWinger(slot) ? 1 : -2,
        11 => slot == "GK" || IsDefender(slot) || IsMidfielder(slot) || IsWinger(slot) ? -1 : IsForward(slot) ? -2 : 0,
        12 => IsWinger(slot) || IsForward(slot) ? 1 : -2,
        _ => 0
    };

    // The old CHPP XML table omits Wing Attacks; the enum implementation defines code 12:
    // https://github.com/lucianoq/hattrick/blob/7b09b3c550ba/chpp/type_training_type.go
    private static bool IsSupportedTrainingTypeCode(int code) => code is >= 0 and <= 12;

    private static bool IsDefender(string s) => s is "RWB" or "RCD" or "CD" or "LCD" or "LWB";
    private static bool IsWingBack(string s) => s is "RWB" or "LWB";
    private static bool IsWinger(string s) => s is "RW" or "LW";
    private static bool IsMidfielder(string s) => s is "RIM" or "IM" or "LIM";
    private static bool IsForward(string s) => s is "RFW" or "FW" or "LFW";

    private static void AccumulateTrainingPosition(PlayerTrainingMinutes minutes, int trainingType, string slot, int played)
    {
        if (trainingType < 0)
        {
            minutes.Warnings.Add("Training type is unavailable; no position-specific training was estimated.");
            return;
        }
        if (string.IsNullOrEmpty(slot))
        {
            minutes.Warnings.Add("CHPP position code is unknown; these minutes are excluded from position-specific training.");
            return;
        }
        var factor = TrainingFactor(trainingType, slot);
        if (factor >= 0.99) minutes.Full += played;
        else if (Math.Abs(factor - 0.5) < 0.001) minutes.Half += played;
        else if (factor == -1)
        {
            minutes.Small += played;
            minutes.Warnings.Add("The official manual classifies this position as a small effect but gives no numeric fraction; small-effect minutes are reported separately and excluded from weighted minutes.");
        }
        else if (factor == -2)
        {
            minutes.VerySmall += played;
            minutes.Warnings.Add("The official manual classifies this position as a very small (osmosis) effect but gives no numeric fraction; those minutes are reported separately and excluded from weighted minutes.");
        }
        if (string.IsNullOrEmpty(minutes.LastSlot) || factor > TrainingFactor(trainingType, minutes.LastSlot)) minutes.LastSlot = slot;
    }

    private static void AccumulateSetPiecesBonus(PlayerTrainingMinutes minutes, int playerId,
        int? setPiecesTakerId, bool takerKnown, string slot, int played)
    {
        if (played <= 0) return;
        if (slot == "GK" || playerId == setPiecesTakerId)
            minutes.SetPiecesBonusFull += played;
        else if (!takerKnown)
            minutes.SetPiecesBonusUnknownMinutes += played;
    }

    private static void AccumulateTimelineSetPiecesBonus(PlayerTrainingMinutes minutes, PositionMinuteSegment segment)
    {
        if (segment.Minutes <= 0) return;
        if (segment.Slot == "GK" || (segment.SetPiecesTakerKnown && segment.IsSetPiecesTaker))
            minutes.SetPiecesBonusFull += segment.Minutes;
        else if (!segment.SetPiecesTakerKnown)
            minutes.SetPiecesBonusUnknownMinutes += segment.Minutes;
    }

    private static List<(string Slot, int Minutes)> ReadPositionMinuteSegments(System.Xml.Linq.XElement player)
    {
        var segments = new List<(string Slot, int Minutes)>();
        foreach (var segment in player.Descendants().Where(e => e.Name.LocalName is "Segment" or "PositionSegment" or "RoleSegment"))
        {
            var role = segment.Elements().FirstOrDefault(e => e.Name.LocalName is "RoleID" or "PositionCode")?.Value;
            var amount = segment.Elements().FirstOrDefault(e => e.Name.LocalName is "Minutes" or "DurationMinutes" or "PlayedMinutes")?.Value;
            var slot = MapChppPosition(role);
            if (!string.IsNullOrEmpty(slot) && int.TryParse(amount, out var minutes) && minutes > 0)
                segments.Add((slot, minutes));
        }
        return segments;
    }

    private static OfficialMinuteTimeline ReconstructOfficialPositionMinutes(
        System.Xml.Linq.XElement startingLineup, System.Xml.Linq.XElement? substitutions,
        System.Xml.Linq.XDocument? details, int teamId, int? initialTakerId, int? finalTakerId)
    {
        if (details == null)
            return OfficialMinuteTimeline.Unknown("Match details are unavailable, so red cards and match completion could not be verified.");
        var match = details.Descendants().FirstOrDefault(e => e.Name.LocalName == "Match");
        if (match == null || string.IsNullOrWhiteSpace(match.Element("FinishedDate")?.Value))
            return OfficialMinuteTimeline.Unknown("Match details did not confirm a finished match.");
        if (!int.TryParse(match.Element("AddedMinutes")?.Value, out var addedMinutes) || addedMinutes is < 0 or > 30)
            return OfficialMinuteTimeline.Unknown("Match details omitted a plausible AddedMinutes duration value (0..30).");
        // MatchPart 3 is the documented overtime period. Training's 90-minute selection
        // happens after collecting all positions, so include regulation plus 30 ET minutes.
        // AddedMinutes is the matchdetails stoppage-time total. MatchPart 4 penalties do not count.
        var playedMatchParts = match.Descendants().Where(e => e.Name.LocalName == "MatchPart").Select(e => e.Value).ToList();
        var matchEndMinute = (playedMatchParts.Any(part => part is "3" or "4") ? 120 : 90) + addedMinutes;
        if (matchEndMinute is < 90 or > 150)
            return OfficialMinuteTimeline.Unknown("Match details imply a physical duration outside the supported 90..150 minute range.");

        var roles = new Dictionary<int, string>();
        foreach (var player in startingLineup.Elements().Where(e => e.Name.LocalName == "Player"))
        {
            if (!int.TryParse(ChildValue(player, "PlayerID"), out var id)
                || !int.TryParse(ChildValue(player, "RoleID"), out var role)) continue;
            var slot = MapChppPosition(role.ToString());
            if (!string.IsNullOrEmpty(slot)) roles[id] = slot;
        }
        if (roles.Count == 0) return OfficialMinuteTimeline.Unknown("StartingLineup contained no recognizable field players.");
        var kickoffFieldPlayerIds = roles.Keys.ToHashSet();

        var redCardTimes = new Dictionary<int, int>();
        foreach (var booking in match.Descendants().Where(e => e.Name.LocalName == "Booking"))
        {
            if (ChildValue(booking, "BookingType") != "2"
                || !int.TryParse(ChildValue(booking, "BookingPlayerID"), out var playerId)) continue;
            var part = ChildValue(booking, "MatchPart");
            if (part == "4") continue; // Penalty shootout events are not physical training time.
            if (!TryReadElapsedMinute(ChildValue(booking, "BookingMinute"), part, out var minute))
                return OfficialMinuteTimeline.Unknown("A red-card minute could not be interpreted.");
            if (minute < matchEndMinute) redCardTimes[playerId] = Math.Min(redCardTimes.GetValueOrDefault(playerId, matchEndMinute), minute);
        }

        var orders = new List<TimedLineupOrder>();
        foreach (var order in substitutions?.Elements().Where(e => e.Name.LocalName == "Substitution")
                     ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            if (!int.TryParse(ChildValue(order, "OrderType"), out var orderType)
                || orderType is not (1 or 3)
                || !int.TryParse(ChildValue(order, "SubjectPlayerID"), out var subject)
                || !int.TryParse(ChildValue(order, "ObjectPlayerID"), out var target)
                || !TryReadElapsedMinute(ChildValue(order, "MatchMinute"), ChildValue(order, "MatchPart"), out var minute))
                return OfficialMinuteTimeline.Unknown("An executed substitution/position-change record was incomplete or used an unsupported order type.");
            var newPositionRaw = ChildValue(order, "NewPositionId");
            var newPosition = MapChppPosition(newPositionRaw);
            if (orderType == 1 && subject != target && string.IsNullOrEmpty(newPosition))
                return OfficialMinuteTimeline.Unknown("A substitution did not identify the entering player's new field position.");
            orders.Add(new TimedLineupOrder(minute, orderType, subject, target, newPosition));
        }
        orders = orders.OrderBy(o => o.Minute).ToList();
        var ownFieldPlayerIds = kickoffFieldPlayerIds.Concat(orders.Where(o => o.Type == 1 && o.Subject != o.Target)
            .Select(o => o.Target)).ToHashSet();

        var bonusSegments = new Dictionary<int, List<PositionMinuteSegment>>();
        var currentTakerId = initialTakerId;
        var takerKnown = initialTakerId.HasValue;
        var cursor = 0;
        foreach (var order in orders)
        {
            var at = Math.Clamp(order.Minute, 0, matchEndMinute);
            AddTimelineInterval(roles, redCardTimes, bonusSegments,
                currentTakerId, takerKnown, cursor, at);
            if (order.Type == 1)
            {
                if (order.Subject == order.Target)
                {
                    if (!roles.ContainsKey(order.Subject))
                        return OfficialMinuteTimeline.Unknown("A role-change order referred to a player who was not on the field.");
                    if (!string.IsNullOrEmpty(order.NewPosition)) roles[order.Subject] = order.NewPosition;
                }
                else
                {
                    if (!roles.ContainsKey(order.Subject) || roles.ContainsKey(order.Target))
                        return OfficialMinuteTimeline.Unknown("A substitution did not match the current on-field lineup.");
                    roles.Remove(order.Subject);
                    roles[order.Target] = order.NewPosition!;
                    if (takerKnown && currentTakerId == order.Subject)
                    {
                        if (finalTakerId.HasValue && roles.ContainsKey(finalTakerId.Value))
                            currentTakerId = finalTakerId;
                        else
                        {
                            currentTakerId = null;
                            takerKnown = false;
                        }
                    }
                    if (!takerKnown && finalTakerId == order.Target)
                    {
                        currentTakerId = finalTakerId;
                        takerKnown = true;
                    }
                }
            }
            else
            {
                if (!roles.TryGetValue(order.Subject, out var subjectSlot)
                    || !roles.TryGetValue(order.Target, out var targetSlot))
                    return OfficialMinuteTimeline.Unknown("A position swap referred to a player who was not on the field.");
                roles[order.Subject] = targetSlot;
                roles[order.Target] = subjectSlot;
            }
            cursor = at;
        }
        AddTimelineInterval(roles, redCardTimes, bonusSegments,
            currentTakerId, takerKnown, cursor, matchEndMinute);

        foreach (var injury in match.Descendants().Where(e => e.Name.LocalName == "Injury"))
        {
            if (ChildValue(injury, "InjuryType") != "2"
                || !int.TryParse(ChildValue(injury, "InjuryPlayerID"), out var playerId)
                || !ownFieldPlayerIds.Contains(playerId)) continue;
            var injuryTeamRaw = ChildValue(injury, "InjuryTeamID");
            if (!int.TryParse(injuryTeamRaw, out var injuryTeamId))
                return OfficialMinuteTimeline.Unknown($"Own player {playerId} had a serious injury with no verifiable InjuryTeamID.");
            if (injuryTeamId != teamId) continue;
            var part = ChildValue(injury, "MatchPart");
            if (part == "4") continue;
            if (!TryReadElapsedMinute(ChildValue(injury, "InjuryMinute"), part, out var injuryMinute))
                return OfficialMinuteTimeline.Unknown($"Own player {playerId} had an injury with an ambiguous minute.");

            var wasActive = kickoffFieldPlayerIds.Contains(playerId);
            foreach (var priorOrder in orders.Where(o => o.Minute < injuryMinute))
            {
                if (priorOrder.Type == 1 && priorOrder.Subject != priorOrder.Target)
                {
                    if (priorOrder.Subject == playerId) wasActive = false;
                    if (priorOrder.Target == playerId) wasActive = true;
                }
            }
            if (!wasActive && orders.Any(o => o.Type == 1 && o.Target == playerId && o.Minute == injuryMinute))
                return OfficialMinuteTimeline.Unknown($"Own player {playerId} had an injury at the same minute as entering; event ordering is ambiguous.");
            if (!wasActive) continue;
            if (!orders.Any(o => o.Type == 1 && o.Subject == playerId && o.Target != playerId
                && o.Minute >= injuryMinute))
                return OfficialMinuteTimeline.Unknown($"Own player {playerId} had a serious on-field injury without a later executed substitution; remaining minutes cannot be established.");
        }

        if (takerKnown && finalTakerId.HasValue && currentTakerId != finalTakerId)
            return OfficialMinuteTimeline.Unknown("The final set-piece taker differs from the reconstructed taker without a timed substitution.");

        return new OfficialMinuteTimeline(true, bonusSegments, "");
    }

    private static void AddTimelineInterval(Dictionary<int, string> roles, Dictionary<int, int> redCardTimes,
        Dictionary<int, List<PositionMinuteSegment>> bonusSegments,
        int? setPiecesTakerId, bool setPiecesTakerKnown, int from, int to)
    {
        if (to <= from) return;
        foreach (var (playerId, slot) in roles)
        {
            var end = Math.Min(to, redCardTimes.GetValueOrDefault(playerId, to));
            var minutes = end - from;
            if (minutes <= 0) continue;
            if (!bonusSegments.TryGetValue(playerId, out var bonuses)) bonusSegments[playerId] = bonuses = new();
            var isTaker = setPiecesTakerKnown && setPiecesTakerId == playerId;
            if (bonuses.Count > 0 && bonuses[^1].Slot == slot
                && bonuses[^1].SetPiecesTakerKnown == setPiecesTakerKnown
                && bonuses[^1].IsSetPiecesTaker == isTaker)
            {
                var last = bonuses[^1];
                bonuses[^1] = last with { Minutes = last.Minutes + minutes };
            }
            else
                bonuses.Add(new PositionMinuteSegment(slot, minutes, setPiecesTakerKnown, isTaker));
        }
    }

    private static bool TryReadElapsedMinute(string? minuteValue, string? partValue, out int minute)
    {
        minute = 0;
        if (!int.TryParse(minuteValue, out var raw) || raw < 0) return false;
        if (!int.TryParse(partValue, out var part)) return false;
        // CHPP documents both fields but does not specify whether minutes in a later MatchPart
        // are local or elapsed. Accept only values that cannot be confused between the two.
        if (raw > 150) return false;
        if (part == 0) { minute = raw; return raw == 0; }
        if (part == 1 && raw is <= 75) { minute = raw; return true; }
        if (part == 2 && raw is > 45 and <= 120) { minute = raw; return true; }
        if (part == 3 && raw is > 90 and <= 150) { minute = raw; return true; }
        return false;
    }

    private static string? ChildValue(System.Xml.Linq.XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private sealed record TimedLineupOrder(int Minute, int Type, int Subject, int Target, string? NewPosition);
    private sealed record PositionMinuteSegment(string Slot, int Minutes, bool SetPiecesTakerKnown, bool IsSetPiecesTaker);
    private sealed record OfficialMinuteTimeline(bool Known,
        Dictionary<int, List<PositionMinuteSegment>> SetPiecesSegments, string Warning)
    {
        public static OfficialMinuteTimeline Unknown(string warning) => new(false, new(), warning);
    }

    private static string? MapChppPosition(string? raw)
    {
        if (!int.TryParse(raw, out var code)) return null;
        if (code is >= 100 and <= 113) return CalibrationService.MapRoleIdToSlot(code);
        return code switch
        {
            1 or 100 => "GK", 2 => "RWB", 3 => "RCD", 4 => "LCD", 5 => "LWB",
            6 => "RW", 7 => "RIM", 8 => "LIM", 9 => "LW", 10 => "RFW", 11 => "LFW",
            _ => null
        };
    }

    /// <summary>Pure approximation of the stamina share in a weekly training allocation.</summary>
    public static double? StaminaTrainingFraction(bool injured, int physicalMinutes, bool statusKnown)
    {
        if (!statusKnown) return null;
        if (injured) return 0;
        if (physicalMinutes <= 0) return 0.5;
        if (physicalMinutes >= 90) return 1;
        return 0.75 + 0.25 * physicalMinutes / 90.0;
    }

    /// <summary>Pure acceptance seam: full minutes are selected first, then half-effect minutes fill the remaining physical 90.</summary>
    public static TrainingMinuteResult ComputeBest90(double fullMinutes, double halfMinutes, int smallEffectMinutes,
        int verySmallEffectMinutes = 0)
    {
        var full = Math.Clamp(fullMinutes, 0, 90);
        var half = Math.Clamp(halfMinutes, 0, 90 - full);
        var remaining = Math.Max(0, (int)Math.Floor(90 - full - half));
        var small = Math.Clamp(smallEffectMinutes, 0, remaining);
        var verySmall = Math.Clamp(verySmallEffectMinutes, 0, remaining - small);
        return new TrainingMinuteResult(Math.Max(0, fullMinutes), Math.Max(0, halfMinutes),
            full, half, small, full + half * 0.5, VerySmallEffectMinutes: verySmall);
    }

    /// <summary>Pure SFG best-90 selection: bonus-role full minutes (1.25), plain full (1), bonus half (.625), plain half (.5).</summary>
    public static TrainingMinuteResult ComputeSetPiecesBest90(double fullMinutes, double halfMinutes,
        double bonusFullMinutes, double bonusHalfMinutes, int smallEffectMinutes, int verySmallEffectMinutes = 0)
    {
        var bonusFull = Math.Clamp(bonusFullMinutes, 0, Math.Min(90, fullMinutes));
        var regularFull = Math.Max(0, fullMinutes - bonusFull);
        var bonusHalf = Math.Clamp(bonusHalfMinutes, 0, Math.Min(90, halfMinutes));
        var regularHalf = Math.Max(0, halfMinutes - bonusHalf);
        var remaining = 90.0;
        var selectedBonusFull = Math.Min(remaining, bonusFull); remaining -= selectedBonusFull;
        var selectedRegularFull = Math.Min(remaining, regularFull); remaining -= selectedRegularFull;
        var selectedBonusHalf = Math.Min(remaining, bonusHalf); remaining -= selectedBonusHalf;
        var selectedRegularHalf = Math.Min(remaining, regularHalf);
        var selectedFull = selectedBonusFull + selectedRegularFull;
        var selectedHalf = selectedBonusHalf + selectedRegularHalf;
        var remainingAfterWeighted = 90 - selectedFull - selectedHalf;
        var selectedSmall = Math.Clamp(smallEffectMinutes, 0, (int)remainingAfterWeighted);
        var selectedVerySmall = Math.Clamp(verySmallEffectMinutes, 0, (int)remainingAfterWeighted - selectedSmall);
        return new TrainingMinuteResult(Math.Max(0, fullMinutes), Math.Max(0, halfMinutes),
            selectedFull, selectedHalf, selectedSmall, selectedFull + selectedHalf * 0.5,
            selectedBonusFull, selectedBonusHalf,
            0.25 * (selectedBonusFull + selectedBonusHalf * 0.5), selectedVerySmall);
    }

    /// <summary>Inspectable relative-speed proxy, not an official skill-pop calculator.</summary>
    public static double? IntensityAndStaminaMultiplier(int? intensityPercent, int? staminaSharePercent)
    {
        if (intensityPercent is < 0 or > 100 || staminaSharePercent is < 0 or > 100
            || !intensityPercent.HasValue || !staminaSharePercent.HasValue) return null;
        return intensityPercent.Value / 100.0 * (1 - staminaSharePercent.Value / 100.0);
    }

    public static double? AssistantSpeedMultiplier(int? assistantSkillTotal) =>
        assistantSkillTotal is >= 0 and <= 10 ? 1 + 0.035 * assistantSkillTotal.Value : null;

    public static double? ApproximateTrainingSpeedMultiplier(int? intensityPercent, int? staminaSharePercent,
        double? coachFactor, int? assistantSkillTotal)
    {
        var baseFactor = IntensityAndStaminaMultiplier(intensityPercent, staminaSharePercent);
        var assistantFactor = AssistantSpeedMultiplier(assistantSkillTotal);
        return baseFactor.HasValue && assistantFactor.HasValue && coachFactor is > 0
            ? baseFactor.Value * assistantFactor.Value * coachFactor.Value : null;
    }

    public static double? GetConfiguredCoachSpeedMultiplier(int? coachSkillRaw, IConfiguration configuration)
    {
        if (!coachSkillRaw.HasValue) return null;
        var value = configuration[$"Training:CoachSpeedFactorBySkill:{coachSkillRaw.Value}"];
        return double.TryParse(value, CultureInfo.InvariantCulture, out var factor) && factor > 0 ? factor : null;
    }

    private async Task<StaffListTrainingData> GetStaffListDataAsync(int teamId)
    {
        if (_api.MockMode)
            return new StaffListTrainingData(true, 1, "Mock coach", "deterministic mock stafflist 1.2", 5, "mock stafflist 1.2 TrainerSkillLevel",
                "Neutral", "mock stafflist 1.2 TrainerType", 10, "mock stafflist 1.2 assistant StaffLevel", new());
        try
        {
            var doc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
            {
                ["file"] = "stafflist", ["teamId"] = teamId.ToString(), ["version"] = "1.2"
            }, $"stafflist teamId={teamId}");
            var staffList = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "StaffList");
            if (staffList == null) return new StaffListTrainingData(false, null, null, null, null, null, null, null, null, null,
                new() { "CHPP stafflist 1.2 response had no StaffList element." });
            var trainer = staffList.Elements().FirstOrDefault(e => e.Name.LocalName == "Trainer");
            var trainerId = ReadStaffInteger(trainer, "TrainerId", 1, int.MaxValue);
            var trainerName = trainer?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
            var coachSkill = ReadStaffInteger(trainer, "TrainerSkillLevel", 1, 5);
            var coachTypeCode = ReadStaffInteger(trainer, "TrainerType", 0, 2);
            var coachType = coachTypeCode switch { 0 => "Defensive", 1 => "Offensive", 2 => "Neutral", _ => null };
            var warnings = new List<string>();
            if (trainer == null) warnings.Add("CHPP stafflist 1.2 omitted Trainer; current coach fields are unknown.");
            else
            {
                if (coachSkill is null) warnings.Add("CHPP stafflist TrainerSkillLevel was missing or outside 1..5; coach speed factor is unknown.");
                if (coachType is null) warnings.Add("CHPP stafflist TrainerType was missing or outside 0..2; coach type is unknown.");
                if (!trainerId.HasValue) warnings.Add("CHPP stafflist TrainerId was missing or malformed.");
            }
            var assistants = staffList.Descendants().Where(e => e.Name.LocalName == "Staff"
                && (e.Elements().FirstOrDefault(c => c.Name.LocalName == "StaffType")?.Value == "1"
                    || string.Equals(e.Elements().FirstOrDefault(c => c.Name.LocalName == "StaffType")?.Value,
                        "AssistantTrainer", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.Elements().FirstOrDefault(c => c.Name.LocalName == "StaffType")?.Value,
                        "AssistantCoach", StringComparison.OrdinalIgnoreCase))).ToList();
            if (assistants.Count > 2) return new StaffListTrainingData(true, trainerId, trainerName,
                "CHPP stafflist 1.2 Trainer", coachSkill,
                coachSkill.HasValue ? "CHPP stafflist 1.2 TrainerSkillLevel" : null, coachType,
                coachType is null ? null : "CHPP stafflist 1.2 TrainerType", null, null,
                warnings.Append("CHPP stafflist contained more than two assistant trainers; total assistant skill is unknown.").ToList());
            var levels = new List<int>();
            foreach (var assistant in assistants)
            {
                var raw = assistant.Elements().FirstOrDefault(c => c.Name.LocalName == "StaffLevel")?.Value;
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
                    || level is < 1 or > 5)
                    return new StaffListTrainingData(true, trainerId, trainerName, "CHPP stafflist 1.2 Trainer", coachSkill,
                        coachSkill.HasValue ? "CHPP stafflist 1.2 TrainerSkillLevel" : null, coachType,
                        coachType is null ? null : "CHPP stafflist 1.2 TrainerType", null, null,
                        warnings.Append("An assistant StaffLevel was missing or outside 1..5; total assistant skill is unknown.").ToList());
                levels.Add(level);
            }
            return new StaffListTrainingData(true, trainerId, trainerName, "CHPP stafflist 1.2 Trainer", coachSkill,
                coachSkill.HasValue ? "CHPP stafflist 1.2 TrainerSkillLevel" : null, coachType,
                coachType is null ? null : "CHPP stafflist 1.2 TrainerType", levels.Sum(),
                "CHPP stafflist 1.2 StaffMembers/Staff StaffType=1 StaffLevel", warnings);
        }
        catch (ChppApiException ex)
        {
            _logger.LogWarning(ex, "CHPP assistant staff levels unavailable for team {TeamId}", teamId);
            return new StaffListTrainingData(false, null, null, null, null, null, null, null, null, null,
                new() { "CHPP stafflist 1.2 request failed; current coach and assistant staff fields are unknown." });
        }
    }

    private static int? ReadStaffInteger(System.Xml.Linq.XElement? parent, string name, int min, int max)
    {
        var raw = parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= min && value <= max ? value : null;
    }

    private async Task<(int? Skill, string? Type)> GetCoachDataAsync(int? coachId)
    {
        if (_api.MockMode || coachId is null or <= 0) return (null, null);
        try
        {
            var coachDoc = await _api.FetchChppXmlAsync(new Dictionary<string, string>
            {
                ["file"] = "playerdetails", ["playerId"] = coachId.Value.ToString(), ["version"] = "1.1"
            }, $"playerdetails coach={coachId}");
            var trainer = coachDoc.Descendants("TrainerData").FirstOrDefault();
            var skill = trainer == null ? null : TryParseNullableInt(trainer, "TrainerSkill");
            var typeCode = trainer == null ? null : TryParseNullableInt(trainer, "TrainerType");
            var type = typeCode switch { 0 => "Defensive", 1 => "Offensive", 2 => "Neutral", _ => null };
            return (skill, type);
        }
        catch (ChppApiException ex)
        {
            _logger.LogWarning(ex, "CHPP coach details unavailable for training summary");
            return (null, null);
        }
    }

    /// <summary>Return the current [update, now] training week in the configured local time zone.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End, bool Configured) GetTrainingWindow(DateTimeOffset now, IConfiguration configuration)
    {
        var hasDay = Enum.TryParse<DayOfWeek>(configuration["Training:UpdateDay"], true, out var updateDay);
        var hasHour = int.TryParse(configuration["Training:UpdateHour"], out var hour) && hour is >= 0 and <= 23;
        var zoneId = configuration["Training:TimeZoneId"];
        var configured = hasDay && hasHour && !string.IsNullOrWhiteSpace(zoneId);
        if (!hasDay) updateDay = DayOfWeek.Thursday;
        if (!hasHour) hour = 22;
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(!string.IsNullOrWhiteSpace(zoneId) ? zoneId : "Europe/Warsaw"); }
        catch (TimeZoneNotFoundException) { throw new InvalidOperationException($"Unknown Training:TimeZoneId '{zoneId}'."); }
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var daysBack = ((int)localNow.DayOfWeek - (int)updateDay + 7) % 7;
        var localStart = new DateTime(localNow.Year, localNow.Month, localNow.Day, hour, 0, 0, DateTimeKind.Unspecified).AddDays(-daysBack);
        if (localStart > localNow.DateTime) localStart = localStart.AddDays(-7);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone), TimeSpan.Zero);
        return (start, now, configured);
    }

    private static string TrainingTypeName(int type) => type switch
    {
        2 => "SetPieces", 3 => "Defending", 4 => "Scoring", 5 => "Crossing",
        6 => "Shooting", 7 => "ShortPasses", 8 => "Playmaking", 9 => "Goalkeeping",
        10 => "ThroughPasses", 11 => "DefensivePositions", 12 => "WingAttacks",
        0 => "General (deprecated)", 1 => "Stamina (deprecated; share is separate)",
        _ => $"Unknown({type})"
    };
    private static string TrainedSkillName(int type) => type switch
    {
        2 => "SetPieces", 3 or 11 => "Defending", 4 or 6 => "Scoring", 5 or 12 => "Winger",
        7 or 10 => "Passing", 8 => "Playmaking", 9 => "Keeper", _ => ""
    };
    private static int TrainedSkillValue(Player p, int type) => type switch
    {
        2 => p.Skills.SetPieces, 3 or 11 => p.Skills.Defending, 4 or 6 => p.Skills.Scoring,
        5 or 12 => p.Skills.Winger, 7 or 10 => p.Skills.Passing, 8 => p.Skills.Playmaking,
        9 => p.Skills.Keeper, _ => 0
    };
    private static bool IsTrainedSkillAvailable(Player p, int type) => type switch
    {
        2 => p.Skills.SetPiecesAvailable, 3 or 11 => p.Skills.DefendingAvailable,
        4 or 6 => p.Skills.ScoringAvailable, 5 or 12 => p.Skills.WingerAvailable,
        7 or 10 => p.Skills.PassingAvailable, 8 => p.Skills.PlaymakingAvailable, 9 => p.Skills.KeeperAvailable,
        _ => false
    };
    private static int ParseInt(System.Xml.Linq.XElement parent, string name) => int.TryParse(parent.Element(name)?.Value, out var value) ? value : 0;
    private static int? TryParseNullableInt(System.Xml.Linq.XElement parent, string name) =>
        int.TryParse(parent.Element(name)?.Value, out var value) ? value : null;
    private static int? ReadExplicitSetPiecesTakerId(System.Xml.Linq.XElement? team)
    {
        if (team == null) return null;
        var names = new[] { "SetPiecesTakerID", "SetPiecesTakerId", "PenaltyTakerID", "SetPiecesTaker" };
        foreach (var element in team.DescendantsAndSelf().Where(e => names.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase)))
        {
            if (int.TryParse(element.Value, out var id) && id > 0) return id;
            var nestedId = element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("PlayerID", StringComparison.OrdinalIgnoreCase));
            if (int.TryParse(nestedId?.Value, out id) && id > 0) return id;
        }
        return null;
    }
    private static int? ReadRole17PlayerId(System.Xml.Linq.XElement? lineup)
    {
        var player = lineup?.Elements().FirstOrDefault(e => e.Name.LocalName == "Player"
            && ChildValue(e, "RoleID") == "17");
        return int.TryParse(player == null ? null : ChildValue(player, "PlayerID"), out var playerId)
            ? playerId : null;
    }
    private static DateTime? ParseDate(string? value) => DateTime.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

    private sealed class PlayerTrainingMinutes
    {
        public int Coverage { get; set; }
        public int Full { get; set; }
        public int Half { get; set; }
        public int Small { get; set; }
        public int VerySmall { get; set; }
        public double SetPiecesBonusFull { get; set; }
        public double SetPiecesBonusHalf { get; set; }
        public int SetPiecesBonusUnknownMinutes { get; set; }
        public string LastSlot { get; set; } = "";
        public List<string> Warnings { get; } = new();
    }

    private sealed record StaffListTrainingData(bool Available, int? TrainerId, string? TrainerName, string? IdentitySource,
        int? CoachSkill, string? CoachSkillSource, string? CoachType, string? CoachTypeSource,
        int? AssistantSkillTotal, string? AssistantSkillSource, List<string> Warnings);
}

public class TrainingSummary
{
    public int TeamId { get; set; }
    public int TrainingTypeCode { get; set; }
    public bool TrainingTypeKnown { get; set; }
    public string TrainingTypeName { get; set; } = "";
    public string TrainedSkill { get; set; } = "";
    public int TrainingLevel { get; set; }
    public int StaminaTrainingPart { get; set; }
    public int? StaminaTrainingSharePercent { get; set; }
    public bool StaminaTrainingPartKnown { get; set; }
    public int? TrainingIntensityPercent { get; set; }
    public int? TeamSpiritLevel { get; set; }
    public int? ConfidenceLevel { get; set; }
    public int? CoachSkillRaw { get; set; }
    public string? CoachSkillSource { get; set; }
    public string? CoachType { get; set; }
    public string? CoachTypeSource { get; set; }
    public int? TrainerId { get; set; }
    public int? AssistantSkillTotal { get; set; }
    public string? AssistantSkillSource { get; set; }
    public double? ApproximateTrainingSpeedMultiplier { get; set; }
    public double? IntensityAndStaminaMultiplier { get; set; }
    public double? AssistantSpeedMultiplier { get; set; }
    public double? CoachSpeedMultiplier { get; set; }
    public string TrainingSpeedMethod { get; set; } = "";
    public string TrainerName { get; set; } = "";
    public string? TrainerIdentitySource { get; set; }
    public long LastMatchId { get; set; }
    public DateTime? LastMatchDate { get; set; }
    public DateTime WeekStart { get; set; }
    public DateTime WeekEnd { get; set; }
    public bool IsEstimate { get; set; }
    public List<string> Warnings { get; set; } = new();
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
    public bool TrainedSkillAvailable { get; set; }
    public int CoverageMinutes { get; set; }
    public double FullTrainingMinutes { get; set; }
    public double HalfTrainingMinutes { get; set; }
    public double ObservedFullMinutes { get; set; }
    public double ObservedHalfMinutes { get; set; }
    public int SmallEffectMinutes { get; set; }
    public int VerySmallEffectMinutes { get; set; }
    public int ObservedSmallEffectMinutes { get; set; }
    public int ObservedVerySmallEffectMinutes { get; set; }
    public double EffectiveTrainingMinutes { get; set; }
    public double SetPiecesSpecialBonusMinutes { get; set; }
    public double SetPiecesBonusFullMinutes { get; set; }
    public double SetPiecesBonusHalfMinutes { get; set; }
    public int SetPiecesBonusUnknownMinutes { get; set; }
    public double TrainingFraction { get; set; }
    public double? StaminaTrainingFraction { get; set; }
    public bool IsEstimate { get; set; }
    public List<string> Warnings { get; set; } = new();
    public double? EstimatedWeeksToNextLevel { get; set; }
}

public record TrainingMinuteResult(double ObservedFullMinutes, double ObservedHalfMinutes,
    double FullMinutes, double HalfMinutes, int SmallEffectMinutes, double EffectiveMinutes,
    double BonusFullMinutes = 0, double BonusHalfMinutes = 0, double SpecialBonusMinutes = 0,
    int VerySmallEffectMinutes = 0);
