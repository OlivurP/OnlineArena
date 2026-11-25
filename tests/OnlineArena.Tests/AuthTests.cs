using AuthService.Domain;
using AuthService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Shared.Auth;

namespace OnlineArena.Tests;

internal static class TestJwt
{
    public const string Secret = "test-only-secret-0123456789abcdef-0123456789";

    public static JwtOptions CreateOptions(string secret = Secret) => new() { Secret = secret };

    public static TokenService CreateTokenService() => new(Options.Create(CreateOptions()));

    public static string CreateAccessToken(string username, string role = Roles.Player, Guid? userId = null) =>
        CreateTokenService().CreateAccessToken(new User
        {
            Id = userId ?? Guid.NewGuid(),
            Username = username,
            PasswordHash = "-",
            Role = role
        }).Token;

    /// <summary>Korrekt signeret token der udløb for længe siden (langt uden for ClockSkew).</summary>
    public static string CreateExpiredAccessToken(string username)
    {
        var options = CreateOptions();
        var issued = DateTime.UtcNow.AddHours(-2);
        return new JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = issued,
            NotBefore = issued,
            Expires = issued.AddMinutes(15),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                options.CreateSigningKey(), Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Guid.NewGuid().ToString(),
                ["name"] = username,
                ["role"] = Roles.Player
            }
        });
    }
}

public class AccountServiceTests
{
    private readonly InMemoryUserRepository _users = CreateRepository();
    private readonly AccountService _accounts;

    public AccountServiceTests() => _accounts = new AccountService(_users, TestJwt.CreateTokenService());

    internal static InMemoryUserRepository CreateRepository(string? adminPassword = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Seed:AdminPassword"] = adminPassword })
            .Build();
        return new InMemoryUserRepository(config, NullLogger<InMemoryUserRepository>.Instance);
    }

    [Fact]
    public async Task Register_ThenLogin_Succeeds()
    {
        var registered = await _accounts.RegisterAsync("alice", "secret123");
        var login = await _accounts.LoginAsync("alice", "secret123");

        Assert.Equal(AuthStatus.Success, registered.Status);
        Assert.NotNull(registered.Tokens);
        Assert.Equal(AuthStatus.Success, login.Status);
        Assert.NotEqual(registered.Tokens!.RefreshToken, login.Tokens!.RefreshToken);
    }

    [Fact]
    public async Task Register_NewUser_GetsPlayerRole()
    {
        await _accounts.RegisterAsync("alice", "secret123");

        Assert.Equal(Roles.Player, (await _users.GetByUsernameAsync("alice"))!.Role);
    }

