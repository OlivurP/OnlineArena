using System.Collections.Concurrent;
using Shared.Auth;

namespace AuthService.Domain;

public sealed class User
{
    public Guid Id { get; init; }
    public required string Username { get; init; }
    public required string PasswordHash { get; init; }
    public required string Role { get; init; }
    public string? RefreshTokenHash { get; set; }
    public DateTime? RefreshTokenExpiresUtc { get; set; }
}

/// <summary>Repository-abstraktion: in-memory nu, kan udskiftes med PostgreSQL/EF Core uden at ændre servicelaget.</summary>
public interface IUserRepository
{
    Task<User?> GetByUsernameAsync(string username);
    Task<User?> GetByRefreshTokenHashAsync(string refreshTokenHash);
    Task<bool> TryAddAsync(User user);
    Task UpdateAsync(User user);
}

public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<string, User> _users = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryUserRepository(IConfiguration configuration, ILogger<InMemoryUserRepository> logger)
    {
        var adminPassword = configuration["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(adminPassword))
        {
            logger.LogInformation("Ingen Seed:AdminPassword – admin-bruger oprettes ikke");
            return;
        }

        _users["admin"] = new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
            Role = Roles.Admin
        };
    }

    public Task<User?> GetByUsernameAsync(string username) =>
        Task.FromResult(_users.GetValueOrDefault(username));

    public Task<User?> GetByRefreshTokenHashAsync(string refreshTokenHash) =>
        Task.FromResult(_users.Values.FirstOrDefault(u => u.RefreshTokenHash == refreshTokenHash));

    public Task<bool> TryAddAsync(User user) => Task.FromResult(_users.TryAdd(user.Username, user));

    public Task UpdateAsync(User user)
    {
        _users[user.Username] = user;
        return Task.CompletedTask;
    }
}
