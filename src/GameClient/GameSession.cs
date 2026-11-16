using System.Net;
using System.Net.Sockets;
using System.Text;
using Shared.Contracts;
using Shared.Protocol;

namespace GameClient;

/// <summary>
/// UDP-session mod gameserveren. Styring: WASD/piletaster, P = prediction til/fra,
/// I = interpolation til/fra, Q = afslut. Prøv at slå P fra og mærk input-lag.
/// </summary>
internal sealed class GameSession(string host, int port, string accessToken)
{
    private static readonly TimeSpan InputInterval = TimeSpan.FromMilliseconds(50);   // 20 Hz input
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan InterpolationDelay = TimeSpan.FromMilliseconds(100); // ~2 ticks

    private readonly object _lock = new();
    private readonly PredictedPlayer _me = new();
    private readonly EntityInterpolator _interpolator = new();
    private UdpClient? _udp;
    private SnapshotMessage? _latest;
    private IReadOnlyList<PlayerResult>? _lastResults;
    private string? _playerId;
    private string _status = "Forbinder...";
    private bool _prediction = true;
    private bool _interpolation = true;
    private int _sequence;

    public async Task RunAsync()
    {
        var address = (await Dns.GetHostAddressesAsync(host)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _udp = new UdpClient();
        _udp.Connect(new IPEndPoint(address, port));

        using var cts = new CancellationTokenSource();
        var receiveTask = ReceiveLoopAsync(cts.Token);

        // UDP kan tabe pakker – join gensendes indtil serveren svarer
        for (var attempt = 0; attempt < 5 && _playerId is null && !_status.StartsWith("Afvist"); attempt++)
        {
            await SendAsync(new JoinMessage(accessToken));
            await Task.Delay(1000);
        }

        if (_playerId is null)
        {
            Console.WriteLine(_status == "Forbinder..." ? "Ingen svar fra gameserveren." : _status);
            await cts.CancelAsync();
            return;
        }

        Console.Clear();
        Console.CursorVisible = false;
        var nextRender = DateTime.UtcNow;

        try
        {
            while (true)
            {
                var (dx, dy, quit) = ReadInput();
                if (quit) break;

                _sequence++;
                lock (_lock) _me.ApplyLocal(_sequence, dx, dy);
                await SendAsync(new InputMessage(_sequence, dx, dy)); // sendes også ved (0,0) = heartbeat

                if (DateTime.UtcNow >= nextRender)
                {
                    Render();
                    nextRender = DateTime.UtcNow + RenderInterval;
                }

                await Task.Delay(InputInterval);
            }
        }
        finally
        {
            await cts.CancelAsync();
            Console.CursorVisible = true;
            try { await receiveTask; } catch { /* ignoreres */ }
            _udp.Dispose();
        }
    }

    private (float Dx, float Dy, bool Quit) ReadInput()
    {
        float dx = 0, dy = 0;
        var quit = false;
        while (Console.KeyAvailable)
        {
            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.W or ConsoleKey.UpArrow: dy = -1; break;
                case ConsoleKey.S or ConsoleKey.DownArrow: dy = 1; break;
                case ConsoleKey.A or ConsoleKey.LeftArrow: dx = -1; break;
                case ConsoleKey.D or ConsoleKey.RightArrow: dx = 1; break;
                case ConsoleKey.P: _prediction = !_prediction; break;
                case ConsoleKey.I: _interpolation = !_interpolation; break;
                case ConsoleKey.Q or ConsoleKey.Escape: quit = true; break;
            }
        }
        return (dx, dy, quit);
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
            catch (SocketException) { continue; }

            var message = NetCodec.Decode(result.Buffer);
            var now = DateTime.UtcNow;

            lock (_lock)
            {
                switch (message)
                {
                    case WelcomeMessage welcome:
                        _playerId = welcome.PlayerId;
                        _status = "Forbundet";
                        break;

                    case RejectedMessage rejected:
                        _status = $"Afvist: {rejected.Reason}";
                        break;

                    case SnapshotMessage snapshot:
                        if (_latest is not null && snapshot.Tick <= _latest.Tick) break; // gammel/dublet pakke
                        _latest = snapshot;
                        foreach (var p in snapshot.Players)
                        {
                            if (p.Id == _playerId) _me.Reconcile(p.X, p.Y, snapshot.LastProcessedInput);
                            else _interpolator.Add(p.Id, p.X, p.Y, now);
                        }
                        _interpolator.Retain(snapshot.Players.Select(p => p.Id));
                        break;

                    case MatchEndedMessage ended:
                        _lastResults = ended.Results;
                        break;
                }
            }
        }
    }

    private void Render()
    {
        lock (_lock)
        {
            if (_latest is null) return;

            var width = (int)GameRules.WorldWidth + 1;
            var height = (int)GameRules.WorldHeight + 1;
            var grid = new char[height, width];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    grid[y, x] = '.';

            void Plot(float x, float y, char c) =>
                grid[Math.Clamp((int)MathF.Round(y), 0, height - 1), Math.Clamp((int)MathF.Round(x), 0, width - 1)] = c;

            foreach (var coin in _latest.Coins) Plot(coin.X, coin.Y, '$');

            var renderTime = DateTime.UtcNow - InterpolationDelay;
            foreach (var p in _latest.Players.Where(p => p.Id != _playerId))
            {
                var pos = _interpolation ? _interpolator.Get(p.Id, renderTime) ?? (p.X, p.Y) : (p.X, p.Y);
                Plot(pos.X, pos.Y, char.ToUpperInvariant(p.Name.FirstOrDefault('?')));
            }

            var self = _latest.Players.FirstOrDefault(p => p.Id == _playerId);
            if (self is not null)
            {
                if (_prediction) Plot(_me.X, _me.Y, '@');
                else Plot(self.X, self.Y, '@');
            }

            var sb = new StringBuilder();
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++) sb.Append(grid[y, x]);
                sb.AppendLine();
            }

            sb.AppendLine($"Fase: {_latest.Phase,-18} Tid tilbage: {_latest.SecondsLeft,5:0}s   Tick: {_latest.Tick}".PadRight(70));
            sb.AppendLine($"[P] Prediction: {(_prediction ? "TIL" : "FRA"),-4} [I] Interpolation: {(_interpolation ? "TIL" : "FRA"),-4} Ubekræftede inputs: {_me.PendingCount,3}".PadRight(70));
            sb.AppendLine("Score: " + string.Join("  ", _latest.Players.OrderByDescending(p => p.Score).Select(p => $"{p.Name}={p.Score}")).PadRight(63));
            sb.AppendLine((_lastResults is null
                ? "Saml $ med WASD. Q = afslut."
                : "Sidste match: " + string.Join(", ", _lastResults.Select(r => $"{r.Username} {r.Score}"))).PadRight(70));

            Console.SetCursorPosition(0, 0);
            Console.Write(sb.ToString());
        }
    }

    private async Task SendAsync(NetMessage message)
    {
        try
        {
            var bytes = NetCodec.Encode(message);
            await _udp!.SendAsync(bytes, bytes.Length);
        }
        catch (SocketException) { /* UDP: tabte pakker er forventelige */ }
    }
}
