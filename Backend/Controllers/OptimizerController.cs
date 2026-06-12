using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OptimizerController : ControllerBase
{
    private readonly AdvancedLineupOptimizer _optimizer;

    public OptimizerController(AdvancedLineupOptimizer optimizer)
    {
        _optimizer = optimizer;
    }

    [HttpPost("optimize")]
    public async Task<IActionResult> OptimizeLineup([FromBody] OptimizerRequest request)
    {
        var result = await _optimizer.OptimizeLineupAsync(request);
        return Ok(result);
    }
}
