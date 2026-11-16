using MatchmakingService.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Contracts;

namespace MatchmakingService.Controllers;

[ApiController]
[Route("api/matchmaking")]
[Authorize]
public sealed class MatchmakingController(MatchmakingQueue queue, MatchHistory history) : ControllerBase
{
    private string Username => User.Identity?.Name ?? throw new InvalidOperationException("Token mangler name-claim");

    [HttpPost("queue")]
    [EndpointSummary("Stil dig i kø til et match")]
    [EndpointDescription("Returnerer 202 Accepted med et ticket. Poll Location-headeren for status.")]
    [ProducesResponseType<TicketResponse>(StatusCodes.Status202Accepted)]
    public IActionResult Enqueue()
    {
        var ticket = queue.Enqueue(Username);
        return AcceptedAtAction(nameof(GetTicket), new { id = ticket.Id }, ticket.ToResponse());
    }

    [HttpGet("tickets/{id:guid}")]
    [EndpointSummary("Status på et ticket")]
    [ProducesResponseType<TicketResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetTicket(Guid id)
    {
        var ticket = queue.Get(id);
        // 404 (ikke 403) for andres tickets – afslører ikke at ticket-id'et findes
        if (ticket is null || !string.Equals(ticket.Username, Username, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }
        return Ok(ticket.ToResponse());
    }

    [HttpDelete("tickets/{id:guid}")]
    [EndpointSummary("Forlad køen")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Cancel(Guid id) => queue.Cancel(id, Username) ? NoContent() : NotFound();

    [AllowAnonymous]
    [HttpGet("recent-matches")]
    [EndpointSummary("Seneste afsluttede matches")]
    [ProducesResponseType<IReadOnlyList<MatchSummary>>(StatusCodes.Status200OK)]
    public IActionResult RecentMatches() => Ok(history.Recent());
}

/// <summary>Interne endpoints – eksponeres IKKE gennem API gatewayen.</summary>
[ApiController]
[Route("api/servers")]
public sealed class ServersController(ServerRegistry registry) : ControllerBase
{
    [HttpPost("register")]
    [InternalApiKey]
    [EndpointSummary("Gameserver self-registration")]
    public IActionResult Register(RegisterServerRequest request)
    {
        registry.Register(request);
        return NoContent();
    }

    [HttpPost("{serverId}/heartbeat")]
    [InternalApiKey]
    [EndpointSummary("Gameserver heartbeat med aktuelt spillerantal")]
    public IActionResult Heartbeat(string serverId, HeartbeatRequest request) =>
        registry.Heartbeat(serverId, request.CurrentPlayers) ? NoContent() : NotFound();

    [HttpGet]
    [Authorize(Roles = Roles.Admin)]
    [EndpointSummary("Oversigt over registrerede gameservere (admin)")]
    public IActionResult GetAll() => Ok(registry.GetAll());
}
