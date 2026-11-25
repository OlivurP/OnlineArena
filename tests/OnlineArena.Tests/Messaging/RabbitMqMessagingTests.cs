using System.Text;
using System.Text.Json;
using GameServer.Networking;
using MatchmakingService.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnlineArena.Tests.Infrastructure;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Shared;
using Shared.Contracts;
using Shared.Messaging;
using StatsService.Domain;
using Testcontainers.RabbitMq;
using HistoryConsumer = MatchmakingService.Workers.MatchEndedConsumer;
using StatsConsumer = StatsService.Domain.MatchEndedConsumer;

namespace OnlineArena.Tests.Messaging;

/// <summary>Én RabbitMQ-container (Testcontainers) delt af alle tests i klassen. Startes kun når Docker-tests skal køre.</summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    public const string Image = "rabbitmq:4-management";
    public const string UserName = "arena";
    public const string Password = "arena-test";

    private RabbitMqContainer? _container;
    private IConnection? _admin;

    public RabbitMqOptions Options => new()
    {
        HostName = _container!.Hostname,
        Port = _container.GetMappedPublicPort(5672),
        UserName = UserName,
        Password = Password
    };

    public async Task InitializeAsync()
    {
        if (!DockerFactAttribute.ShouldRun) return;

        _container = new RabbitMqBuilder(Image).WithUsername(UserName).WithPassword(Password).Build();
        await _container.StartAsync();
        _admin = await CreateConnectionFactory(Options).CreateConnectionAsync();
    }

    public static ConnectionFactory CreateConnectionFactory(RabbitMqOptions o) =>
        new() { HostName = o.HostName, Port = o.Port, UserName = o.UserName, Password = o.Password };

    /// <summary>Giver hver test rene køer, selv om containeren deles.</summary>
    public async Task DeleteQueuesAsync(params string[] queues)
    {
        await using var channel = await _admin!.CreateChannelAsync();
        foreach (var queue in queues) await channel.QueueDeleteAsync(queue);
    }

    public async Task DeclareTestQueueAsync(string queue, string routingKey)
    {
        await using var channel = await _admin!.CreateChannelAsync();
        await RabbitConnector.DeclareTopologyAsync(channel, CancellationToken.None);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(queue, Topology.EventsExchange, routingKey);
    }

    public async Task PublishRawAsync(string routingKey, byte[] body)
    {
        await using var channel = await _admin!.CreateChannelAsync();
        await channel.BasicPublishAsync(Topology.EventsExchange, routingKey, body);
    }

    public async Task<BasicGetResult> GetMessageAsync(string queue, TimeSpan timeout)
    {
        await using var channel = await _admin!.CreateChannelAsync();
        BasicGetResult? result = null;
        await Eventually.UntilAsync(async () => (result = await channel.BasicGetAsync(queue, autoAck: true)) is not null,
            timeout, $"a message in '{queue}'");
        return result!;
    }

    public async Task<uint> MessageCountAsync(string queue)
    {
        await using var channel = await _admin!.CreateChannelAsync();
        return await channel.MessageCountAsync(queue);
    }

    /// <summary>IsReady bliver sand når kanalen er åben – før køen er bundet. Vent derfor på at consumeren er registreret.</summary>
    public async Task WaitForConsumerAsync(string queue)
    {
        await Eventually.UntilAsync(async () =>
        {
            try
            {
                await using var channel = await _admin!.CreateChannelAsync();
                return await channel.ConsumerCountAsync(queue) > 0;
            }
            catch (OperationInterruptedException)
            {
                return false; // køen findes ikke endnu (passiv declare fejler og lukker kanalen)
            }
        }, TimeSpan.FromSeconds(30), $"a consumer on '{queue}'");
    }

    public async Task DisposeAsync()
    {
        if (_admin is not null) await _admin.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}

