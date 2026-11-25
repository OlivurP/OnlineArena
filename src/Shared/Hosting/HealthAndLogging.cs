using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Shared.Hosting;

/// <summary>En afhængighed der skal være klar, før servicen må modtage trafik (readiness).</summary>
public interface IReadinessSignal
{
    string Name { get; }
    bool IsReady { get; }
}

public sealed class ReadinessHealthCheck(IEnumerable<IReadinessSignal> signals) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var notReady = signals.Where(s => !s.IsReady).Select(s => s.Name).ToList();
        return Task.FromResult(notReady.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Ikke klar: " + string.Join(", ", notReady)));
    }
}

public static class HostingExtensions
{
    public const string CorrelationHeader = "X-Correlation-Id";

    public static IHealthChecksBuilder AddGameHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<ReadinessHealthCheck>("dependencies", tags: ["ready"]);

    /// <summary>
    /// /health/live  = processen kører (genstart hvis den fejler)
    /// /health/ready = servicen kan modtage trafik (fjern fra load balancing hvis den fejler)
    /// </summary>
    public static WebApplication MapGameHealthEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains("live") || c.Tags.Contains("ready")
        });
        return app;
    }

    /// <summary>
    /// Sikrer et correlation id på hvert request, sender det videre (gateway -> service) og
    /// lægger det i log-scope, så et request kan følges på tværs af services i de centrale logs.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Headers.TryGetValue(CorrelationHeader, out var id) || string.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString("N");
                context.Request.Headers[CorrelationHeader] = id;
            }

            // Sættes lige før svaret sendes, så headeren overskriver (ikke dubleres med) den,
            // som YARP kopierer med fra downstream-servicen i gatewayen.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[CorrelationHeader] = id;
                return Task.CompletedTask;
            });

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Correlation");
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id.ToString() }))
            {
                await next(context);
            }
        });
}
