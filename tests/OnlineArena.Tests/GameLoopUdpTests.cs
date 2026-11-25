using System.Net;
using System.Net.Sockets;
using AuthService.Domain;
using AuthService.Services;
using GameServer;
using GameServer.Game;
using GameServer.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OnlineArena.Tests.Infrastructure;
using Shared.Auth;
using Shared.Contracts;
using Shared.Messaging;
using Shared.Protocol;

namespace OnlineArena.Tests;

/// <summary>
/// Kører det rigtige GameLoop over rigtig UDP på localhost. Tiden styres med FakeTimeProvider,
/// så ticks, match-varighed og timeouts er deterministiske og testene hurtige.
/// </summary>
public sealed class GameLoopHarness : IAsyncDisposable
{
    private readonly GameLoop _loop;

    public FakeTimeProvider Time { get; } = new();
    public ServerState State { get; } = new();
    public EventOutbox Outbox { get; } = new();
    public GameServerOptions Options { get; }
    public TimeSpan TickInterval => TimeSpan.FromSeconds(1.0 / Options.TickRate);

    private GameLoopHarness(GameServerOptions options)
    {
        Options = options;
        _loop = new GameLoop(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(TestJwt.CreateOptions()),
            Outbox, State, Time, NullLogger<GameLoop>.Instance);
    }

    public static async Task<GameLoopHarness> StartAsync(Action<GameServerOptions>? configure = null)
    {
        var options = new GameServerOptions
        {
            ServerId = "test-server",
            UdpPort = GetFreeUdpPort(),
            TickRate = 20,
            ClientTimeoutSeconds = 600, // undgå utilsigtede timeouts når testene spoler tiden frem
            MinPlayersToStart = 99      // ingen match medmindre testen beder om det
        };
        configure?.Invoke(options);

        var harness = new GameLoopHarness(options);
        await harness._loop.StartAsync(CancellationToken.None);
        await Eventually.UntilAsync(() => harness.State.IsReady, TimeSpan.FromSeconds(5), "UDP listener to bind");
        return harness;
    }

    public UdpTestClient CreateClient() => new("127.0.0.1", Options.UdpPort);

    /// <summary>Spoler tiden frem én tick ad gangen, indtil klienten modtager en matchende besked.</summary>
    public async Task<T> PumpUntilReceivedAsync<T>(
        UdpTestClient client, Func<T, bool>? predicate = null, TimeSpan? step = null, TimeSpan? timeout = null)
        where T : NetMessage
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            Time.Advance(step ?? TickInterval);
            if (await client.TryReceiveAsync(TimeSpan.FromMilliseconds(25), predicate) is { } message) return message;
        }
        throw new TimeoutException($"No matching {typeof(T).Name} received.");
    }

    public async Task PumpUntilAsync(Func<bool> condition, string description, TimeSpan? step = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Timed out waiting for {description}.");
            Time.Advance(step ?? TickInterval);
            await Task.Delay(10);
        }
    }

    public async Task<WelcomeMessage> JoinAsync(UdpTestClient client, string token)
    {
        await client.SendAsync(new JoinMessage(token));
        return await PumpUntilReceivedAsync<WelcomeMessage>(client);
    }

    private static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _loop.StopAsync(CancellationToken.None);
        _loop.Dispose();
    }
}

