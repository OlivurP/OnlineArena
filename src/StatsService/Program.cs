using Scalar.AspNetCore;
using Shared.Auth;
using Shared.Hosting;
using Shared.Messaging;
using StatsService.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
builder.Services.AddSingleton<StatsStore>();

// Consumeren er både hosted service og readiness-signal:
// Stats-servicens eneste datakilde er events, så uden RabbitMQ er den "not ready".
builder.Services.AddSingleton<MatchEndedConsumer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MatchEndedConsumer>());
builder.Services.AddSingleton<IReadinessSignal>(sp => sp.GetRequiredService<MatchEndedConsumer>());

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
