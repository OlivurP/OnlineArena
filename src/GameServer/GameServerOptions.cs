using Shared.Hosting;

namespace GameServer;

public sealed class GameServerOptions
{
    public const string Section = "GameServer";

    public string ServerId { get; set; } = Environment.MachineName;
    public int UdpPort { get; set; } = 7777;

    /// <summary>Adressen klienterne skal forbinde til (kan afvige fra intern adresse, fx bag NAT/Docker).</summary>
    public string PublicHost { get; set; } = "localhost";
    public int PublicPort { get; set; } = 7777;

    /// <summary>Intern HTTP-adresse som matchmaking bruger til health checks.</summary>
    public string InternalBaseUrl { get; set; } = "http://localhost:5100";
    public string MatchmakingUrl { get; set; } = "http://localhost:5002";

    public int Capacity { get; set; } = 8;
    public int TickRate { get; set; } = 20;
    public int MaxInputsPerTick { get; set; } = 4;
    public int MinPlayersToStart { get; set; } = 2;
    public int MatchDurationSeconds { get; set; } = 90;
    public int IntermissionSeconds { get; set; } = 5;
    public int ClientTimeoutSeconds { get; set; } = 10;
    public int HeartbeatSeconds { get; set; } = 5;
}

/// <summary>Delt tilstand mellem game loop, registrering og readiness-check.</summary>
public sealed class ServerState : IReadinessSignal
{
    private volatile bool _udpBound;
    private int _currentPlayers;

    public string Name => "udp-listener";
    public bool IsReady => _udpBound;

    public void MarkUdpBound() => _udpBound = true;

    public int CurrentPlayers
    {
        get => Volatile.Read(ref _currentPlayers);
        set => Volatile.Write(ref _currentPlayers, value);
    }
}
