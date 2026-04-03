using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Services;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TeamController : ControllerBase
{
    private readonly HattrickApiService _hattrickApi;

    public TeamController(HattrickApiService hattrickApi)
    {
        _hattrickApi = hattrickApi;
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
}
