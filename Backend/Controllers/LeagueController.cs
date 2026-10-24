using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Services;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LeagueController : ControllerBase
{
    private readonly LeagueSimulationService _simulation;
    private readonly TokenStore _tokenStore;
    private readonly IConfiguration _configuration;

    public LeagueController(LeagueSimulationService simulation, TokenStore tokenStore, IConfiguration configuration)
    {
        _simulation = simulation;
        _tokenStore = tokenStore;
        _configuration = configuration;
    }

    /// <summary>
    /// Symulacja pozostalych meczow sezonu ligi zalogowanego uzytkownika —
    /// prawdopodobienstwa pozycji koncowych (Monte Carlo na modelu Poissona).
    /// </summary>
    // fromFirstRound=true — symulacja całego sezonu od 1. kolejki zamiast od stanu obecnego.
    [HttpGet("simulation")]
    public async Task<IActionResult> GetSimulation([FromQuery] bool fromFirstRound = false)
    {
        if (_configuration.GetValue<bool>("UseMockData")) return Ok(await _simulation.SimulateAsync(MockChppData.TeamId, fromFirstRound));
        var sessionId = Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || stored.OwnTeamId == 0)
        {
            return Unauthorized(new { error = "Brak autoryzacji OAuth — zaloguj się do Hattricka." });
        }

        var report = await _simulation.SimulateAsync(stored.OwnTeamId, fromFirstRound);
        return Ok(report);
    }
}
