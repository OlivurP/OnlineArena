using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Shared.Messaging;

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
}

public static class Topology
{
    /// <summary>Topic exchange: publisher kender ikke sine consumers (løs kobling, one-to-many).</summary>
    public const string EventsExchange = "game.events";

    /// <summary>Dead-letter exchange: beskeder der ikke kan behandles havner i "&lt;queue&gt;.dlq".</summary>
    public const string DeadLetterExchange = "game.events.dlx";

    public const string MatchStartedKey = "match.started";
    public const string MatchEndedKey = "match.ended";
}

public static class RabbitConnector
{
    /// <summary>Forbinder med exponential backoff – brokeren er måske ikke klar endnu ved opstart.</summary>
    public static async Task<IConnection> ConnectAsync(RabbitMqOptions options, ILogger logger, CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = options.HostName,
            Port = options.Port,
            UserName = options.UserName,
            Password = options.Password,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ClientProvidedName = AppDomain.CurrentDomain.FriendlyName
        };

        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                var connection = await factory.CreateConnectionAsync(ct);
                logger.LogInformation("Forbundet til RabbitMQ på {Host}:{Port}", options.HostName, options.Port);
                return connection;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("RabbitMQ ikke tilgængelig ({Message}). Prøver igen om {Delay}s",
                    ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    /// <summary>Idempotent: deklarering af eksisterende exchanges gør ingenting.</summary>
    public static async Task DeclareTopologyAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Topology.EventsExchange, ExchangeType.Topic,
            durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(Topology.DeadLetterExchange, ExchangeType.Direct,
            durable: true, autoDelete: false, cancellationToken: ct);
    }
}