[Trait("Category", TestCategories.Integration)]
public sealed class RabbitMqMessagingTests(RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private const string StatsQueue = "stats.match-ended";
    private const string HistoryQueue = "matchmaking.match-ended";
    private const string FailingQueue = "test.failing";
    private const string RelayQueue = "test.relay";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly List<Microsoft.Extensions.Hosting.BackgroundService> _consumers = [];
    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync()
    {
        if (!DockerFactAttribute.ShouldRun) return;
        string[] queues = [StatsQueue, HistoryQueue, FailingQueue, RelayQueue];
        await rabbit.DeleteQueuesAsync([.. queues, .. queues.Select(q => q + ".dlq")]);
    }

    [DockerFact]
    public async Task MatchEnded_IsDeliveredToEverySubscribingService()
    {
        var stats = new StatsStore();
        var history = new MatchHistory();
        await StartConsumerAsync(new StatsConsumer(stats, Options.Create(rabbit.Options), NullLogger<StatsConsumer>.Instance), StatsQueue);
        await StartConsumerAsync(new HistoryConsumer(history, Options.Create(rabbit.Options), NullLogger<HistoryConsumer>.Instance), HistoryQueue);
        var publisher = await StartPublisherAsync();
        var match = NewMatchEnded(("alice", 5), ("bob", 3));

        await publisher.PublishAsync(Topology.MatchEndedKey, match.MessageId, match, CancellationToken.None);

        await Eventually.UntilAsync(() => stats.Get("alice") is not null && history.Recent().Count == 1, Timeout,
            "both services to process the event");
        Assert.Equal(new PlayerStatsView("alice", 1, 1, 5, 5), stats.Get("alice"));
        Assert.Equal(new PlayerStatsView("bob", 1, 0, 3, 3), stats.Get("bob"));
        var summary = Assert.Single(history.Recent());
        Assert.Equal(match.MatchId, summary.MatchId);
        Assert.Equal("alice", summary.Winner);
        Assert.Equal(2, summary.Players);
    }

    [DockerFact]
    public async Task RedeliveredEvent_WithSameMessageId_IsCountedOnce()
    {
        var stats = new StatsStore();
        await StartConsumerAsync(new StatsConsumer(stats, Options.Create(rabbit.Options), NullLogger<StatsConsumer>.Instance), StatsQueue);
        var publisher = await StartPublisherAsync();
        var match = NewMatchEnded(("alice", 5));
        var marker = NewMatchEnded(("marker", 1));

        await publisher.PublishAsync(Topology.MatchEndedKey, match.MessageId, match, CancellationToken.None);
        await publisher.PublishAsync(Topology.MatchEndedKey, match.MessageId, match, CancellationToken.None);
        await publisher.PublishAsync(Topology.MatchEndedKey, marker.MessageId, marker, CancellationToken.None);

        // Beskeder behandles i rækkefølge, så når markøren er behandlet, er dubletten det også
        await Eventually.UntilAsync(() => stats.Get("marker") is not null, Timeout, "the marker event");
        Assert.Equal(1, stats.Get("alice")!.MatchesPlayed);
        Assert.Equal(0u, await rabbit.MessageCountAsync(StatsQueue + ".dlq"));
    }

    [DockerFact]
    public async Task InvalidMessages_AreDeadLetteredWithoutBlockingTheQueue()
    {
        var stats = new StatsStore();
        await StartConsumerAsync(new StatsConsumer(stats, Options.Create(rabbit.Options), NullLogger<StatsConsumer>.Instance), StatsQueue);
        var valid = NewMatchEnded(("alice", 5));

        await rabbit.PublishRawAsync(Topology.MatchEndedKey, "this is not json"u8.ToArray());
        await rabbit.PublishRawAsync(Topology.MatchEndedKey, "null"u8.ToArray());
        await rabbit.PublishRawAsync(Topology.MatchEndedKey, JsonSerializer.SerializeToUtf8Bytes(valid, SharedJson.Options));

        await Eventually.UntilAsync(() => stats.Get("alice") is not null, Timeout, "the valid event after the poison messages");
        var first = await rabbit.GetMessageAsync(StatsQueue + ".dlq", Timeout);
        var second = await rabbit.GetMessageAsync(StatsQueue + ".dlq", Timeout);
        Assert.Equal("this is not json", Encoding.UTF8.GetString(first.Body.Span));
        Assert.Equal("null", Encoding.UTF8.GetString(second.Body.Span));
    }

    [DockerFact]
    public async Task HandlerFailure_IsRetriedOnceThenDeadLettered()
    {
        var consumer = new FailingConsumer(Options.Create(rabbit.Options));
        await StartConsumerAsync(consumer, FailingQueue);
        var publisher = await StartPublisherAsync();
        var match = NewMatchEnded(("alice", 5));

        await publisher.PublishAsync(FailingQueue, match.MessageId, match, CancellationToken.None);

        var deadLettered = await rabbit.GetMessageAsync(FailingQueue + ".dlq", Timeout);
        Assert.Equal(match.MessageId.ToString(), deadLettered.BasicProperties.MessageId);
        Assert.Equal(2, consumer.Attempts);
    }

    [DockerFact]
    public async Task OutboxRelay_PublishesPersistentEventsWithStableMessageId()
    {
        await rabbit.DeclareTestQueueAsync(RelayQueue, "match.*");
        var outbox = new EventOutbox();
        await StartConsumerAsync(new OutboxRelay(outbox, CreatePublisher(), NullLogger<OutboxRelay>.Instance), queue: null);
        var started = new MatchStarted(Guid.NewGuid(), Guid.NewGuid(), "gs-1", ["alice", "bob"], DateTime.UtcNow);

        outbox.Enqueue(Topology.MatchStartedKey, started);

        var message = await rabbit.GetMessageAsync(RelayQueue, Timeout);
        Assert.Equal(Topology.MatchStartedKey, message.RoutingKey);
        Assert.Equal(started.MessageId.ToString(), message.BasicProperties.MessageId);
        Assert.Equal("application/json", message.BasicProperties.ContentType);
        Assert.Equal(nameof(MatchStarted), message.BasicProperties.Type);
        Assert.True(message.BasicProperties.Persistent);
        var body = JsonSerializer.Deserialize<MatchStarted>(message.Body.Span, SharedJson.Options)!;
        Assert.Equal(started.MatchId, body.MatchId);
        Assert.Equal(started.Players, body.Players);
    }

    /// <summary>
    /// Som i produktion: ingen service abonnerer på match.started. Relay'et må ikke hænge på det
    /// event – det efterfølgende MatchEnded skal stadig nå frem til statistikken.
    /// </summary>
    [DockerFact]
    public async Task OutboxRelay_EventWithoutSubscribers_DoesNotBlockLaterEvents()
    {
        var stats = new StatsStore();
        await StartConsumerAsync(new StatsConsumer(stats, Options.Create(rabbit.Options), NullLogger<StatsConsumer>.Instance), StatsQueue);
        var outbox = new EventOutbox();
        await StartConsumerAsync(new OutboxRelay(outbox, CreatePublisher(), NullLogger<OutboxRelay>.Instance), queue: null);
        var matchId = Guid.NewGuid();

        outbox.Enqueue(Topology.MatchStartedKey, new MatchStarted(Guid.NewGuid(), matchId, "gs-1", ["alice"], DateTime.UtcNow));
        outbox.Enqueue(Topology.MatchEndedKey, NewMatchEnded(("alice", 5)) with { MatchId = matchId });

        await Eventually.UntilAsync(() => stats.Get("alice") is not null, Timeout, "MatchEnded to reach the stats consumer");
    }

    private static MatchEnded NewMatchEnded(params (string Name, int Score)[] results) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "gs-1", results.Select(r => new PlayerResult(r.Name, r.Score)).ToList(), DateTime.UtcNow);

    private async Task StartConsumerAsync(Microsoft.Extensions.Hosting.BackgroundService service, string? queue)
    {
        _consumers.Add(service);
        await service.StartAsync(CancellationToken.None);
        if (queue is not null) await rabbit.WaitForConsumerAsync(queue);
    }

    private EventPublisher CreatePublisher()
    {
        var publisher = new EventPublisher(Options.Create(rabbit.Options), NullLogger<EventPublisher>.Instance);
        _disposables.Add(publisher);
        return publisher;
    }

    private async Task<EventPublisher> StartPublisherAsync()
    {
        var publisher = CreatePublisher();
        await publisher.StartAsync(CancellationToken.None);
        return publisher;
    }

    public async Task DisposeAsync()
    {
        foreach (var service in _consumers)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }

    private sealed class FailingConsumer(IOptions<RabbitMqOptions> options)
        : RabbitConsumerBase<MatchEnded>(options, NullLogger.Instance)
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        protected override string QueueName => FailingQueue;
        protected override IReadOnlyList<string> RoutingKeys => [FailingQueue];

        protected override Task HandleAsync(MatchEnded message, string routingKey, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("Simuleret fejl i handler");
        }
    }
}
