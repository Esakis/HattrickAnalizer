using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Services;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Controllers;

public record CheckAndSaveRequest(List<Player> Players, int TeamId);

[ApiController]
[Route("api/[controller]")]
public class PlayerHistoryController : ControllerBase
{
    private readonly PlayerHistoryService _historyService;

    public PlayerHistoryController(PlayerHistoryService historyService)
    {
        _historyService = historyService;
    }

    [HttpGet("{playerId}")]
    public async Task<IActionResult> GetHistory(int playerId)
    {
        try
        {
            var history = await _historyService.GetPlayerHistoryAsync(playerId);
            return Ok(history);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayerHistoryController] Błąd: {ex.Message}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("check-and-save")]
    public async Task<IActionResult> CheckAndSave([FromBody] CheckAndSaveRequest request)
    {
        try
        {
            var results = await _historyService.CheckAndSaveChangesAsync(request.Players, request.TeamId);
            return Ok(results);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayerHistoryController] Błąd check-and-save: {ex.Message}");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
