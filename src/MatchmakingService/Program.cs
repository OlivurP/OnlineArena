using MatchmakingService.Domain;
using MatchmakingService.Workers;
using Scalar.AspNetCore;
using Shared.Auth;
using Shared.Hosting;
using Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MatchmakingOptions>(builder.Configuration.GetSection(MatchmakingOptions.Section));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<ServerRegistry>();
builder.Services.AddSingleton<MatchmakingQueue>();
builder.Services.AddSingleton<MatchHistory>();

builder.Services.AddHostedService<HealthCheckInvoker>();
builder.Services.AddHostedService<MatchmakerWorker>();
// Bevidst IKKE en readiness-afhængighed: matchmaking virker selvom RabbitMQ er nede
// (kun match-historikken bliver forsinket). Tilgængelighed prioriteres.
builder.Services.AddHostedService<MatchEndedConsumer>();

builder.Services.AddHttpClient(HealthCheckInvoker.ClientName, c => c.Timeout = TimeSpan.FromSeconds(2));

builder.Services.AddGameJwtAuthentication(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddGameHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCorrelationId();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGameHealthEndpoints();

app.Run();
