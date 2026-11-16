using System.ComponentModel.DataAnnotations;

namespace Shared.Contracts;

// ---------- Auth ----------
public sealed record RegisterRequest(
    [Required, StringLength(20, MinimumLength = 3)] string Username,
    [Required, StringLength(100, MinimumLength = 6)] string Password);

public sealed record LoginRequest(
    [Required] string Username,
    [Required] string Password);

public sealed record RefreshRequest([Required] string RefreshToken);

public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc);

// ---------- Matchmaking ----------
public sealed record TicketResponse(Guid TicketId, string Status, string? ServerHost, int? ServerPort);

public sealed record RegisterServerRequest(
    [Required] string ServerId,
    [Required] string PublicHost,
    [Range(1, 65535)] int PublicPort,
    [Required, Url] string HealthUrl,
    [Range(1, 64)] int Capacity);

public sealed record HeartbeatRequest([Range(0, 64)] int CurrentPlayers);
