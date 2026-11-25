using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shared.Auth;
using Shared.Hosting;

namespace OnlineArena.Tests;

public class InternalApiKeyAttributeTests
{
    private static async Task<(ActionExecutingContext Context, bool NextCalled)> RunFilterAsync(
        string? configuredKey, string? providedKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:ApiKey"] = configuredKey })
            .Build();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider()
        };
        if (providedKey is not null) httpContext.Request.Headers[InternalApiKeyAttribute.HeaderName] = providedKey;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());

        var nextCalled = false;
        await new InternalApiKeyAttribute().OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
        });

        return (context, nextCalled);
    }

    [Fact]
    public async Task CorrectKey_CallsAction()
    {
        var (context, nextCalled) = await RunFilterAsync("internal-key", "internal-key");

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Theory]
    [InlineData("internal-key", null)]          // header mangler
    [InlineData("internal-key", "wrong-key")]   // forkert nøgle
    [InlineData("internal-key", "internal-ke")] // forkert længde
    [InlineData("", "")]                        // ikke konfigureret => ingen tom nøgle accepteres
    [InlineData(null, "anything")]
    public async Task MissingOrInvalidKey_Returns401WithoutCallingAction(string? configuredKey, string? providedKey)
    {
        var (context, nextCalled) = await RunFilterAsync(configuredKey, providedKey);

        Assert.False(nextCalled);
        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }
}

public class ReadinessHealthCheckTests
{
    private sealed record Signal(string Name, bool IsReady) : IReadinessSignal;

    private static Task<HealthCheckResult> CheckAsync(params IReadinessSignal[] signals) =>
        new ReadinessHealthCheck(signals).CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task NoSignals_IsHealthy()
    {
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync()).Status);
    }

    [Fact]
    public async Task AllSignalsReady_IsHealthy()
    {
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(new Signal("a", true), new Signal("b", true))).Status);
    }

    [Fact]
    public async Task SignalNotReady_IsUnhealthyAndNamesTheDependency()
    {
        var result = await CheckAsync(new Signal("a", true), new Signal("rabbitmq:stats", false));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("rabbitmq:stats", result.Description);
    }
}
