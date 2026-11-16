using GameServer.Networking;
using Microsoft.Extensions.Options;
using Shared.Contracts;

namespace GameServer.Workers;

/// <summary>Self-registration hos matchmaking + løbende heartbeats med aktuelt spillerantal.</summary>
public sealed class RegistrationWorker(
    MatchmakingClient client,
    ServerState state,
    IOptions<GameServerOptions> options,
    ILogger<RegistrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        while (!state.IsReady) await Task.Delay(200, stoppingToken);

        var request = new RegisterServerRequest(
            o.ServerId, o.PublicHost, o.PublicPort, $"{o.InternalBaseUrl.TrimEnd('/')}/health/ready", o.Capacity);

        var registered = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!registered)
                {
                    await client.RegisterAsync(request, stoppingToken);
                    registered = true;
                    logger.LogInformation("Registreret hos matchmaking som {ServerId}", o.ServerId);
                }
                else if (!await client.HeartbeatAsync(o.ServerId, state.CurrentPlayers, stoppingToken))
                {
                    logger.LogWarning("Matchmaking kender ikke serveren – registrerer igen");
                    registered = false;
                    continue;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Resilience-pipelinen har allerede prøvet igen; vi fortsætter bare i næste interval
                logger.LogWarning("Kontakt til matchmaking fejlede: {Message}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(o.HeartbeatSeconds), stoppingToken);
        }
    }
}
