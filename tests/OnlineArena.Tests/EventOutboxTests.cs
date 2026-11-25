using GameServer.Networking;
using Shared.Contracts;

namespace OnlineArena.Tests;

public class EventOutboxTests
{
    [Fact]
    public void Enqueue_MatchEnded_KeepsEventMessageIdForIdempotentConsumers()
    {
        var outbox = new EventOutbox();
        var evt = new MatchEnded(Guid.NewGuid(), Guid.NewGuid(), "gs-1", [new PlayerResult("alice", 3)], DateTime.UtcNow);

        outbox.Enqueue("match.ended", evt);

        Assert.True(outbox.Reader.TryRead(out var item));
        Assert.Equal(evt.MessageId, item.MessageId);
        Assert.Equal("match.ended", item.RoutingKey);
        Assert.Same(evt, item.Payload);
    }

    [Fact]
    public void Enqueue_MatchStarted_KeepsEventMessageId()
    {
        var outbox = new EventOutbox();
        var evt = new MatchStarted(Guid.NewGuid(), Guid.NewGuid(), "gs-1", ["alice", "bob"], DateTime.UtcNow);

        outbox.Enqueue("match.started", evt);

        Assert.True(outbox.Reader.TryRead(out var item));
        Assert.Equal(evt.MessageId, item.MessageId);
    }

    [Fact]
    public void Enqueue_UnknownPayload_GetsNewMessageId()
    {
        var outbox = new EventOutbox();

        outbox.Enqueue("other", new { Value = 1 });

        Assert.True(outbox.Reader.TryRead(out var item));
        Assert.NotEqual(Guid.Empty, item.MessageId);
    }

    [Fact]
    public void Enqueue_PreservesOrder()
    {
        var outbox = new EventOutbox();

        outbox.Enqueue("a", new object());
        outbox.Enqueue("b", new object());

        Assert.True(outbox.Reader.TryRead(out var first));
        Assert.True(outbox.Reader.TryRead(out var second));
        Assert.Equal(("a", "b"), (first.RoutingKey, second.RoutingKey));
    }
}
