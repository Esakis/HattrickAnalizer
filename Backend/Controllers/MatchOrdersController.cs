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

    [HttpPost("preview")]
    public IActionResult Preview([FromBody] MatchOrdersRequest request)
    {
        var error = MatchOrdersService.ValidateRequest(request);
        if (error != null) return BadRequest(new { error });
        return Ok(new
        {
            payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(MatchOrdersService.BuildLineupJson(request)),
            liveWriteAllowed = !_matchOrders.MockMode,
            message = "Preview only. Current roster, player availability and match ownership are checked again when sending."
        });
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
