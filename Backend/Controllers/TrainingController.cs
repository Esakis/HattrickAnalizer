using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Services;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TrainingController : ControllerBase
{
    private readonly TrainingService _training;
    private readonly TokenStore _tokenStore;
    private readonly IConfiguration _configuration;

    public TrainingController(TrainingService training, TokenStore tokenStore, IConfiguration configuration)
    {
        _training = training;
        _tokenStore = tokenStore;
        _configuration = configuration;
    }

    /// <summary>
    /// Podsumowanie treningu druzyny zalogowanego uzytkownika: typ, intensywnosc,
    /// kto dostal pelny trening i orientacyjna prognoza skoku skilla.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        if (_configuration.GetValue<bool>("UseMockData")) return Ok(await _training.GetSummaryAsync(MockChppData.TeamId));
        var sessionId = Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || stored.OwnTeamId == 0)
        {
            return Unauthorized(new { error = "Brak autoryzacji OAuth — zaloguj się do Hattricka." });
        }

        var summary = await _training.GetSummaryAsync(stored.OwnTeamId);
        return Ok(summary);
    }
}