public class GameLoopUdpTests
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Join_WithValidToken_ReceivesWelcomeAndSnapshots()
    {
        await using var server = await GameLoopHarness.StartAsync();
        await using var client = server.CreateClient();
        var userId = Guid.NewGuid();

        var welcome = await server.JoinAsync(client, TestJwt.CreateAccessToken("alice", userId: userId));

        Assert.Equal(userId.ToString(), welcome.PlayerId);
        Assert.Equal(server.Options.TickRate, welcome.TickRate);

        var snapshot = await server.PumpUntilReceivedAsync<SnapshotMessage>(client);
        Assert.Equal(nameof(MatchPhase.WaitingForPlayers), snapshot.Phase);
        Assert.Contains(snapshot.Players, p => p.Id == welcome.PlayerId && p.Name == "alice");
        Assert.Equal(1, server.State.CurrentPlayers);
    }

    [Fact]
    public async Task Join_WithTokenSignedByAnotherSecret_IsRejected()
    {
        await using var server = await GameLoopHarness.StartAsync();
        await using var client = server.CreateClient();
        var forged = new TokenService(Options.Create(TestJwt.CreateOptions("attacker-secret-0123456789abcdef-0123456789")))
            .CreateAccessToken(new User { Id = Guid.NewGuid(), Username = "mallory", PasswordHash = "-", Role = Roles.Admin })
            .Token;

        await client.SendAsync(new JoinMessage(forged));
        var rejected = await server.PumpUntilReceivedAsync<RejectedMessage>(client);

        Assert.Equal("Ugyldigt eller udløbet token", rejected.Reason);
        Assert.Equal(0, server.State.CurrentPlayers);
    }

    [Fact]
    public async Task Join_WithExpiredToken_IsRejected()
    {
        await using var server = await GameLoopHarness.StartAsync();
        await using var client = server.CreateClient();

        await client.SendAsync(new JoinMessage(TestJwt.CreateExpiredAccessToken("alice")));
        var rejected = await server.PumpUntilReceivedAsync<RejectedMessage>(client);

        Assert.Equal("Ugyldigt eller udløbet token", rejected.Reason);
    }

    [Fact]
    public async Task Join_WithGarbageToken_IsRejected()
    {
        await using var server = await GameLoopHarness.StartAsync();
        await using var client = server.CreateClient();

        await client.SendAsync(new JoinMessage("not-a-jwt"));

        await server.PumpUntilReceivedAsync<RejectedMessage>(client);
    }

    [Fact]
    public async Task DuplicateJoin_FromSameEndpoint_ReturnsSamePlayerWithoutNewSession()
    {
        await using var server = await GameLoopHarness.StartAsync();
        await using var client = server.CreateClient();
        var token = TestJwt.CreateAccessToken("alice");

        var first = await server.JoinAsync(client, token);
        var second = await server.JoinAsync(client, token);

        Assert.Equal(first.PlayerId, second.PlayerId);
        await server.PumpUntilAsync(() => server.State.CurrentPlayers == 1, "one player");
    }

    [Fact]
    public async Task Join_WhenServerIsFull_IsRejected()
    {
        await using var server = await GameLoopHarness.StartAsync(o => o.Capacity = 1);
        await using var alice = server.CreateClient();
        await using var bob = server.CreateClient();

        await server.JoinAsync(alice, TestJwt.CreateAccessToken("alice"));
        await bob.SendAsync(new JoinMessage(TestJwt.CreateAccessToken("bob")));
        var rejected = await server.PumpUntilReceivedAsync<RejectedMessage>(bob);

        Assert.Equal("Serveren er fuld", rejected.Reason);
        Assert.Equal(1, server.State.CurrentPlayers);
    }

    [Fact]
    public async Task Reconnect_FromNewEndpoint_ReplacesOldSessionEvenWhenFull()
    {
        await using var server = await GameLoopHarness.StartAsync(o => o.Capacity = 1);
        await using var oldConnection = server.CreateClient();
        await using var newConnection = server.CreateClient();
        await using var stranger = server.CreateClient();
        var userId = Guid.NewGuid();

        await server.JoinAsync(oldConnection, TestJwt.CreateAccessToken("alice", userId: userId));
        var welcome = await server.JoinAsync(newConnection, TestJwt.CreateAccessToken("alice", userId: userId));

        Assert.Equal(userId.ToString(), welcome.PlayerId);
        await server.PumpUntilAsync(() => server.State.CurrentPlayers == 1, "the old session to be replaced");

        // Pladsen er stadig optaget af den genforbundne spiller
        await stranger.SendAsync(new JoinMessage(TestJwt.CreateAccessToken("bob")));
        Assert.Equal("Serveren er fuld", (await server.PumpUntilReceivedAsync<RejectedMessage>(stranger)).Reason);
    }

    [Fact]
    public async Task Inputs_AreLimitedToMaxInputsPerTick()
    {
        await using var server = await GameLoopHarness.StartAsync(o => o.MaxInputsPerTick = 4);
        await using var client = server.CreateClient();
        await server.JoinAsync(client, TestJwt.CreateAccessToken("speedhacker"));
        await Task.Delay(200); // lad game loop'et blive færdigt med evt. ventende ticks

        for (var sequence = 1; sequence <= 10; sequence++)
        {
            await client.SendAsync(new InputMessage(sequence, 1, 0));
        }
        await Task.Delay(300); // UDP på localhost: giv receive-loop'et tid til at lægge alle inputs i køen

        server.Time.Advance(server.TickInterval);
        var afterFirstTick = await client.ReceiveAsync<SnapshotMessage>(ShortWait, s => s.LastProcessedInput > 0);
        Assert.Equal(4, afterFirstTick.LastProcessedInput);

        server.Time.Advance(server.TickInterval);
        var afterSecondTick = await client.ReceiveAsync<SnapshotMessage>(ShortWait, s => s.LastProcessedInput > 4);
        Assert.Equal(8, afterSecondTick.LastProcessedInput);

        server.Time.Advance(server.TickInterval);
        var afterThirdTick = await client.ReceiveAsync<SnapshotMessage>(ShortWait, s => s.LastProcessedInput > 8);
        Assert.Equal(10, afterThirdTick.LastProcessedInput);
    }

    [Fact]
    public async Task Inputs_FromUnknownEndpoint_AreIgnored()
    {
        await using var server = await GameLoopHarness.StartAsync();
        await using var player = server.CreateClient();
        await using var intruder = server.CreateClient();
        await server.JoinAsync(player, TestJwt.CreateAccessToken("alice"));

        await intruder.SendAsync(new InputMessage(1, 1, 0));
        var snapshot = await server.PumpUntilReceivedAsync<SnapshotMessage>(player);

        Assert.Single(snapshot.Players);
        Assert.Null(await intruder.TryReceiveAsync<NetMessage>(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task SilentClient_IsDisconnectedAfterClientTimeout()
    {
        await using var server = await GameLoopHarness.StartAsync(o => o.ClientTimeoutSeconds = 2);
        await using var client = server.CreateClient();
        await server.JoinAsync(client, TestJwt.CreateAccessToken("alice"));
        await server.PumpUntilAsync(() => server.State.CurrentPlayers == 1, "player to be counted");

        await server.PumpUntilAsync(() => server.State.CurrentPlayers == 0, "client timeout",
            step: TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task MalformedAndOversizedPackets_DoNotBreakTheServer()
    {
        await using var server = await GameLoopHarness.StartAsync();
        await using var client = server.CreateClient();

        await client.SendRawAsync("this is not json"u8.ToArray());
        await client.SendRawAsync("{\"type\":\"unknown\"}"u8.ToArray());
        var oversized = NetCodec.Encode(new JoinMessage(new string('x', NetCodec.MaxPacketBytes)));
        Assert.True(oversized.Length > NetCodec.MaxPacketBytes);
        await client.SendRawAsync(oversized);

        var welcome = await server.JoinAsync(client, TestJwt.CreateAccessToken("alice"));
        Assert.False(string.IsNullOrEmpty(welcome.PlayerId));
    }

    [Fact]
    public async Task FullMatchLifecycle_PublishesEventsAndNotifiesPlayers()
    {
        await using var server = await GameLoopHarness.StartAsync(o =>
        {
            o.MinPlayersToStart = 2;
            o.MatchDurationSeconds = 3;
            o.IntermissionSeconds = 1;
        });
        await using var alice = server.CreateClient();
        await using var bob = server.CreateClient();

        await server.JoinAsync(alice, TestJwt.CreateAccessToken("alice"));
        await server.JoinAsync(bob, TestJwt.CreateAccessToken("bob"));
        await server.PumpUntilReceivedAsync<SnapshotMessage>(alice, s => s.Phase == nameof(MatchPhase.Running));

        Assert.True(server.Outbox.Reader.TryRead(out var startedItem));
        Assert.Equal(Topology.MatchStartedKey, startedItem.RoutingKey);
        var started = Assert.IsType<MatchStarted>(startedItem.Payload);
        Assert.Equal(started.MessageId, startedItem.MessageId);
        Assert.Equal("test-server", started.ServerId);
        Assert.Equal(["alice", "bob"], started.Players.Order());

        var aliceResult = await server.PumpUntilReceivedAsync<MatchEndedMessage>(alice, step: TimeSpan.FromMilliseconds(250));
        var bobResult = await bob.ReceiveAsync<MatchEndedMessage>(ShortWait);
        Assert.Equal(aliceResult.Results, bobResult.Results);
        Assert.Equal(["alice", "bob"], aliceResult.Results.Select(r => r.Username).Order());

        Assert.True(server.Outbox.Reader.TryRead(out var endedItem));
        Assert.Equal(Topology.MatchEndedKey, endedItem.RoutingKey);
        var ended = Assert.IsType<MatchEnded>(endedItem.Payload);
        Assert.Equal(started.MatchId, ended.MatchId);
        Assert.Equal(aliceResult.Results, ended.Results);

        // Efter pausen starter en ny match med et nyt MatchId
        await server.PumpUntilReceivedAsync<SnapshotMessage>(alice, s => s.Phase == nameof(MatchPhase.Intermission));
        await server.PumpUntilReceivedAsync<SnapshotMessage>(alice, s => s.Phase == nameof(MatchPhase.Running),
            step: TimeSpan.FromMilliseconds(250));
        Assert.True(server.Outbox.Reader.TryRead(out var nextMatch));
        Assert.NotEqual(started.MatchId, Assert.IsType<MatchStarted>(nextMatch.Payload).MatchId);
    }
}
