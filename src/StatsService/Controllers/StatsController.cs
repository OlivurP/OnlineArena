using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StatsService.Domain;

namespace StatsService.Controllers;

[ApiController]
[Route("api/stats")]
public sealed class StatsController(StatsStore store) : ControllerBase
{
    [HttpGet("leaderboard")]
    [EndpointSummary("Top-spillere (offentlig)")]
    [ProducesResponseType<IReadOnlyList<PlayerStatsView>>(StatusCodes.Status200OK)]
    public IActionResult Leaderboard([FromQuery] int top = 10) => Ok(store.Leaderboard(top));

    [Authorize]
    [HttpGet("players/{username}")]
    [EndpointSummary("Statistik for én spiller")]
    [ProducesResponseType<PlayerStatsView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Player(string username) =>
        store.Get(username) is { } stats ? Ok(stats) : NotFound();
}
