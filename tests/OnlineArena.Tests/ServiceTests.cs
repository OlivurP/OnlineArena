using MatchmakingService.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Contracts;
using StatsService.Domain;

namespace OnlineArena.Tests;

public class StatsStoreTests
{
    [Fact]
    public void Apply_SameMessageTwice_IsCountedOnce()
    {
        var store = new StatsStore();
        var evt = new MatchEnded(Guid.NewGuid(), Guid.NewGuid(), "gs-1",
            [new PlayerResult("alice", 5), new PlayerResult("bob", 3)], DateTime.UtcNow);

        Assert.True(store.Apply(evt));
        Assert.False(store.Apply(evt)); // genlevering fra RabbitMQ

        var alice = store.Get("alice")!;
        Assert.Equal(1, alice.MatchesPlayed);
        Assert.Equal(1, alice.Wins);
        Assert.Equal(0, store.Get("bob")!.Wins);
    }

    [Fact]
    public void Leaderboard_OrdersByWinsThenScore()
    {
        var store = new StatsStore();
        store.Apply(new MatchEnded(Guid.NewGuid(), Guid.NewGuid(), "gs", [new("a", 1), new("b", 9)], DateTime.UtcNow));
        store.Apply(new MatchEnded(Guid.NewGuid(), Guid.NewGuid(), "gs", [new("a", 1), new("c", 2)], DateTime.UtcNow));

        Assert.Equal("b", store.Leaderboard(10)[0].Username);
    }
}

public class MatchmakingQueueTests
{
    private static (MatchmakingQueue Queue, ServerRegistry Registry) Create(int matchSize = 2)
    {
        var options = Options.Create(new MatchmakingOptions { MatchSize = matchSize });
        var registry = new ServerRegistry(TimeProvider.System, options);
        var queue = new MatchmakingQueue(registry, options, TimeProvider.System, NullLogger<MatchmakingQueue>.Instance);
        return (queue, registry);
    }

    private static void AddHealthyServer(ServerRegistry registry, string id, int capacity = 8)
    {
        registry.Register(new RegisterServerRequest(id, "localhost", 7777, "http://x/health/ready", capacity));
        registry.ReportHealth(id, healthy: true);
    }

    [Fact]
    public void TwoPlayers_AndHealthyServer_AreMatchedToSameServer()
    {
        var (queue, registry) = Create();
        AddHealthyServer(registry, "gs-1");

        var t1 = queue.Enqueue("alice");
        var t2 = queue.Enqueue("bob");

        Assert.Equal(TicketStatus.Matched, queue.Get(t1.Id)!.Status);
        Assert.Equal(TicketStatus.Matched, t2.Status);
        Assert.Equal(queue.Get(t1.Id)!.ServerPort, t2.ServerPort);
    }

    [Fact]
    public void RegisteredButNotHealthyServer_IsNotUsed()
    {
        var (queue, registry) = Create();
        registry.Register(new RegisterServerRequest("gs-1", "localhost", 7777, "http://x/health/ready", 8));

        queue.Enqueue("alice");
        var t2 = queue.Enqueue("bob");

        Assert.Equal(TicketStatus.Waiting, t2.Status);
    }

    [Fact]
    public void Enqueue_SamePlayerTwice_ReturnsSameTicket()
    {
        var (queue, _) = Create();
        Assert.Equal(queue.Enqueue("alice").Id, queue.Enqueue("ALICE").Id);
    }

    [Fact]
    public void FullServer_IsSkipped()
    {
        var (queue, registry) = Create();
        AddHealthyServer(registry, "gs-small", capacity: 1);

        queue.Enqueue("alice");
        Assert.Equal(TicketStatus.Waiting, queue.Enqueue("bob").Status);
    }
}
