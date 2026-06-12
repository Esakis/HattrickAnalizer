using Microsoft.AspNetCore.Mvc;
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

    public CalibrationController(CalibrationService calibration, TokenStore tokenStore)
    {
        _calibration = calibration;
        _tokenStore = tokenStore;
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
}
