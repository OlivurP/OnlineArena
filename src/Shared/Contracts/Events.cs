namespace Shared.Contracts;

// Integration events publiceres på topic exchange "game.events".
// MessageId bruges af consumers til idempotens (dubletter ved genlevering).

public sealed record PlayerResult(string Username, int Score);

public sealed record MatchStarted(
    Guid MessageId,
    Guid MatchId,
    string ServerId,
    IReadOnlyList<string> Players,
    DateTime OccurredAtUtc);

public sealed record MatchEnded(
    Guid MessageId,
    Guid MatchId,
    string ServerId,
    IReadOnlyList<PlayerResult> Results,
    DateTime OccurredAtUtc);
