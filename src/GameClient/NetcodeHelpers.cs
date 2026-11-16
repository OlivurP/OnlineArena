using Shared.Protocol;

namespace GameClient;

/// <summary>
/// Client-side prediction + server reconciliation for den lokale spiller.
/// Input anvendes straks lokalt (ingen input-lag), og når serverens autoritative
/// position ankommer, genspilles alle inputs serveren endnu ikke har behandlet.
/// </summary>
public sealed class PredictedPlayer
{
    private readonly List<(int Sequence, float Dx, float Dy)> _pending = [];

    public float X { get; private set; }
    public float Y { get; private set; }
    public int PendingCount => _pending.Count;

    public void ApplyLocal(int sequence, float dx, float dy)
    {
        _pending.Add((sequence, dx, dy));
        (X, Y) = GameRules.Move(X, Y, dx, dy);
    }

    public void Reconcile(float serverX, float serverY, int lastProcessedInput)
    {
        X = serverX;
        Y = serverY;
        _pending.RemoveAll(p => p.Sequence <= lastProcessedInput);
        foreach (var (_, dx, dy) in _pending) (X, Y) = GameRules.Move(X, Y, dx, dy);
    }
}

/// <summary>
/// Entity interpolation for andre spillere: de vises lidt i fortiden og glider
/// jævnt mellem to modtagne snapshots i stedet for at "teleportere".
/// </summary>
public sealed class EntityInterpolator
{
    private readonly Dictionary<string, List<(DateTime At, float X, float Y)>> _buffers = new();

    public void Add(string id, float x, float y, DateTime receivedAt)
    {
        if (!_buffers.TryGetValue(id, out var buffer)) _buffers[id] = buffer = [];
        buffer.Add((receivedAt, x, y));
        if (buffer.Count > 20) buffer.RemoveAt(0);
    }

    public void Retain(IEnumerable<string> ids)
    {
        var keep = ids.ToHashSet();
        foreach (var id in _buffers.Keys.Where(k => !keep.Contains(k)).ToList()) _buffers.Remove(id);
    }

    public (float X, float Y)? Get(string id, DateTime renderTime)
    {
        if (!_buffers.TryGetValue(id, out var buffer) || buffer.Count == 0) return null;

        for (var i = buffer.Count - 1; i > 0; i--)
        {
            var (t0, x0, y0) = buffer[i - 1];
            var (t1, x1, y1) = buffer[i];
            if (t0 <= renderTime && renderTime <= t1)
            {
                var span = Math.Max(1, (t1 - t0).TotalMilliseconds);
                var f = (float)((renderTime - t0).TotalMilliseconds / span);
                return (x0 + (x1 - x0) * f, y0 + (y1 - y0) * f);
            }
        }

        var edge = renderTime < buffer[0].At ? buffer[0] : buffer[^1];
        return (edge.X, edge.Y);
    }
}
