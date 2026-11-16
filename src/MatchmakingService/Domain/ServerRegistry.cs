using Microsoft.Extensions.Options;
using Shared.Contracts;

namespace MatchmakingService.Domain;

public sealed class MatchmakingOptions
{
    public const string Section = "Matchmaking";
    public int MatchSize { get; set; } = 2;
    public int HeartbeatTimeoutSeconds { get; set; } = 15;
    public int StaleServerSeconds { get; set; } = 60;
    public int ReservationSeconds { get; set; } = 10;
    public int TicketMaxAgeMinutes { get; set; } = 5;
}

public sealed record ServerAllocation(string ServerId, string Host, int Port);

public sealed record ServerView(
    string ServerId, string PublicHost, int PublicPort, int Capacity, int CurrentPlayers,
    bool IsHealthy, DateTime LastHeartbeatUtc);

/// <summary>
/// Service registry for gameservere (self-registration + heartbeats).
/// Registreret betyder IKKE klar: en server bruges først, når HealthCheckInvoker har set /health/ready svare 200.
/// </summary>
public sealed class ServerRegistry(TimeProvider time, IOptions<MatchmakingOptions> options)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, RegisteredServer> _servers = new(StringComparer.OrdinalIgnoreCase);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public void Register(RegisterServerRequest request)
    {
        lock (_lock)
        {
            _servers[request.ServerId] = new RegisteredServer
            {
                ServerId = request.ServerId,
                PublicHost = request.PublicHost,
                PublicPort = request.PublicPort,
                HealthUrl = request.HealthUrl,
                Capacity = request.Capacity,
                LastHeartbeatUtc = Now,
                IsHealthy = false
            };
        }
    }

    /// <returns>false hvis serveren ikke kendes (fx efter genstart af matchmaking) => serveren registrerer sig igen.</returns>
    public bool Heartbeat(string serverId, int currentPlayers)
    {
        lock (_lock)
        {
            if (!_servers.TryGetValue(serverId, out var server)) return false;
            server.CurrentPlayers = currentPlayers;
            server.LastHeartbeatUtc = Now;
            return true;
        }
    }

    public void ReportHealth(string serverId, bool healthy)
    {
        lock (_lock)
        {
            if (!_servers.TryGetValue(serverId, out var server)) return;
            if (healthy)
            {
                server.IsHealthy = true;
                server.ConsecutiveFailures = 0;
            }
            else if (++server.ConsecutiveFailures >= 3)
            {
                server.IsHealthy = false;
            }
        }
    }

    /// <summary>Fjerner servere der er crashet uden at afregistrere sig.</summary>
    public int RemoveStale()
    {
        lock (_lock)
        {
            var limit = TimeSpan.FromSeconds(options.Value.StaleServerSeconds);
            var stale = _servers.Values.Where(s => Now - s.LastHeartbeatUtc > limit).Select(s => s.ServerId).ToList();
            stale.ForEach(id => _servers.Remove(id));
            return stale.Count;
        }
    }

    /// <summary>Vælger den sunde server med mest ledig kapacitet (least-connections) og reserverer pladser.</summary>
    public ServerAllocation? TryReserve(int slots)
    {
        lock (_lock)
        {
            var now = Now;
            var heartbeatTimeout = TimeSpan.FromSeconds(options.Value.HeartbeatTimeoutSeconds);

            var best = _servers.Values
                .Where(s => s.IsHealthy && now - s.LastHeartbeatUtc <= heartbeatTimeout)
                .Select(s => (Server: s, Free: s.Capacity - s.CurrentPlayers - s.ActiveReservations(now)))
                .Where(x => x.Free >= slots)
                .OrderByDescending(x => x.Free)
                .FirstOrDefault();

            if (best.Server is null) return null;

            // Reservationer udløber automatisk – spillerne tælles derefter via heartbeat
            var expires = now.AddSeconds(options.Value.ReservationSeconds);
            for (var i = 0; i < slots; i++) best.Server.Reservations.Add(expires);

            return new ServerAllocation(best.Server.ServerId, best.Server.PublicHost, best.Server.PublicPort);
        }
    }

    public IReadOnlyList<(string ServerId, string HealthUrl)> GetHealthTargets()
    {
        lock (_lock) return _servers.Values.Select(s => (s.ServerId, s.HealthUrl)).ToList();
    }

    public IReadOnlyList<ServerView> GetAll()
    {
        lock (_lock)
        {
            return _servers.Values
                .Select(s => new ServerView(s.ServerId, s.PublicHost, s.PublicPort, s.Capacity,
                    s.CurrentPlayers, s.IsHealthy, s.LastHeartbeatUtc))
                .ToList();
        }
    }

    private sealed class RegisteredServer
    {
        public required string ServerId { get; init; }
        public required string PublicHost { get; init; }
        public required int PublicPort { get; init; }
        public required string HealthUrl { get; init; }
        public required int Capacity { get; init; }
        public int CurrentPlayers { get; set; }
        public bool IsHealthy { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTime LastHeartbeatUtc { get; set; }
        public List<DateTime> Reservations { get; } = [];

        public int ActiveReservations(DateTime now)
        {
            Reservations.RemoveAll(expires => expires <= now);
            return Reservations.Count;
        }
    }
}
