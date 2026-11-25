using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Shared.Protocol;

namespace OnlineArena.Tests.Infrastructure;

/// <summary>Minimal UDP game client: sends protocol messages and buffers everything the server sends back.</summary>
public sealed class UdpTestClient : IAsyncDisposable
{
    private readonly UdpClient _udp = new(AddressFamily.InterNetwork);
    private readonly Channel<NetMessage> _received = Channel.CreateUnbounded<NetMessage>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;

    public UdpTestClient(string host, int port)
    {
        var address = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _udp.Connect(new IPEndPoint(address, port));
        _receiveLoop = ReceiveLoopAsync();
    }

    public Task SendAsync(NetMessage message) => SendRawAsync(NetCodec.Encode(message));

    public async Task SendRawAsync(byte[] bytes) => await _udp.SendAsync(bytes);

    /// <summary>Returns the next message of type <typeparamref name="T"/>, discarding other messages (e.g. snapshots).</summary>
    public async Task<T?> TryReceiveAsync<T>(TimeSpan timeout, Func<T, bool>? predicate = null) where T : NetMessage
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var message = await _received.Reader.ReadAsync(cts.Token);
                if (message is T typed && (predicate is null || predicate(typed))) return typed;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<T> ReceiveAsync<T>(TimeSpan timeout, Func<T, bool>? predicate = null) where T : NetMessage =>
        await TryReceiveAsync(timeout, predicate)
        ?? throw new TimeoutException($"No matching {typeof(T).Name} received within {timeout.TotalSeconds:0.#}s.");

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                if (NetCodec.Decode(result.Buffer) is { } message) _received.Writer.TryWrite(message);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { } // fx ICMP "port unreachable" på Windows
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _udp.Dispose();
        try { await _receiveLoop; } catch { /* ignoreres ved nedlukning */ }
        _cts.Dispose();
    }
}
