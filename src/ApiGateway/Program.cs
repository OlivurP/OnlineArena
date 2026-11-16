using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Shared.Auth;
using Shared.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Gatewayen validerer JWT (hvis til stede) for at kunne rate-limite pr. bruger.
// Services validerer selv tokenet igen (defense in depth).
builder.Services.AddGameJwtAuthentication(builder.Configuration);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
        }
        return ValueTask.CompletedTask;
    };

    // Login/registrering: lav grænse pr. IP (beskytter mod brute force)
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    // Øvrige endpoints: sliding window pr. bruger (sub), ellers pr. IP
    options.AddPolicy("per-user", context => RateLimitPartition.GetSlidingWindowLimiter(
        context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        }));
});

builder.Services.AddGameHealthChecks();

var app = builder.Build();

app.UseCorrelationId();
app.UseAuthentication();
app.UseRateLimiter();
app.MapReverseProxy();
app.MapGameHealthEndpoints();

app.Run();
