using Microsoft.Extensions.Options;
using Shared.Contracts;
using Shared.Messaging;

namespace StatsService.Domain;

public sealed record PlayerStatsView(string Username, int MatchesPlayed, int Wins, int TotalScore, int BestScore);

/// <summary>
/// Read model (CQRS-query-side) bygget ud fra events. In-memory her; i produktion en database,
/// hvor "processed message ids" gemmes i SAMME transaktion som statistikken.
/// </summary>
public sealed class StatsStore
{
    private readonly object _lock = new();
    private readonly HashSet<Guid> _processedMessages = [];
    private readonly Dictionary<string, PlayerStats> _players = new(StringComparer.OrdinalIgnoreCase);

    /// <returns>false hvis eventet allerede er behandlet (idempotent consumer).</returns>
    public bool Apply(MatchEnded match)
    {
        lock (_lock)
        {
            if (!_processedMessages.Add(match.MessageId)) return false;

            var topScore = match.Results.Count == 0 ? 0 : match.Results.Max(r => r.Score);
            foreach (var result in match.Results)
            {
                if (!_players.TryGetValue(result.Username, out var stats))
                {
                    stats = new PlayerStats(result.Username);
                    _players[result.Username] = stats;
                }

                stats.MatchesPlayed++;
                stats.TotalScore += result.Score;
                stats.BestScore = Math.Max(stats.BestScore, result.Score);
                if (topScore > 0 && result.Score == topScore) stats.Wins++;
            }

            return true;
        }
    }

    public IReadOnlyList<PlayerStatsView> Leaderboard(int top)
    {
        lock (_lock)
        {
            return _players.Values
                .OrderByDescending(p => p.Wins)
                .ThenByDescending(p => p.TotalScore)
                .Take(Math.Clamp(top, 1, 100))
                .Select(p => p.ToView())
                .ToList();
        }
    }

    public PlayerStatsView? Get(string username)
    {
        lock (_lock) return _players.GetValueOrDefault(username)?.ToView();
    }

    private sealed class PlayerStats(string username)
    {
        public string Username { get; } = username;
        public int MatchesPlayed { get; set; }
        public int Wins { get; set; }
        public int TotalScore { get; set; }
        public int BestScore { get; set; }

        public PlayerStatsView ToView() => new(Username, MatchesPlayed, Wins, TotalScore, BestScore);
    }
}

public sealed class MatchEndedConsumer(
    StatsStore store,
    IOptions<RabbitMqOptions> options,
    ILogger<MatchEndedConsumer> logger) : RabbitConsumerBase<MatchEnded>(options, logger)
{
    protected override string QueueName => "stats.match-ended";
    protected override IReadOnlyList<string> RoutingKeys => [Topology.MatchEndedKey];

    protected override Task HandleAsync(MatchEnded message, string routingKey, CancellationToken ct)
    {
        if (!store.Apply(message))
        {
            logger.LogInformation("Dublet ignoreret: {MessageId}", message.MessageId);
        }
        return Task.CompletedTask;
    }
}
