using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Shared.Contracts;
using StatsService.Controllers;
using StatsService.Domain;

namespace OnlineArena.Tests.Api;

public class StatsApiTests(ServiceFactory<StatsController> factory) : IClassFixture<ServiceFactory<StatsController>>
{
    private void SeedMatch(params PlayerResult[] results) =>
        factory.Services.GetRequiredService<StatsStore>()
            .Apply(new MatchEnded(Guid.NewGuid(), Guid.NewGuid(), "gs-1", results, DateTime.UtcNow));

    [Fact]
    public async Task Leaderboard_IsPublic_AndOrderedByWins()
    {
        var winner = ServiceFactory<StatsController>.NewUsername();
        var loser = ServiceFactory<StatsController>.NewUsername();
        SeedMatch(new(winner, 10), new(loser, 2));
        SeedMatch(new(winner, 8), new(loser, 7));

        var leaderboard = await factory.CreateClient()
            .GetFromJsonAsync<List<PlayerStatsView>>("/api/stats/leaderboard?top=100");

        var ranked = leaderboard!.Select(p => p.Username).Where(u => u == winner || u == loser).ToList();
        Assert.Equal([winner, loser], ranked);
    }

    [Fact]
    public async Task Player_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/stats/players/alice");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Player_Unknown_Returns404()
    {
        var response = await factory.CreateClientFor("alice").GetAsync($"/api/stats/players/{ServiceFactory<StatsController>.NewUsername()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Player_Known_ReturnsStats()
    {
        var username = ServiceFactory<StatsController>.NewUsername();
        SeedMatch(new(username, 5), new(ServiceFactory<StatsController>.NewUsername(), 1));

        var stats = await factory.CreateClientFor("alice").GetFromJsonAsync<PlayerStatsView>($"/api/stats/players/{username}");

        Assert.Equal(new PlayerStatsView(username, 1, 1, 5, 5), stats);
    }

    [Fact]
    public async Task Live_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_Returns503_WhenRabbitMqConsumerIsNotConnected()
    {
        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
