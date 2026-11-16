using MatchmakingService.Domain;
using Microsoft.Extensions.Options;
using Shared.Contracts;
using Shared.Messaging;

namespace MatchmakingService.Workers;

/// <summary>
/// Health-check invoker: kalder hver registreret servers /health/ready og
/// markerer den sund/usund. Kun sunde servere får tildelt spillere.
/// </summary>
public sealed class HealthCheckInvoker(
    ServerRegistry registry,
    IHttpClientFactory httpClientFactory,
    ILogger<HealthCheckInvoker> logger) : BackgroundService
{
    public const string ClientName = "health";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var removed = registry.RemoveStale();
            if (removed > 0) logger.LogWarning("{Count} server(e) fjernet pga. manglende heartbeat", removed);

            var client = httpClientFactory.CreateClient(ClientName);
            await Task.WhenAll(registry.GetHealthTargets().Select(async target =>
            {
                bool healthy;
                try
                {
                    using var response = await client.GetAsync(target.HealthUrl, stoppingToken);
                    healthy = response.IsSuccessStatusCode;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    healthy = false;
                }

                registry.ReportHealth(target.ServerId, healthy);
            }));
        }
    }
}

/// <summary>Danner matches når servere bliver sunde, og rydder gamle tickets op.</summary>
public sealed class MatchmakerWorker(MatchmakingQueue queue) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            queue.TryFormMatches();
            queue.RemoveExpired();
        }
    }
}

/// <summary>
/// Matchmaking abonnerer på samme MatchEnded-event som StatsService, men med sin EGEN kø.
/// Det er publish/subscribe: gameserveren kender ingen af sine consumers.
/// </summary>
public sealed class MatchEndedConsumer(
    MatchHistory history,
    IOptions<RabbitMqOptions> options,
    ILogger<MatchEndedConsumer> logger) : RabbitConsumerBase<MatchEnded>(options, logger)
{
    protected override string QueueName => "matchmaking.match-ended";
    protected override IReadOnlyList<string> RoutingKeys => [Topology.MatchEndedKey];

    protected override Task HandleAsync(MatchEnded message, string routingKey, CancellationToken ct)
    {
        var winner = message.Results.OrderByDescending(r => r.Score).FirstOrDefault()?.Username;
        history.TryAdd(new MatchSummary(message.MatchId, message.ServerId, winner, message.Results.Count, message.OccurredAtUtc));
        return Task.CompletedTask;
    }
}
