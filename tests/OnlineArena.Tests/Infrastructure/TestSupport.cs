using System.Diagnostics;

namespace OnlineArena.Tests.Infrastructure;

public static class TestCategories
{
    /// <summary>Needs Docker (Testcontainers). Runs in the normal CI test step on ubuntu-latest.</summary>
    public const string Integration = "Integration";

    /// <summary>Needs the full docker compose stack. Runs in the dedicated e2e-test CI job.</summary>
    public const string E2E = "E2E";
}

/// <summary>
/// Runs only when Docker is available, so local test runs without Docker skip instead of fail.
/// Set REQUIRE_DOCKER_TESTS=true (as CI does) to make a missing Docker engine a test failure.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> DockerAvailable = new(IsDockerAvailable);

    public DockerFactAttribute()
    {
        if (!ShouldRun) Skip = "Docker is not available. Start Docker or set REQUIRE_DOCKER_TESTS=true.";
    }

    public static bool ShouldRun =>
        string.Equals(Environment.GetEnvironmentVariable("REQUIRE_DOCKER_TESTS"), "true", StringComparison.OrdinalIgnoreCase)
        || DockerAvailable.Value;

    private static bool IsDockerAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return false;
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                process.Kill();
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false; // docker CLI er ikke installeret
        }
    }
}

/// <summary>Runs only when E2E_GATEWAY_URL points at a running docker compose stack.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public const string GatewayVariable = "E2E_GATEWAY_URL";

    public static string? GatewayUrl => Environment.GetEnvironmentVariable(GatewayVariable);

    public E2EFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(GatewayUrl))
        {
            Skip = $"Set {GatewayVariable} (e.g. http://localhost:8080) to run against a docker compose stack.";
        }
    }
}

public static class Eventually
{
    public static Task UntilAsync(Func<bool> condition, TimeSpan timeout, string description) =>
        UntilAsync(() => Task.FromResult(condition()), timeout, description);

    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0}s waiting for {description}.");
            }
            await Task.Delay(100);
        }
    }
}
