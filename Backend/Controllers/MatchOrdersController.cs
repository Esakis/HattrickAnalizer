using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Services;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MatchOrdersController : ControllerBase
{
    private readonly MatchOrdersService _matchOrders;

    public MatchOrdersController(MatchOrdersService matchOrders)
    {
        _matchOrders = matchOrders;
    }

    /// <summary>
    /// Wysyła ustawienie meczowe do Hattricka. Wymaga tokenu ze scope set_matchorder —
    /// bez niego zwraca 502 z komunikatem CHPP i instrukcją ponownej autoryzacji.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SendLineup([FromBody] MatchOrdersRequest request)
    {
        var validationError = MatchOrdersService.ValidateRequest(request);
        if (validationError != null)
        {
            return BadRequest(new MatchOrdersResult { Success = false, Error = validationError, RawResponse = "validation" });
        }

        var result = await _matchOrders.SendLineupAsync(request);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }
}
