using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Hosting;

namespace Shared.Messaging;

/// <summary>
/// Genbrugelig consumer:
///  - durable queue bundet til topic exchange (egen kø pr. consumer-service = publish/subscribe)
///  - manuel ack + prefetch (fair dispatch)
///  - ugyldige beskeder (kan ikke parses) => direkte til dead-letter queue (invalid message channel)
///  - fejl i behandling => genlevering én gang, derefter dead-letter
/// Implementeringer SKAL være idempotente, da genlevering kan give dubletter.
/// </summary>
public abstract class RabbitConsumerBase<TMessage>(IOptions<RabbitMqOptions> options, ILogger logger)
    : BackgroundService, IReadinessSignal
{
    private IConnection? _connection;
    private IChannel? _channel;

    protected abstract string QueueName { get; }
    protected abstract IReadOnlyList<string> RoutingKeys { get; }
    protected virtual ushort PrefetchCount => 10;

    protected abstract Task HandleAsync(TMessage message, string routingKey, CancellationToken ct);

    public string Name => $"rabbitmq:{QueueName}";
    public bool IsReady => _channel?.IsOpen == true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _connection = await RabbitConnector.ConnectAsync(options.Value, logger, stoppingToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await RabbitConnector.DeclareTopologyAsync(_channel, stoppingToken);

        var deadLetterQueue = QueueName + ".dlq";
        await _channel.QueueDeclareAsync(deadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: stoppingToken);
        await _channel.QueueBindAsync(deadLetterQueue, Topology.DeadLetterExchange, QueueName,
            cancellationToken: stoppingToken);

        var arguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = Topology.DeadLetterExchange,
            ["x-dead-letter-routing-key"] = QueueName
        };
        await _channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false,
            arguments: arguments, cancellationToken: stoppingToken);

        foreach (var key in RoutingKeys)
        {
            await _channel.QueueBindAsync(QueueName, Topology.EventsExchange, key, cancellationToken: stoppingToken);
        }

        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: PrefetchCount, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;
        await _channel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);

        logger.LogInformation("Consumer lytter på kø {Queue} ({Keys})", QueueName, string.Join(", ", RoutingKeys));
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        var channel = _channel!;
        TMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<TMessage>(ea.Body.Span, SharedJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Ugyldig besked (MessageId {MessageId}) sendes til dead-letter", ea.BasicProperties.MessageId);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        if (message is null)
        {
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        try
        {
            await HandleAsync(message, ea.RoutingKey, ea.CancellationToken);
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            var requeue = !ea.Redelivered; // én genlevering, derefter dead-letter (undgår poison-loop)
            logger.LogError(ex, "Fejl ved behandling af {MessageId}. Requeue: {Requeue}",
                ea.BasicProperties.MessageId, requeue);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: requeue);
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
