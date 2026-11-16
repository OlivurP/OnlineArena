using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Shared.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "online-arena-auth";
    public string Audience { get; set; } = "online-arena";
    public string Secret { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;

    public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(Secret));

    /// <summary>
    /// Samme valideringsregler bruges af alle services (og af gameserveren ved UDP-join),
    /// så et token udstedt af AuthService kan valideres stateless overalt.
    /// </summary>
    public TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = CreateSigningKey(),
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "name",
        RoleClaimType = "role"
    };
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Player = "Player";
}
