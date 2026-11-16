using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GameServer.Networking;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shared.Auth;
using Shared.Contracts;
using Shared.Messaging;
using Shared.Protocol;

namespace GameServer.Game;

public enum MatchPhase { WaitingForPlayers, Running, Intermission }

/// <summary>
/// Dedikeret autoritativ gameserver:
///  - modtager INPUT over UDP (ikke positioner)
///  - simulerer verden med fast tickrate
///  - sender snapshots til alle klienter efter hver tick
///  - publicerer MatchStarted/MatchEnded via outboxen
/// </summary>
public sealed class GameLoop(
    IOptions<GameServerOptions> options,
    IOptions<JwtOptions> jwtOptions,
    EventOutbox outbox,
    ServerState state,
    TimeProvider time,
    ILogger<GameLoop> logger) : BackgroundService
{
    private const int MaxBufferedInputsPerClient = 30;
    private const int MaxIncomingQueue = 10_000;

    private readonly GameServerOptions _options = options.Value;
    private readonly ConcurrentQueue<(IPEndPoint From, NetMessage Message)> _incoming = new();
    private readonly Dictionary<IPEndPoint, ClientSession> _sessions = new();
    private readonly JsonWebTokenHandler _tokenHandler = new();
    private readonly GameWorld _world = new();

    private TokenValidationParameters? _tokenValidation;
    private UdpClient? _udp;
    private MatchPhase _phase = MatchPhase.WaitingForPlayers;
    private Guid _matchId;
    private DateTime _phaseEndsAtUtc;
    private long _tick;
    private MatchEndedMessage? _pendingMatchEnded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tokenValidation = jwtOptions.Value.CreateValidationParameters();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _options.UdpPort));
        state.MarkUdpBound();
        logger.LogInformation("Gameserver {ServerId} lytter på UDP {Port} med {TickRate} Hz",
            _options.ServerId, _options.UdpPort, _options.TickRate);

        var receiveTask = ReceiveLoopAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / _options.TickRate), time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = time.GetUtcNow().UtcDateTime;
                await ProcessIncomingAsync(now, stoppingToken);
                Simulate(now);
                await BroadcastAsync(now, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // normal nedlukning
        }
        finally
        {
            _udp.Dispose();
            try { await receiveTask; } catch { /* ignoreres ved nedlukning */ }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp!.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; } // fx ICMP "port unreachable" fra en klient der er væk

            if (result.Buffer.Length > NetCodec.MaxPacketBytes || _incoming.Count > MaxIncomingQueue) continue;

            var message = NetCodec.Decode(result.Buffer);
            if (message is not null) _incoming.Enqueue((result.RemoteEndPoint, message));
        }
    }

    private async Task ProcessIncomingAsync(DateTime now, CancellationToken ct)
    {
        while (_incoming.TryDequeue(out var item))
        {
            switch (item.Message)
            {
                case JoinMessage join:
                    await HandleJoinAsync(item.From, join, now, ct);
                    break;

                case InputMessage input when _sessions.TryGetValue(item.From, out var session):
                    session.LastSeenUtc = now;
                    if (session.PendingInputs.Count < MaxBufferedInputsPerClient)
                    {
                        session.PendingInputs.Enqueue(input);
                    }
                    break;
            }
        }
    }

    private async Task HandleJoinAsync(IPEndPoint from, JoinMessage join, DateTime now, CancellationToken ct)
    {
        // Dublet-join (klienten gensender join, fordi UDP kan tabe pakker)
        if (_sessions.TryGetValue(from, out var existing))
        {
            await SendAsync(from, new WelcomeMessage(existing.PlayerId, _options.TickRate), ct);
            return;
        }

        var validation = await _tokenHandler.ValidateTokenAsync(join.Token, _tokenValidation);
        var playerId = validation.IsValid ? validation.ClaimsIdentity.FindFirst("sub")?.Value : null;
        if (playerId is null)
        {
            await SendAsync(from, new RejectedMessage("Ugyldigt eller udløbet token"), ct);
            return;
        }

        var name = validation.ClaimsIdentity.Name ?? "?";

        // Reconnect fra ny adresse: fjern den gamle session
        var previous = _sessions.FirstOrDefault(kv => kv.Value.PlayerId == playerId);
        if (previous.Value is not null)
        {
            _sessions.Remove(previous.Key);
        }
        else if (_sessions.Count >= _options.Capacity)
        {
            await SendAsync(from, new RejectedMessage("Serveren er fuld"), ct);
            return;
        }

        var player = _world.AddPlayer(playerId, name);
        player.LastProcessedInput = 0; // ny session => klienten starter sekvens forfra

        _sessions[from] = new ClientSession(playerId, name, from) { LastSeenUtc = now };
        logger.LogInformation("{Player} forbundet fra {EndPoint} ({Count} spillere)", name, from, _sessions.Count);

        await SendAsync(from, new WelcomeMessage(playerId, _options.TickRate), ct);
    }

    private void Simulate(DateTime now)
    {
        // Timeout af klienter der ikke har sendt noget
        var timeout = TimeSpan.FromSeconds(_options.ClientTimeoutSeconds);
        foreach (var (endPoint, session) in _sessions.Where(kv => now - kv.Value.LastSeenUtc > timeout).ToList())
        {
            _sessions.Remove(endPoint);
            _world.RemovePlayer(session.PlayerId);
            logger.LogInformation("{Player} timeout", session.Name);
        }

        // Begræns antal inputs pr. tick (beskytter mod "speed hacks" der sender ekstra inputs)
        foreach (var session in _sessions.Values)
        {
            for (var i = 0; i < _options.MaxInputsPerTick && session.PendingInputs.TryDequeue(out var input); i++)
            {
                _world.ApplyInput(session.PlayerId, input.Sequence, input.Dx, input.Dy);
            }
        }

        _tick++;
        UpdateMatchPhase(now);
        state.CurrentPlayers = _sessions.Count;
    }

    private void UpdateMatchPhase(DateTime now)
    {
        switch (_phase)
        {
            case MatchPhase.WaitingForPlayers when _sessions.Count >= _options.MinPlayersToStart:
                _matchId = Guid.NewGuid();
                _world.ResetScores();
                _phase = MatchPhase.Running;
                _phaseEndsAtUtc = now.AddSeconds(_options.MatchDurationSeconds);

                outbox.Enqueue(Topology.MatchStartedKey, new MatchStarted(
                    Guid.NewGuid(), _matchId, _options.ServerId,
                    _world.Players.Select(p => p.Name).ToList(), now));
                logger.LogInformation("Match {MatchId} startet", _matchId);
                break;

            case MatchPhase.Running when now >= _phaseEndsAtUtc || _sessions.Count == 0:
                var results = _world.Players
                    .OrderByDescending(p => p.Score)
                    .Select(p => new PlayerResult(p.Name, p.Score))
                    .ToList();

                if (results.Count > 0)
                {
                    // MessageId sættes ÉN gang – genforsøg i outboxen bruger samme id (idempotens hos consumers)
                    outbox.Enqueue(Topology.MatchEndedKey, new MatchEnded(
                        Guid.NewGuid(), _matchId, _options.ServerId, results, now));
                }

                _pendingMatchEnded = new MatchEndedMessage(results);
                _phase = MatchPhase.Intermission;
                _phaseEndsAtUtc = now.AddSeconds(_options.IntermissionSeconds);
                logger.LogInformation("Match {MatchId} slut", _matchId);
                break;

            case MatchPhase.Intermission when now >= _phaseEndsAtUtc:
                _phase = MatchPhase.WaitingForPlayers;
                break;
        }
    }

    private async Task BroadcastAsync(DateTime now, CancellationToken ct)
    {
        if (_pendingMatchEnded is not null)
        {
            foreach (var session in _sessions.Values) await SendAsync(session.EndPoint, _pendingMatchEnded, ct);
            _pendingMatchEnded = null;
        }

        var players = _world.Players.Select(p => new PlayerState(p.Id, p.Name, p.X, p.Y, p.Score)).ToList();
        var coins = _world.CoinStates;
        var secondsLeft = _phase == MatchPhase.Running ? Math.Max(0, (_phaseEndsAtUtc - now).TotalSeconds) : 0;

        foreach (var session in _sessions.Values)
        {
            var lastProcessed = _world.TryGetPlayer(session.PlayerId)?.LastProcessedInput ?? 0;
            var snapshot = new SnapshotMessage(_tick, lastProcessed, _phase.ToString(), secondsLeft, players, coins);
            await SendAsync(session.EndPoint, snapshot, ct);
        }
    }

    private async Task SendAsync(IPEndPoint to, NetMessage message, CancellationToken ct)
    {
        try
        {
            await _udp!.SendAsync(NetCodec.Encode(message), to, ct);
        }
        catch (SocketException ex)
        {
            logger.LogDebug("UDP send til {EndPoint} fejlede: {Message}", to, ex.Message);
        }
    }

    private sealed class ClientSession(string playerId, string name, IPEndPoint endPoint)
    {
        public string PlayerId { get; } = playerId;
        public string Name { get; } = name;
        public IPEndPoint EndPoint { get; } = endPoint;
        public DateTime LastSeenUtc { get; set; }
        public Queue<InputMessage> PendingInputs { get; } = new();
    }
}
