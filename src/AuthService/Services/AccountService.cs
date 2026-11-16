using AuthService.Domain;
using Shared.Auth;
using Shared.Contracts;

namespace AuthService.Services;

public enum AuthStatus { Success, InvalidCredentials, UsernameTaken, InvalidRefreshToken }

public sealed record AuthResult(AuthStatus Status, TokenResponse? Tokens = null);

public interface IAccountService
{
    Task<AuthResult> RegisterAsync(string username, string password);
    Task<AuthResult> LoginAsync(string username, string password);
    Task<AuthResult> RefreshAsync(string refreshToken);
}

public sealed class AccountService(IUserRepository users, TokenService tokens) : IAccountService
{
    // Bruges når brugeren ikke findes, så svartiden ikke afslører om et brugernavn eksisterer
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy-password");

    public async Task<AuthResult> RegisterAsync(string username, string password)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = Roles.Player
        };

        if (!await users.TryAddAsync(user)) return new AuthResult(AuthStatus.UsernameTaken);
        return new AuthResult(AuthStatus.Success, await IssueTokensAsync(user));
    }

    public async Task<AuthResult> LoginAsync(string username, string password)
    {
        var user = await users.GetByUsernameAsync(username.Trim());
        var valid = BCrypt.Net.BCrypt.Verify(password, user?.PasswordHash ?? DummyHash) && user is not null;

        return valid
            ? new AuthResult(AuthStatus.Success, await IssueTokensAsync(user!))
            : new AuthResult(AuthStatus.InvalidCredentials);
    }

    /// <summary>Token rotation: den gamle refresh token bliver ugyldig, når en ny udstedes.</summary>
    public async Task<AuthResult> RefreshAsync(string refreshToken)
    {
        var user = await users.GetByRefreshTokenHashAsync(TokenService.Hash(refreshToken));
        if (user?.RefreshTokenExpiresUtc is null || user.RefreshTokenExpiresUtc <= DateTime.UtcNow)
        {
            return new AuthResult(AuthStatus.InvalidRefreshToken);
        }

        return new AuthResult(AuthStatus.Success, await IssueTokensAsync(user));
    }

    private async Task<TokenResponse> IssueTokensAsync(User user)
    {
        var (accessToken, expiresAt) = tokens.CreateAccessToken(user);
        var refreshToken = TokenService.CreateRefreshToken();

        user.RefreshTokenHash = TokenService.Hash(refreshToken);
        user.RefreshTokenExpiresUtc = DateTime.UtcNow.Add(tokens.RefreshTokenLifetime);
        await users.UpdateAsync(user);

        return new TokenResponse(accessToken, refreshToken, expiresAt);
    }
}
