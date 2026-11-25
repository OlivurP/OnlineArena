using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Auth;

public static class JwtExtensions
{
    public static IServiceCollection AddGameJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(JwtOptions.Section);
        services.Configure<JwtOptions>(section);

        var options = section.Get<JwtOptions>() ?? new JwtOptions();
        // Secret kan være null, hvis nøglen findes i konfigurationen uden værdi (fx "Jwt__Secret=")
        if (options.Secret is null || options.Secret.Length < 32)
        {
            throw new InvalidOperationException(
                "Jwt:Secret mangler eller er kortere end 32 tegn. Sæt den via miljøvariablen Jwt__Secret.");
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false; // behold "sub", "name", "role" som de er
                o.TokenValidationParameters = options.CreateValidationParameters();
            });

        services.AddAuthorization();
        return services;
    }
}
