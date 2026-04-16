using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Services;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TeamController : ControllerBase
{
    private readonly HattrickApiService _hattrickApi;
    private readonly TokenStore _tokenStore;

    public TeamController(HattrickApiService hattrickApi, TokenStore tokenStore)
    {
        _hattrickApi = hattrickApi;
        _tokenStore = tokenStore;
    }

    [HttpGet("{teamId}")]
    public async Task<IActionResult> GetTeam(int teamId)
    {
        try
        {
            var team = await _hattrickApi.GetTeamDetailsAsync(teamId);
            return Ok(team);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{teamId}/players")]
    public async Task<IActionResult> GetPlayers(int teamId)
    {
        try
        {
            var players = await _hattrickApi.GetTeamPlayersAsync(teamId);
            return Ok(players);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{teamId}/match-stats")]
    public async Task<IActionResult> GetTeamMatchStats(int teamId)
    {
        try
        {
            var teamStats = await _hattrickApi.GetTeamMatchStatsAsync(teamId);
            return Ok(teamStats);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("next-opponent")]
    public async Task<IActionResult> GetNextOpponent()
    {
        try
        {
            var stored = _tokenStore.Get();
            if (stored == null || stored.OwnTeamId == 0)
            {
                return BadRequest(new { error = "Brak zapisanego teamId — dokończ autoryzację OAuth." });
            }
            var info = await _hattrickApi.GetNextOpponentAsync(stored.OwnTeamId);
            if (info == null)
            {
                return NotFound(new { error = "Brak nadchodzących meczów." });
            }
            return Ok(info);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{teamId}/formation-experience")]
    public async Task<IActionResult> GetFormationExperience(int teamId)
    {
        try
        {
            var formationExperience = await _hattrickApi.GetFormationExperienceAsync(teamId);
            return Ok(formationExperience);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
