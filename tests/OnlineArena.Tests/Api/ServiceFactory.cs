using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared.Auth;

namespace OnlineArena.Tests.Api;

/// <summary>
/// Starts a service in-memory with test configuration. Background services from the
/// application (RabbitMQ consumers, health-check pollers, workers) are removed so the
/// tests need no external infrastructure and run in the plain `dotnet test` CI step.
/// </summary>
/// <typeparam name="TEntryPoint">Any public type from the service assembly, e.g. its controller.</typeparam>
public sealed class ServiceFactory<TEntryPoint> : WebApplicationFactory<TEntryPoint> where TEntryPoint : class
{
    public const string InternalApiKey = "test-internal-key";
    public const string AdminPassword = "admin-test-password";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Jwt:Secret", TestJwt.Secret);
        builder.UseSetting("Internal:ApiKey", InternalApiKey);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);

        builder.ConfigureTestServices(services =>
        {
            var appAssemblies = new[] { typeof(TEntryPoint).Assembly, typeof(JwtOptions).Assembly };
            var appHostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService) && !d.IsKeyedService &&
                            (d.ImplementationType is null || appAssemblies.Contains(d.ImplementationType.Assembly)))
                .ToList();
            foreach (var descriptor in appHostedServices) services.Remove(descriptor);
        });
    }

    public HttpClient CreateClientFor(string username, string role = Roles.Player)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwt.CreateAccessToken(username, role));
        return client;
    }

    public static string NewUsername() => "u" + Guid.NewGuid().ToString("N")[..10];
}
