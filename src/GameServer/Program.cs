using GameServer;
using GameServer.Game;
using GameServer.Networking;
using GameServer.Workers;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Shared.Auth;
using Shared.Hosting;
using Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GameServerOptions>(builder.Configuration.GetSection(GameServerOptions.Section));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<ServerState>();
builder.Services.AddSingleton<IReadinessSignal>(sp => sp.GetRequiredService<ServerState>());

// RabbitMQ er bevidst IKKE en readiness-afhængighed: spillet skal kunne køre, selvom brokeren er nede.
// Events buffers i outboxen og sendes, når forbindelsen er tilbage (AP frem for CP).
builder.Services.AddSingleton<EventOutbox>();
builder.Services.AddSingleton<EventPublisher>();

builder.Services.AddHostedService<GameLoop>();
builder.Services.AddHostedService<OutboxRelay>();
builder.Services.AddHostedService<RegistrationWorker>();

// Typed client med resilience pipeline (Polly): retry m. exponential backoff + jitter,
// circuit breaker og timeout pr. forsøg.
builder.Services.AddHttpClient<MatchmakingClient>((sp, client) =>
    {
        var options = builder.Configuration.GetSection(GameServerOptions.Section).Get<GameServerOptions>() ?? new();
        client.BaseAddress = new Uri(options.MatchmakingUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add(InternalApiKeyAttribute.HeaderName, builder.Configuration["Internal:ApiKey"]);
        client.Timeout = TimeSpan.FromSeconds(60); // dækker hele operationen inkl. retries
    })
    .AddResilienceHandler("matchmaking", pipeline =>
    {
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 4,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        });
        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.9,
            MinimumThroughput = 5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15)
        });
        pipeline.AddTimeout(TimeSpan.FromSeconds(3));
    });

builder.Services.AddGameHealthChecks();

var app = builder.Build();
app.MapGameHealthEndpoints();
app.Run();
