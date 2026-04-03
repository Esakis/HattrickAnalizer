using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OptimizerController : ControllerBase
{
    private readonly LineupOptimizerService _optimizer;

    public OptimizerController(LineupOptimizerService optimizer)
    {
        _optimizer = optimizer;
    }

    [HttpPost("optimize")]
    public async Task<IActionResult> OptimizeLineup([FromBody] OptimizerRequest request)
    {
        try
        {
            var result = await _optimizer.OptimizeLineupAsync(request);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
