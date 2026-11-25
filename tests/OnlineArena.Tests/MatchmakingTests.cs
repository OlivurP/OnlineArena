using MatchmakingService.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shared.Contracts;

namespace OnlineArena.Tests;

public class ServerRegistryTests
{
    // Defaults: heartbeat timeout 15s, stale 60s, reservation 10s
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly ServerRegistry _registry;

    public ServerRegistryTests() => _registry = new ServerRegistry(_time, Options.Create(new MatchmakingOptions()));

    private void AddHealthyServer(string id, int capacity = 8)
    {
        _registry.Register(new RegisterServerRequest(id, "localhost", 7777, "http://x/health/ready", capacity));
        _registry.ReportHealth(id, healthy: true);
    }

    [Fact]
    public void RemoveStale_RemovesServerWithoutHeartbeat()
    {
        AddHealthyServer("gs-1");

        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(1, _registry.RemoveStale());
        Assert.Empty(_registry.GetAll());
    }

    [Fact]
    public void RemoveStale_KeepsServerThatSendsHeartbeats()
    {
        AddHealthyServer("gs-1");

        _time.Advance(TimeSpan.FromSeconds(40));
        _registry.Heartbeat("gs-1", currentPlayers: 0);
        _time.Advance(TimeSpan.FromSeconds(40));

        Assert.Equal(0, _registry.RemoveStale());
        Assert.Single(_registry.GetAll());
    }

    [Fact]
    public void TryReserve_SkipsServerWithMissedHeartbeat_UntilNextHeartbeat()
    {
        AddHealthyServer("gs-1");

        _time.Advance(TimeSpan.FromSeconds(16));
        Assert.Null(_registry.TryReserve(1));

        _registry.Heartbeat("gs-1", currentPlayers: 0);
        Assert.NotNull(_registry.TryReserve(1));
    }

    [Fact]
    public void ReportHealth_RequiresThreeConsecutiveFailuresToMarkUnhealthy()
    {
        AddHealthyServer("gs-1");

        _registry.ReportHealth("gs-1", healthy: false);
        _registry.ReportHealth("gs-1", healthy: false);
        Assert.True(_registry.GetAll()[0].IsHealthy);

        _registry.ReportHealth("gs-1", healthy: false);
        Assert.False(_registry.GetAll()[0].IsHealthy);
    }

    [Fact]
    public void ReportHealth_SuccessResetsFailureCount()
    {
        AddHealthyServer("gs-1");

        _registry.ReportHealth("gs-1", healthy: false);
        _registry.ReportHealth("gs-1", healthy: false);
        _registry.ReportHealth("gs-1", healthy: true);
        _registry.ReportHealth("gs-1", healthy: false);

        Assert.True(_registry.GetAll()[0].IsHealthy);
    }

    [Fact]
    public void Reservations_BlockCapacity_UntilTheyExpire()
    {
        AddHealthyServer("gs-1", capacity: 2);

        Assert.NotNull(_registry.TryReserve(2));
        Assert.Null(_registry.TryReserve(2));

        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.NotNull(_registry.TryReserve(2));
    }

    [Fact]
    public void TryReserve_PicksServerWithMostFreeCapacity()
    {
        AddHealthyServer("gs-small", capacity: 4);
        AddHealthyServer("gs-big", capacity: 8);

        Assert.Equal("gs-big", _registry.TryReserve(1)!.ServerId);

        _registry.Heartbeat("gs-big", currentPlayers: 7);
        Assert.Equal("gs-small", _registry.TryReserve(1)!.ServerId);
    }

    [Fact]
    public void Heartbeat_UnknownServer_ReturnsFalse()
    {
        Assert.False(_registry.Heartbeat("unknown", 0));
    }
}

public class MatchmakingQueueTimeTests
{
    // Defaults: match size 2, ticket max age 5 min
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly ServerRegistry _registry;
    private readonly MatchmakingQueue _queue;

    public MatchmakingQueueTimeTests()
    {
        var options = Options.Create(new MatchmakingOptions());
        _registry = new ServerRegistry(_time, options);
        _queue = new MatchmakingQueue(_registry, options, _time, NullLogger<MatchmakingQueue>.Instance);
    }

    private void AddHealthyServer(string id = "gs-1")
    {
        _registry.Register(new RegisterServerRequest(id, "localhost", 7777, "http://x/health/ready", 8));
        _registry.ReportHealth(id, healthy: true);
    }

