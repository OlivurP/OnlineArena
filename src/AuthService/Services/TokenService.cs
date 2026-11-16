using System.Security.Cryptography;
using System.Text;
using AuthService.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shared.Auth;

namespace AuthService.Services;

public sealed class TokenService(IOptions<JwtOptions> options)
{
    private readonly JsonWebTokenHandler _handler = new();

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(options.Value.RefreshTokenDays);

    /// <summary>Kortlivet JWT med sub, name og role. Kan valideres af alle services uden databaseopslag.</summary>
    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(User user)
    {
        var o = options.Value;
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(o.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(o.CreateSigningKey(), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id.ToString(),
                ["name"] = user.Username,
                ["role"] = user.Role
            }
        };

        return (_handler.CreateToken(descriptor), expires);
    }

    public static string CreateRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Kun hashen gemmes – et læk af databasen giver ikke brugbare refresh tokens.</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
