using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Shared.Messaging;

/// <summary>
/// Publicerer events med publisher confirms (brokeren bekræfter modtagelse) og
/// persistente beskeder (overlever broker-genstart når køen er durable).
/// </summary>
public sealed class EventPublisher(IOptions<RabbitMqOptions> options, ILogger<EventPublisher> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1); // en channel er ikke trådsikker til publish
    private IConnection? _connection;
    private IChannel? _channel;

    public bool IsConnected => _channel?.IsOpen == true;

    public async Task StartAsync(CancellationToken ct)
    {
        _connection = await RabbitConnector.ConnectAsync(options.Value, logger, ct);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            ct);
        await RabbitConnector.DeclareTopologyAsync(_channel, ct);
    }

    /// <summary>
    /// Kaster exception hvis brokeren ikke bekræfter (nack/forbindelsesfejl) – kalderen (outbox relay) prøver igen.
    /// Events uden abonnenter (basic.return) logges og kastes ikke, da et genforsøg aldrig ville lykkes.
    /// </summary>
    public async Task PublishAsync(string routingKey, Guid messageId, object payload, CancellationToken ct)
    {
        if (_channel is null) throw new InvalidOperationException("Publisher er ikke startet");

        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = messageId.ToString(),
            ContentType = "application/json",
            Type = payload.GetType().Name,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), SharedJson.Options);

        await _lock.WaitAsync(ct);
        try
        {
            // mandatory: true => beskeder der ikke kan routes returneres i stedet for at forsvinde
            await _channel.BasicPublishAsync(Topology.EventsExchange, routingKey,
                mandatory: true, basicProperties: properties, body: body, cancellationToken: ct);
        }
        catch (PublishException ex) when (ex.IsReturn)
        {
            // Ingen kø abonnerer på routing key'en. Et genforsøg ændrer ikke på det og ville blokere
            // outboxen for alle efterfølgende events – så log og gå videre. (Nack fra brokeren kastes stadig.)
            logger.LogWarning("Event {Type} ({MessageId}) har ingen abonnenter på {RoutingKey} og blev ikke leveret",
                properties.Type, messageId, routingKey);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _lock.Dispose();
    }
}