    [Fact]
    public void RemoveExpired_ExpiresWaitingTicketAfterMaxAge()
    {
        var ticket = _queue.Enqueue("alice");

        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        _queue.RemoveExpired();

        Assert.Equal(TicketStatus.Expired, _queue.Get(ticket.Id)!.Status);
        Assert.NotEqual(ticket.Id, _queue.Enqueue("alice").Id);
    }

    [Fact]
    public void RemoveExpired_KeepsTicketsYoungerThanMaxAge()
    {
        var ticket = _queue.Enqueue("alice");

        _time.Advance(TimeSpan.FromMinutes(4));
        _queue.RemoveExpired();

        Assert.Equal(TicketStatus.Waiting, _queue.Get(ticket.Id)!.Status);
    }

    [Fact]
    public void ExpiredTicket_IsNotMatched()
    {
        _queue.Enqueue("alice");
        _time.Advance(TimeSpan.FromMinutes(6));
        _queue.RemoveExpired();

        AddHealthyServer();
        var bob = _queue.Enqueue("bob");

        Assert.Equal(0, _queue.TryFormMatches());
        Assert.Equal(TicketStatus.Waiting, _queue.Get(bob.Id)!.Status);
    }

    [Fact]
    public void RemoveExpired_ForgetsOldFinishedTickets()
    {
        AddHealthyServer();
        var alice = _queue.Enqueue("alice");
        _queue.Enqueue("bob");

        _time.Advance(TimeSpan.FromMinutes(6));
        _queue.RemoveExpired();

        Assert.Null(_queue.Get(alice.Id));
    }

    [Fact]
    public void TryFormMatches_MatchesWaitingPlayers_WhenServerBecomesHealthy()
    {
        var alice = _queue.Enqueue("alice");
        var bob = _queue.Enqueue("bob");
        Assert.Equal(TicketStatus.Waiting, bob.Status);

        AddHealthyServer();

        Assert.Equal(1, _queue.TryFormMatches());
        Assert.Equal(TicketStatus.Matched, _queue.Get(alice.Id)!.Status);
        Assert.Equal(7777, _queue.Get(bob.Id)!.ServerPort);
    }

    [Fact]
    public void Cancel_ByAnotherPlayer_IsRejected()
    {
        var ticket = _queue.Enqueue("alice");

        Assert.False(_queue.Cancel(ticket.Id, "mallory"));
        Assert.Equal(TicketStatus.Waiting, _queue.Get(ticket.Id)!.Status);
    }

    [Fact]
    public void CancelledPlayer_IsNotMatched()
    {
        var alice = _queue.Enqueue("alice");
        Assert.True(_queue.Cancel(alice.Id, "ALICE"));

        AddHealthyServer();
        var bob = _queue.Enqueue("bob");
        var carol = _queue.Enqueue("carol");

        Assert.Equal(TicketStatus.Cancelled, _queue.Get(alice.Id)!.Status);
        Assert.Equal(TicketStatus.Matched, _queue.Get(bob.Id)!.Status);
        Assert.Equal(TicketStatus.Matched, carol.Status);
    }

    [Fact]
    public void Cancel_AlreadyMatchedTicket_ReturnsFalse()
    {
        AddHealthyServer();
        var alice = _queue.Enqueue("alice");
        _queue.Enqueue("bob");

        Assert.False(_queue.Cancel(alice.Id, "alice"));
    }
}

public class MatchHistoryTests
{
    private static MatchSummary Summary(DateTime? endedAt = null) =>
        new(Guid.NewGuid(), "gs-1", "alice", 2, endedAt ?? DateTime.UtcNow);

    [Fact]
    public void TryAdd_SameMatchTwice_IsStoredOnce()
    {
        var history = new MatchHistory();
        var match = Summary();

        Assert.True(history.TryAdd(match));
        Assert.False(history.TryAdd(match with { Winner = "bob" }));
        Assert.Equal("alice", Assert.Single(history.Recent()).Winner);
    }

    [Fact]
    public void Recent_ReturnsNewestFirst()
    {
        var history = new MatchHistory();
        var first = Summary();
        var second = Summary();

        history.TryAdd(first);
        history.TryAdd(second);

        Assert.Equal([second.MatchId, first.MatchId], history.Recent().Select(m => m.MatchId));
    }

    [Fact]
    public void Recent_KeepsOnlyLatest50Matches()
    {
        var history = new MatchHistory();
        var oldest = Summary();
        history.TryAdd(oldest);
        for (var i = 0; i < 50; i++) history.TryAdd(Summary());

        var recent = history.Recent();

        Assert.Equal(50, recent.Count);
        Assert.DoesNotContain(recent, m => m.MatchId == oldest.MatchId);
    }
}
