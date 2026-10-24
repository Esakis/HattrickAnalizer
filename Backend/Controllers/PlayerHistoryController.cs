using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Services;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PlayerHistoryController : ControllerBase
{
    private readonly PlayerHistoryService _history;
    private readonly HattrickApiService _api;
    private readonly TokenStore _tokens;

    public PlayerHistoryController(PlayerHistoryService history, HattrickApiService api, TokenStore tokens)
    {
        _history = history; _api = api; _tokens = tokens;
    }

    private int OwnTeamId => _api.MockMode ? 1001 : _tokens.Get(Request.Cookies["ht_session"] ?? "")?.OwnTeamId ?? 0;

    [HttpGet("{playerId:int}")]
    public async Task<IActionResult> GetHistory(int playerId)
    {
        var teamId = OwnTeamId;
        if (teamId <= 0) return Unauthorized(new { error = "No authenticated own team." });
        return Ok(await _history.GetPlayerHistoryAsync(playerId, teamId));
    }

    [HttpGet("recent")]
    public async Task<IActionResult> Recent([FromQuery] int count = 30)
    {
        var teamId = OwnTeamId;
        if (teamId <= 0) return Unauthorized(new { error = "No authenticated own team." });
        return Ok(await _history.GetRecentChangesAsync(teamId, count));
    }

    [HttpPost("sync")]
    [HttpPost("check-and-save")]
    public async Task<IActionResult> Sync()
    {
        var teamId = OwnTeamId;
        if (teamId <= 0) return Unauthorized(new { error = "No authenticated own team." });
        // Body data and arbitrary team ids are deliberately never used as historical observations.
        var players = await _api.GetTeamPlayersAsync(teamId);
        return Ok(await _history.CheckAndSaveChangesAsync(players, teamId));
    }
}
