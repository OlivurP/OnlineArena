using AuthService.Domain;
using AuthService.Services;
using Scalar.AspNetCore;
using Shared.Auth;
using Shared.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGameJwtAuthentication(builder.Configuration);
builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<IAccountService, AccountService>();
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