    [Fact]
    public async Task Register_UsernameTakenIgnoringCase_ReturnsUsernameTaken()
    {
        await _accounts.RegisterAsync("alice", "secret123");

        var result = await _accounts.RegisterAsync("ALICE", "other-password");

        Assert.Equal(AuthStatus.UsernameTaken, result.Status);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task Register_TrimsUsername()
    {
        await _accounts.RegisterAsync("  bob  ", "secret123");

        Assert.Equal(AuthStatus.Success, (await _accounts.LoginAsync("bob", "secret123")).Status);
    }

    [Fact]
    public async Task Register_StoresOnlyHashesOfPasswordAndRefreshToken()
    {
        var tokens = (await _accounts.RegisterAsync("alice", "secret123")).Tokens!;
        var user = (await _users.GetByUsernameAsync("alice"))!;

        Assert.NotEqual("secret123", user.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify("secret123", user.PasswordHash));
        Assert.Equal(TokenService.Hash(tokens.RefreshToken), user.RefreshTokenHash);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsInvalidCredentials()
    {
        await _accounts.RegisterAsync("alice", "secret123");

        var result = await _accounts.LoginAsync("alice", "wrong-password");

        Assert.Equal(AuthStatus.InvalidCredentials, result.Status);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task Login_UnknownUser_ReturnsInvalidCredentials()
    {
        var result = await _accounts.LoginAsync("ghost", "secret123");

        Assert.Equal(AuthStatus.InvalidCredentials, result.Status);
    }

    [Fact]
    public async Task Refresh_RotatesToken_OldRefreshTokenIsRejected()
    {
        var first = (await _accounts.RegisterAsync("alice", "secret123")).Tokens!;

        var second = await _accounts.RefreshAsync(first.RefreshToken);
        var reused = await _accounts.RefreshAsync(first.RefreshToken);
        var third = await _accounts.RefreshAsync(second.Tokens!.RefreshToken);

        Assert.Equal(AuthStatus.Success, second.Status);
        Assert.Equal(AuthStatus.InvalidRefreshToken, reused.Status);
        Assert.Equal(AuthStatus.Success, third.Status);
    }

    [Fact]
    public async Task Refresh_ExpiredToken_ReturnsInvalidRefreshToken()
    {
        var tokens = (await _accounts.RegisterAsync("alice", "secret123")).Tokens!;
        (await _users.GetByUsernameAsync("alice"))!.RefreshTokenExpiresUtc = DateTime.UtcNow.AddSeconds(-1);

        var result = await _accounts.RefreshAsync(tokens.RefreshToken);

        Assert.Equal(AuthStatus.InvalidRefreshToken, result.Status);
    }

    [Fact]
    public async Task Refresh_UnknownToken_ReturnsInvalidRefreshToken()
    {
        var result = await _accounts.RefreshAsync(TokenService.CreateRefreshToken());

        Assert.Equal(AuthStatus.InvalidRefreshToken, result.Status);
    }
}

public class TokenServiceTests
{
    private static readonly User Alice = new()
    {
        Id = Guid.NewGuid(),
        Username = "alice",
        PasswordHash = "-",
        Role = Roles.Admin
    };

    [Fact]
    public async Task CreateAccessToken_IsAcceptedBySharedValidationRules_WithExpectedClaims()
    {
        var (token, expiresAt) = TestJwt.CreateTokenService().CreateAccessToken(Alice);

        var result = await new JsonWebTokenHandler()
            .ValidateTokenAsync(token, TestJwt.CreateOptions().CreateValidationParameters());

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(Alice.Id.ToString(), result.ClaimsIdentity.FindFirst("sub")?.Value);
        Assert.Equal("alice", result.ClaimsIdentity.Name);
        Assert.Equal(Roles.Admin, result.ClaimsIdentity.FindFirst("role")?.Value);
        Assert.InRange(expiresAt, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
    }

    [Fact]
    public async Task CreateAccessToken_IsRejectedByServiceWithDifferentSecret()
    {
        var (token, _) = TestJwt.CreateTokenService().CreateAccessToken(Alice);
        var otherOptions = TestJwt.CreateOptions("another-secret-that-is-long-enough-0123456789");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, otherOptions.CreateValidationParameters());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RefreshTokenLifetime_FollowsConfiguredDays()
    {
        var service = new TokenService(Options.Create(new JwtOptions { Secret = TestJwt.Secret, RefreshTokenDays = 3 }));

        Assert.Equal(TimeSpan.FromDays(3), service.RefreshTokenLifetime);
    }

    [Fact]
    public void CreateRefreshToken_Is32RandomBytes()
    {
        var a = TokenService.CreateRefreshToken();
        var b = TokenService.CreateRefreshToken();

        Assert.Equal(32, Convert.FromBase64String(a).Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_IsDeterministicSha256Hex()
    {
        var hash = TokenService.Hash("token");

        Assert.Equal(hash, TokenService.Hash("token"));
        Assert.NotEqual(hash, TokenService.Hash("token2"));
        Assert.Matches("^[0-9A-F]{64}$", hash);
    }
}

public class InMemoryUserRepositoryTests
{
    [Fact]
    public async Task SeedAdminPassword_CreatesAdminUser()
    {
        var users = AccountServiceTests.CreateRepository(adminPassword: "admin-pw");

        var admin = await users.GetByUsernameAsync("ADMIN");

        Assert.NotNull(admin);
        Assert.Equal(Roles.Admin, admin.Role);
        Assert.True(BCrypt.Net.BCrypt.Verify("admin-pw", admin.PasswordHash));
    }

    [Fact]
    public async Task NoSeedAdminPassword_DoesNotCreateAdminUser()
    {
        var users = AccountServiceTests.CreateRepository(adminPassword: "");

        Assert.Null(await users.GetByUsernameAsync("admin"));
    }

    [Fact]
    public async Task TryAdd_DuplicateUsernameIgnoringCase_ReturnsFalse()
    {
        var users = AccountServiceTests.CreateRepository();

        Assert.True(await users.TryAddAsync(new User { Username = "alice", PasswordHash = "-", Role = Roles.Player }));
        Assert.False(await users.TryAddAsync(new User { Username = "Alice", PasswordHash = "-", Role = Roles.Player }));
    }

    [Fact]
    public async Task GetByRefreshTokenHash_FindsUserAfterUpdate()
    {
        var users = AccountServiceTests.CreateRepository();
        var user = new User { Username = "alice", PasswordHash = "-", Role = Roles.Player };
        await users.TryAddAsync(user);

        user.RefreshTokenHash = "ABC";
        await users.UpdateAsync(user);

        Assert.Same(user, await users.GetByRefreshTokenHashAsync("ABC"));
        Assert.Null(await users.GetByRefreshTokenHashAsync("DEF"));
    }
}

public class JwtExtensionsTests
{
    private static IServiceCollection Configure(string? secret)
    {
        var settings = new Dictionary<string, string?>();
        if (secret is not null) settings["Jwt:Secret"] = secret;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddGameJwtAuthentication(config);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short-secret")]
    public void MissingOrShortSecret_FailsAtStartup(string? secret)
    {
        Assert.Throws<InvalidOperationException>(() => Configure(secret));
    }

    [Fact]
    public void SecretKeyPresentWithNullValue_FailsAtStartupWithClearError()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Secret"] = null })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddGameJwtAuthentication(config));
        Assert.Contains("Jwt:Secret", ex.Message);
    }

    [Fact]
    public void ValidSecret_BindsJwtOptions()
    {
        using var provider = Configure(TestJwt.Secret).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<JwtOptions>>().Value;

        Assert.Equal(TestJwt.Secret, options.Secret);
        Assert.Equal("online-arena-auth", options.Issuer);
    }
}
