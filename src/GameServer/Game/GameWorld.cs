using Shared.Protocol;

namespace GameServer.Game;

public sealed class PlayerEntity(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public float X { get; set; }
    public float Y { get; set; }
    public int Score { get; set; }

    /// <summary>Sekvensnummer for seneste anvendte input – sendes tilbage til klienten (reconciliation).</summary>
    public int LastProcessedInput { get; set; }
}

/// <summary>
/// Den autoritative spilsimulation. Ren logik uden netværk/IO, så den kan unit-testes.
/// Spillet: saml mønter ($) i en 2D-arena. Flest mønter når tiden løber ud vinder.
/// </summary>
public sealed class GameWorld
{
    private readonly Dictionary<string, PlayerEntity> _players = new();
    private readonly List<(int Id, float X, float Y)> _coins = [];
    private readonly Random _random;
    private int _nextCoinId;

    public GameWorld(int coinCount = 6, int? seed = null)
    {
        _random = seed is null ? new Random() : new Random(seed.Value);
        for (var i = 0; i < coinCount; i++) SpawnCoin();
    }

    public IReadOnlyCollection<PlayerEntity> Players => _players.Values;

    public IReadOnlyList<CoinState> CoinStates => _coins.Select(c => new CoinState(c.Id, c.X, c.Y)).ToList();

    public PlayerEntity? TryGetPlayer(string id) => _players.GetValueOrDefault(id);

    public PlayerEntity AddPlayer(string id, string name, float? x = null, float? y = null)
    {
        if (_players.TryGetValue(id, out var existing)) return existing;

        var player = new PlayerEntity(id, name)
        {
            X = x ?? RandomX(),
            Y = y ?? RandomY()
        };
        _players[id] = player;
        return player;
    }

    public bool RemovePlayer(string id) => _players.Remove(id);

    public void AddCoinAt(float x, float y) => _coins.Add((_nextCoinId++, x, y));

    /// <summary>
    /// Anvender ét input. Gamle/dublerede sekvensnumre afvises (UDP kan levere i forkert rækkefølge eller dobbelt).
    /// </summary>
    public bool ApplyInput(string playerId, int sequence, float dx, float dy)
    {
        if (!_players.TryGetValue(playerId, out var player)) return false;
        if (sequence <= player.LastProcessedInput) return false;

        (player.X, player.Y) = GameRules.Move(player.X, player.Y, dx, dy);
        player.LastProcessedInput = sequence;
        CollectCoins(player);
        return true;
    }

    public void ResetScores()
    {
        foreach (var player in _players.Values) player.Score = 0;
    }

    private void CollectCoins(PlayerEntity player)
    {
        const float radiusSquared = GameRules.PickupRadius * GameRules.PickupRadius;
        for (var i = _coins.Count - 1; i >= 0; i--)
        {
            var (_, cx, cy) = _coins[i];
            var dx = cx - player.X;
            var dy = cy - player.Y;
            if (dx * dx + dy * dy > radiusSquared) continue;

            player.Score++;
            _coins.RemoveAt(i);
            SpawnCoin();
        }
    }

    private void SpawnCoin() => AddCoinAt(RandomX(), RandomY());
    private float RandomX() => (float)_random.NextDouble() * GameRules.WorldWidth;
    private float RandomY() => (float)_random.NextDouble() * GameRules.WorldHeight;
}
