using System.Text.Json;
using System.Text.Json.Serialization;
using Shared.Contracts;

namespace Shared.Protocol;

// UDP-protokollen mellem GameClient og GameServer.
// JSON er valgt for læsbarhed/debugging – binært format ville give mindre pakker (trade-off).

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JoinMessage), "join")]
[JsonDerivedType(typeof(InputMessage), "input")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(RejectedMessage), "rejected")]
[JsonDerivedType(typeof(SnapshotMessage), "snapshot")]
[JsonDerivedType(typeof(MatchEndedMessage), "matchEnded")]
public abstract record NetMessage;

/// <summary>Klient -> server: JWT fra AuthService. Valideres stateless på gameserveren.</summary>
public sealed record JoinMessage(string Token) : NetMessage;

/// <summary>Klient -> server: kun INPUT (ikke position). Serveren er autoritativ.</summary>
public sealed record InputMessage(int Sequence, float Dx, float Dy) : NetMessage;

public sealed record WelcomeMessage(string PlayerId, int TickRate) : NetMessage;

public sealed record RejectedMessage(string Reason) : NetMessage;

/// <summary>Server -> klient: autoritativ tilstand + sidste behandlede input (til reconciliation).</summary>
public sealed record SnapshotMessage(
    long Tick,
    int LastProcessedInput,
    string Phase,
    double SecondsLeft,
    IReadOnlyList<PlayerState> Players,
    IReadOnlyList<CoinState> Coins) : NetMessage;

public sealed record MatchEndedMessage(IReadOnlyList<PlayerResult> Results) : NetMessage;

public sealed record PlayerState(string Id, string Name, float X, float Y, int Score);

public sealed record CoinState(int Id, float X, float Y);

public static class NetCodec
{
    public const int MaxPacketBytes = 2048;

    public static byte[] Encode(NetMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, SharedJson.Options);

    /// <summary>Returnerer null for ugyldige pakker – serveren må aldrig crashe på klientinput.</summary>
    public static NetMessage? Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            return JsonSerializer.Deserialize<NetMessage>(data, SharedJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
