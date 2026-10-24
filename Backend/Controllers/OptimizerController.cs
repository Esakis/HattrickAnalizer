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
        return Ok(await _optimizer.OptimizeLineupAsync(request));
    }

    [HttpPost("evaluate")]
    public async Task<IActionResult> EvaluateLineup([FromBody] EvaluateLineupRequest request)
    {
        return Ok(await _optimizer.EvaluateLineupAsync(request));
    }
}
