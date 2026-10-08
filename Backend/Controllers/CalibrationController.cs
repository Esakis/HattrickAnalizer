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

    public CalibrationController(CalibrationService calibration, TokenStore tokenStore,
        HattrickApiService api, CalibrationSnapshotStore snapshots)
    {
        _calibration = calibration;
        _tokenStore = tokenStore;
        _api = api;
        _snapshots = snapshots;
    }

    [HttpGet("own-matches")]
    public async Task<IActionResult> CompareOwnMatches([FromQuery] int count = 5)
    {
        var sessionId = Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || stored.OwnTeamId == 0)
        {
            return Unauthorized(new { error = "Brak autoryzacji OAuth — zaloguj się do Hattricka." });
        }

        var report = await _calibration.CompareOwnMatchesAsync(stored.OwnTeamId, count);
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
        var players = await _api.GetTeamPlayersAsync(teamId);
        var snapshot = new CalibrationSnapshot
        {
            TeamId = teamId,
            RecordedAt = DateTimeOffset.UtcNow,
            Players = players,
            Context = context ?? new CalibrationMatchContext(),
            Source = "CHPP own roster",
            Warnings = new List<string> { "Use only for matches whose kickoff is at or after RecordedAt; unknown match context is excluded from calibration." }
        };
        try { await _snapshots.SaveAsync(snapshot); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        return Ok(snapshot);
    }

    [HttpPost("snapshots/import")]
    public async Task<IActionResult> ImportSnapshot([FromBody] CalibrationSnapshot snapshot)
    {
        var teamId = GetOwnTeamId();
        if (teamId == 0) return Unauthorized(new { error = "No authenticated own team." });
        if (snapshot.TeamId != teamId) return BadRequest(new { error = "Snapshot team id must match the authenticated own team." });
        try { await _snapshots.SaveAsync(snapshot); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        return Ok(new { success = true, recordedAt = snapshot.RecordedAt, matchId = snapshot.Context.MatchId });
    }

    private int GetOwnTeamId()
    {
        var sessionId = Request.Cookies["ht_session"] ?? "";
        return _tokenStore.Get(sessionId)?.OwnTeamId ?? 0;
    }
}
