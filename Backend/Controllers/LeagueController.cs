using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Services;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LeagueController : ControllerBase
{
    private readonly LeagueSimulationService _simulation;
    private readonly TokenStore _tokenStore;

    public LeagueController(LeagueSimulationService simulation, TokenStore tokenStore)
    {
        _simulation = simulation;
        _tokenStore = tokenStore;
    }

    /// <summary>
    /// Symulacja pozostalych meczow sezonu ligi zalogowanego uzytkownika —
    /// prawdopodobienstwa pozycji koncowych (Monte Carlo na modelu Poissona).
    /// </summary>
    [HttpGet("simulation")]
    public async Task<IActionResult> GetSimulation()
    {
        var sessionId = Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || stored.OwnTeamId == 0)
        {
            return Unauthorized(new { error = "Brak autoryzacji OAuth — zaloguj się do Hattricka." });
        }

        var report = await _simulation.SimulateAsync(stored.OwnTeamId);
        return Ok(report);
    }
}
