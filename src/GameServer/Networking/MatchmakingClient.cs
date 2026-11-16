using System.Net;
using Shared.Contracts;

namespace GameServer.Networking;

/// <summary>Typed client: samler adresse, auth-header og resilience for integrationen med matchmaking ét sted.</summary>
public sealed class MatchmakingClient(HttpClient http)
{
    public async Task RegisterAsync(RegisterServerRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/servers/register", request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <returns>false hvis matchmaking ikke kender serveren (=> registrér igen).</returns>
    public async Task<bool> HeartbeatAsync(string serverId, int currentPlayers, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/servers/{Uri.EscapeDataString(serverId)}/heartbeat", new HeartbeatRequest(currentPlayers), ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}
