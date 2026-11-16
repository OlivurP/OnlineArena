using System.Threading.Channels;
using Shared.Messaging;

namespace GameServer.Networking;

public sealed record OutboxItem(string RoutingKey, Guid MessageId, object Payload);

/// <summary>
/// Simpel in-memory outbox: game loop'et lægger events her og fortsætter straks,
/// så spillet aldrig blokerer på RabbitMQ. Events overlever et broker-udfald,
/// men IKKE et crash af gameserveren (en database-outbox ville løse det – trade-off).
/// </summary>
public sealed class EventOutbox
{
    private readonly Channel<OutboxItem> _channel = Channel.CreateUnbounded<OutboxItem>();

    public ChannelReader<OutboxItem> Reader => _channel.Reader;

    public void Enqueue(string routingKey, object payload)
    {
        var messageId = payload switch
        {
            Shared.Contracts.MatchStarted s => s.MessageId,
            Shared.Contracts.MatchEnded e => e.MessageId,
            _ => Guid.NewGuid()
        };
        _channel.Writer.TryWrite(new OutboxItem(routingKey, messageId, payload));
    }
}

/// <summary>Message relay: sender outbox-events til RabbitMQ og prøver igen med backoff, indtil brokeren bekræfter.</summary>
public sealed class OutboxRelay(EventOutbox outbox, EventPublisher publisher, ILogger<OutboxRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await publisher.StartAsync(stoppingToken);

        await foreach (var item in outbox.Reader.ReadAllAsync(stoppingToken))
        {
            var delay = TimeSpan.FromSeconds(1);
            while (true)
            {
                try
                {
                    await publisher.PublishAsync(item.RoutingKey, item.MessageId, item.Payload, stoppingToken);
                    logger.LogInformation("Event {Type} publiceret ({MessageId})", item.Payload.GetType().Name, item.MessageId);
                    break;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Publish fejlede ({Message}) – prøver igen om {Delay}s", ex.Message, delay.TotalSeconds);
                    await Task.Delay(delay, stoppingToken);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                }
            }
        }
    }
}
