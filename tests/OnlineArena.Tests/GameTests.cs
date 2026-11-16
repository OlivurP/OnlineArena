using GameClient;
using GameServer.Game;
using Shared.Protocol;

namespace OnlineArena.Tests;

public class GameRulesTests
{
    [Fact]
    public void Move_ClampsToWorldBounds()
    {
        var (x, y) = GameRules.Move(0, 0, -1, -1);
        Assert.Equal(0f, x);
        Assert.Equal(0f, y);
    }

    [Fact]
    public void Move_DiagonalIsNotFasterThanStraight()
    {
        var (x, y) = GameRules.Move(10, 10, 1, 1);
        var distance = MathF.Sqrt((x - 10) * (x - 10) + (y - 10) * (y - 10));
        Assert.Equal(GameRules.SpeedPerInput, distance, precision: 4);
    }

    [Theory]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 0)]
    [InlineData(50f, 0)] // "speed hack": for stor værdi clampes
    public void Move_RejectsOrClampsMaliciousInput(float dx, float dy)
    {
        var (x, _) = GameRules.Move(10, 10, dx, dy);
        Assert.True(float.IsFinite(x));
        Assert.True(x - 10 <= GameRules.SpeedPerInput);
    }
}

public class GameWorldTests
{
    [Fact]
    public void ApplyInput_IgnoresOldAndDuplicateSequenceNumbers()
    {
        var world = new GameWorld(coinCount: 0, seed: 1);
        world.AddPlayer("p1", "alice", 10, 10);

        Assert.True(world.ApplyInput("p1", 2, 1, 0));
        Assert.False(world.ApplyInput("p1", 2, 1, 0)); // dublet
        Assert.False(world.ApplyInput("p1", 1, 1, 0)); // gammel pakke ankommet for sent

        var player = world.TryGetPlayer("p1")!;
        Assert.Equal(10 + GameRules.SpeedPerInput, player.X);
        Assert.Equal(2, player.LastProcessedInput);
    }

    [Fact]
    public void ApplyInput_CollectingCoinIncreasesScoreAndRespawnsCoin()
    {
        var world = new GameWorld(coinCount: 0, seed: 1);
        world.AddPlayer("p1", "alice", 10, 10);
        world.AddCoinAt(10.5f, 10);

        world.ApplyInput("p1", 1, 1, 0);

        Assert.Equal(1, world.TryGetPlayer("p1")!.Score);
        Assert.Single(world.CoinStates); // ny mønt er spawnet
    }

    [Fact]
    public void ApplyInput_UnknownPlayer_ReturnsFalse()
    {
        var world = new GameWorld(coinCount: 0);
        Assert.False(world.ApplyInput("ghost", 1, 1, 0));
    }
}

public class ClientPredictionTests
{
    [Fact]
    public void Reconcile_ReplaysInputsNotYetProcessedByServer()
    {
        var me = new PredictedPlayer();
        me.Reconcile(10, 10, 0);

        me.ApplyLocal(1, 1, 0);
        me.ApplyLocal(2, 1, 0);
        me.ApplyLocal(3, 1, 0);

        // Serveren har kun behandlet input #1
        me.Reconcile(10 + GameRules.SpeedPerInput, 10, lastProcessedInput: 1);

        Assert.Equal(10 + 3 * GameRules.SpeedPerInput, me.X, precision: 4);
        Assert.Equal(2, me.PendingCount);
    }

    [Fact]
    public void Reconcile_ServerCorrectionWins()
    {
        var me = new PredictedPlayer();
        me.ApplyLocal(1, 1, 0);

        // Serveren afviste bevægelsen (fx pga. input-rate-limit) – autoritativ position vinder
        me.Reconcile(0, 0, lastProcessedInput: 1);

        Assert.Equal(0f, me.X);
        Assert.Equal(0, me.PendingCount);
    }
}

public class NetCodecTests
{
    [Fact]
    public void RoundTrip_PreservesMessageType()
    {
        var bytes = NetCodec.Encode(new InputMessage(7, 1, -1));
        var decoded = Assert.IsType<InputMessage>(NetCodec.Decode(bytes));
        Assert.Equal(7, decoded.Sequence);
    }

    [Fact]
    public void Decode_GarbageReturnsNull()
    {
        Assert.Null(NetCodec.Decode("{not json"u8));
        Assert.Null(NetCodec.Decode("""{"type":"hack"}"""u8));
    }
}
