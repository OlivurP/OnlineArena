namespace MatchmakingService.Domain;

public sealed record MatchSummary(Guid MatchId, string ServerId, string? Winner, int Players, DateTime EndedAtUtc);

/// <summary>Seneste afsluttede matches – bygget ud fra MatchEnded-events (eventual consistency).</summary>
public sealed class MatchHistory
{
    private const int MaxEntries = 50;
    private readonly object _lock = new();
    private readonly LinkedList<MatchSummary> _matches = new();

    /// <returns>false hvis matchet allerede er registreret (idempotent ved genlevering).</returns>
    public bool TryAdd(MatchSummary summary)
    {
        lock (_lock)
        {
            if (_matches.Any(m => m.MatchId == summary.MatchId)) return false;
            _matches.AddFirst(summary);
            if (_matches.Count > MaxEntries) _matches.RemoveLast();
            return true;
        }
    }

    public IReadOnlyList<MatchSummary> Recent()
    {
        lock (_lock) return _matches.ToList();
    }
}
