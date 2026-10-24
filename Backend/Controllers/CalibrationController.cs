using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;

namespace HattrickAnalizer.Controllers;

/// <summary>
/// Narzędzie deweloperskie: porównuje przewidywania RatingEngine z prawdziwymi
/// ocenami sektorowymi z rozegranych meczów zalogowanego użytkownika.
/// GET /api/calibration/own-matches?count=5
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CalibrationController : ControllerBase
{
    private readonly CalibrationService _calibration;
    private readonly TokenStore _tokenStore;
    private readonly HattrickApiService _api;
    private readonly CalibrationSnapshotStore _snapshots;
    private readonly RatingEngine _ratingEngine;

    public CalibrationController(CalibrationService calibration, TokenStore tokenStore,
        HattrickApiService api, CalibrationSnapshotStore snapshots, RatingEngine ratingEngine)
    {
        _calibration = calibration;
        _tokenStore = tokenStore;
        _api = api;
        _snapshots = snapshots;
        _ratingEngine = ratingEngine;
    }

    [HttpGet("own-matches")]
    public async Task<IActionResult> CompareOwnMatches([FromQuery] int count = 5)
    {
        var teamId = GetOwnTeamId();
        if (teamId == 0)
        {
            return Unauthorized(new { error = "Brak autoryzacji OAuth — zaloguj się do Hattricka." });
        }

        var report = await _calibration.CompareOwnMatchesAsync(teamId, count);
        return Ok(report);
    }

    [HttpGet("snapshots")]
    public async Task<IActionResult> GetSnapshots()
    {
        var teamId = GetOwnTeamId();
        if (teamId == 0) return Unauthorized(new { error = "No authenticated own team." });
        return Ok(await _snapshots.GetAsync(teamId));
    }

    [HttpPost("snapshots/capture")]
    public async Task<IActionResult> CaptureSnapshot([FromBody] CalibrationMatchContext? context)
    {
        var teamId = GetOwnTeamId();
        if (teamId == 0) return Unauthorized(new { error = "No authenticated own team." });
        context ??= new CalibrationMatchContext();
        var missing = context.MissingFields;
        if (context.PredictedRatings != null && string.IsNullOrWhiteSpace(context.ModelVersion)) missing.Add("modelVersion");
        if (missing.Count > 0)
            return Ok(new { saved = false, complete = false, missingFields = missing,
                warnings = new[] { "No snapshot saved: complete the missing match context before kickoff." }, snapshot = (CalibrationSnapshot?)null });
        var players = await _api.GetTeamPlayersAsync(teamId);
        var missingPlayers = context.PlayerIdsBySlot.Values.Where(id => !players.Any(p => p.PlayerId == id && p.SkillsAvailable)).ToList();
        if (missingPlayers.Count > 0)
            return Ok(new { saved = false, complete = false, missingFields = new[] { "players.privateSkills" },
                warnings = new[] { "Some lineup players are missing from the own roster or their private skills are unavailable." }, snapshot = (CalibrationSnapshot?)null });
        var lineup = new Lineup { Formation = context.Formation, TacticType = context.Tactic! };
        foreach (var (slot, id) in context.PlayerIdsBySlot)
            lineup.Positions[slot] = new LineupPosition { Position = slot, Player = players.Single(p => p.PlayerId == id), Behavior = context.BehavioursBySlot[slot] };
        var computed = _ratingEngine.ComputeContextualRatings(lineup, context.WeatherId!.Value, context.Attitude!, context.CoachType!,
            AdvancedLineupOptimizer.ComputeDisorderRisk(context.FormationExperience!.Value), context.TeamSpiritLevel,
            context.ConfidenceLevel, applyHomeAdvantage: context.IsHomeMatch!.Value);
        _ratingEngine.ApplyTactic(computed, context.Tactic!);
        if (context.PredictedRatings is { } submitted
            && (context.ModelVersion != MatchPredictionModel.Version || !SameRatings(submitted, computed)))
            return Ok(new { saved = false, complete = false, missingFields = new[] { "predictedRatings" },
                warnings = new[] { "The displayed forecast does not match the current roster, context or model version. Recalculate the lineup before saving its forecast." }, snapshot = (CalibrationSnapshot?)null });
        var ratings = context.PredictedRatings ?? computed;
        var snapshot = new CalibrationSnapshot
        {
            TeamId = teamId,
            RecordedAt = DateTimeOffset.UtcNow,
            Players = players,
            Context = context,
            Source = "CHPP own roster",
            PredictedRatings = ratings,
            ModelVersion = MatchPredictionModel.Version,
            PredictionSource = "captured-before-match",
            Warnings = new List<string> { "Use only for matches whose kickoff is at or after RecordedAt; unknown match context is excluded from calibration." }
        };
        try { await _snapshots.SaveAsync(snapshot); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        return Ok(new { saved = true, complete = true, missingFields = Array.Empty<string>(), warnings = snapshot.Warnings, snapshot });
    }

    [HttpPost("snapshots/import")]
    public async Task<IActionResult> ImportSnapshot([FromBody] CalibrationSnapshot snapshot)
    {
        var teamId = GetOwnTeamId();
        if (teamId == 0) return Unauthorized(new { error = "No authenticated own team." });
        if (snapshot.TeamId != teamId) return BadRequest(new { error = "Snapshot team id must match the authenticated own team." });
        if (snapshot.PredictedRatings != null) snapshot.PredictionSource = "imported";
        try { await _snapshots.SaveAsync(snapshot); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        return Ok(new { success = true, recordedAt = snapshot.RecordedAt, matchId = snapshot.Context.MatchId });
    }

    private int GetOwnTeamId()
    {
        if (_api.MockMode) return 1001;
        var sessionId = Request.Cookies["ht_session"] ?? "";
        return _tokenStore.Get(sessionId)?.OwnTeamId ?? 0;
    }

    private static bool SameRatings(LineupRatings submitted, LineupRatings computed)
    {
        var left = new[] { submitted.Midfield, submitted.RightDefense, submitted.CentralDefense, submitted.LeftDefense,
            submitted.RightAttack, submitted.CentralAttack, submitted.LeftAttack, submitted.Overall };
        var right = new[] { computed.Midfield, computed.RightDefense, computed.CentralDefense, computed.LeftDefense,
            computed.RightAttack, computed.CentralAttack, computed.LeftAttack, computed.Overall };
        return left.Zip(right).All(pair => double.IsFinite(pair.First) && Math.Abs(pair.First - pair.Second) <= 1e-8);
    }
}
