using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Services;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TeamController : ControllerBase
{
    private readonly HattrickApiService _hattrickApi;
    private readonly TokenStore _tokenStore;
    private readonly OpponentScoutService _scout;

    public TeamController(HattrickApiService hattrickApi, TokenStore tokenStore, OpponentScoutService scout)
    {
        _hattrickApi = hattrickApi;
        _tokenStore = tokenStore;
        _scout = scout;
    }

    [HttpGet("{teamId}")]
    public async Task<IActionResult> GetTeam(int teamId)
    {
        var team = await _hattrickApi.GetTeamDetailsAsync(teamId);
        return Ok(team);
    }

    [HttpGet("{teamId}/players")]
    public async Task<IActionResult> GetPlayers(int teamId)
    {
        var players = await _hattrickApi.GetTeamPlayersAsync(teamId);
        return Ok(players);
    }

    [HttpGet("{teamId}/match-stats")]
    public async Task<IActionResult> GetTeamMatchStats(int teamId)
    {
        var teamStats = await _hattrickApi.GetTeamMatchStatsAsync(teamId);
        return Ok(teamStats);
    }

    [HttpGet("next-opponent")]
    public async Task<IActionResult> GetNextOpponent()
    {
        var sessionId = Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || stored.OwnTeamId == 0)
        {
            return Unauthorized(new { error = "Brak zapisanego teamId — dokończ autoryzację OAuth." });
        }
        var info = await _hattrickApi.GetNextOpponentAsync(stored.OwnTeamId);
        if (info == null)
        {
            return NotFound(new { error = "Brak nadchodzących meczów." });
        }
        return Ok(info);
    }

    // Raport skauta: agregacja ostatnich meczów dowolnej drużyny (formacja, taktyka,
    // ważone oceny, przewidywana XI). Publiczne dane CHPP, cache po stronie serwisu.
    [HttpGet("{teamId}/scout")]
    public async Task<IActionResult> GetScoutReport(int teamId, [FromQuery] int count = 5)
    {
        var report = await _scout.GetScoutReportAsync(teamId, count);
        return Ok(report);
    }

    [HttpGet("{teamId}/formation-experience")]
    public async Task<IActionResult> GetFormationExperience(int teamId)
    {
        var formationExperience = await _hattrickApi.GetFormationExperienceAsync(teamId);
        return Ok(formationExperience);
    }
}
