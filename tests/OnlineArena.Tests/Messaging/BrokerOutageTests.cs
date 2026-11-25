using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnlineArena.Tests.Infrastructure;
using RabbitMQ.Client;
using Shared;
using Shared.Contracts;
using Shared.Messaging;
using Testcontainers.RabbitMq;

namespace OnlineArena.Tests.Messaging;

/// <summary>
/// Robusthed: gameserverens outbox skal levere events, der opstår mens RabbitMQ er nede, når brokeren kommer tilbage.
/// Egen container med fast host-port, så adressen er den samme efter genstart.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class BrokerOutageTests
{
    private const string Queue = "test.outage";

    [DockerFact]
    public async Task OutboxRelay_DeliversEventsEnqueuedWhileBrokerIsDown()
    {
        var port = GetFreeTcpPort();
        await using var container = new RabbitMqBuilder(RabbitMqFixture.Image)
            .WithUsername(RabbitMqFixture.UserName)
            .WithPassword(RabbitMqFixture.Password)
            .WithPortBinding(port, RabbitMqBuilder.RabbitMqPort)
            .Build();
        await container.StartAsync();

        var options = new RabbitMqOptions
        {
            HostName = container.Hostname,
            Port = port,
            UserName = RabbitMqFixture.UserName,
            Password = RabbitMqFixture.Password
        };
        await DeclareQueueAsync(options);

        var outbox = new EventOutbox();
        await using var publisher = new EventPublisher(Options.Create(options), NullLogger<EventPublisher>.Instance);
        using var relay = new OutboxRelay(outbox, publisher, NullLogger<OutboxRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);

        try
        {
            var before = NewMatchEnded();
            outbox.Enqueue(Topology.MatchEndedKey, before);
            Assert.Equal(before.MessageId, await ReceiveMessageIdAsync(options, TimeSpan.FromSeconds(30)));

            await container.StopAsync();
            var duringOutage = NewMatchEnded();
            outbox.Enqueue(Topology.MatchEndedKey, duringOutage);
            await Task.Delay(TimeSpan.FromSeconds(2)); // relay'et forsøger og fejler mens brokeren er nede
            await container.StartAsync();

            Assert.Equal(duringOutage.MessageId, await ReceiveMessageIdAsync(options, TimeSpan.FromSeconds(90)));
        }
        finally
        {
            await relay.StopAsync(CancellationToken.None);
        }
    }

    private static MatchEnded NewMatchEnded() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "gs-1", [new PlayerResult("alice", 1)], DateTime.UtcNow);

    private static async Task DeclareQueueAsync(RabbitMqOptions options)
    {
        await using var connection = await RabbitMqFixture.CreateConnectionFactory(options).CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitConnector.DeclareTopologyAsync(channel, CancellationToken.None);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(Queue, Topology.EventsExchange, Topology.MatchEndedKey);
    }

    /// <summary>Tåler at brokeren stadig er ved at starte op (forbindelsesfejl => prøv igen).</summary>
    private static async Task<Guid> ReceiveMessageIdAsync(RabbitMqOptions options, TimeSpan timeout)
    {
        Guid? messageId = null;
        await Eventually.UntilAsync(async () =>
        {
            try
            {
                await using var connection = await RabbitMqFixture.CreateConnectionFactory(options).CreateConnectionAsync();
                await using var channel = await connection.CreateChannelAsync();
                var result = await channel.BasicGetAsync(Queue, autoAck: true);
                if (result is null) return false;
                messageId = JsonSerializer.Deserialize<MatchEnded>(result.Body.Span, SharedJson.Options)!.MessageId;
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await Task.Delay(500);
                return false;
            }
        }, timeout, $"a message in '{Queue}'");
        return messageId!.Value;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
