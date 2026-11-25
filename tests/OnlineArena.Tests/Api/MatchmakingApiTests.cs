using System.Net;
using System.Net.Http.Json;
using MatchmakingService.Controllers;
using MatchmakingService.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shared.Auth;
using Shared.Contracts;

namespace OnlineArena.Tests.Api;

public class MatchmakingApiTests(ServiceFactory<MatchmakingController> factory)
    : IClassFixture<ServiceFactory<MatchmakingController>>
{
    private static string NewUsername() => ServiceFactory<MatchmakingController>.NewUsername();

    private static RegisterServerRequest NewServer(string id) =>
        new(id, "localhost", 7777, $"http://{id}:8080/health/ready", 8);

    private HttpClient CreateInternalClient(string apiKey = ServiceFactory<MatchmakingController>.InternalApiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(InternalApiKeyAttribute.HeaderName, apiKey);
        return client;
    }

    [Fact]
    public async Task Queue_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().PostAsync("/api/matchmaking/queue", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Queue_WithToken_Returns202WithLocationOfTicket()
    {
        var client = factory.CreateClientFor(NewUsername());

        var response = await client.PostAsync("/api/matchmaking/queue", null);
        var ticket = await response.Content.ReadFromJsonAsync<TicketResponse>();
        var polled = await client.GetFromJsonAsync<TicketResponse>(response.Headers.Location);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(ticket!.TicketId, polled!.TicketId);
        Assert.Equal(nameof(TicketStatus.Waiting), polled.Status);
    }

    [Fact]
    public async Task GetTicket_OfAnotherPlayer_Returns404()
    {
        var owner = factory.CreateClientFor(NewUsername());
        var ticket = await (await owner.PostAsync("/api/matchmaking/queue", null)).Content.ReadFromJsonAsync<TicketResponse>();

        var response = await factory.CreateClientFor(NewUsername()).GetAsync($"/api/matchmaking/tickets/{ticket!.TicketId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_OwnTicket_Returns204AndTicketIsCancelled()
    {
        var client = factory.CreateClientFor(NewUsername());
        var ticket = await (await client.PostAsync("/api/matchmaking/queue", null)).Content.ReadFromJsonAsync<TicketResponse>();

        var response = await client.DeleteAsync($"/api/matchmaking/tickets/{ticket!.TicketId}");
        var polled = await client.GetFromJsonAsync<TicketResponse>($"/api/matchmaking/tickets/{ticket.TicketId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(nameof(TicketStatus.Cancelled), polled!.Status);
    }

    [Fact]
    public async Task Cancel_AnotherPlayersTicket_Returns404()
    {
        var owner = factory.CreateClientFor(NewUsername());
        var ticket = await (await owner.PostAsync("/api/matchmaking/queue", null)).Content.ReadFromJsonAsync<TicketResponse>();

        var response = await factory.CreateClientFor(NewUsername()).DeleteAsync($"/api/matchmaking/tickets/{ticket!.TicketId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RecentMatches_IsPublic()
    {
        var response = await factory.CreateClient().GetAsync("/api/matchmaking/recent-matches");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RegisterServer_WithoutApiKey_Returns401()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/servers/register", NewServer("gs-nokey"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegisterServer_WithWrongApiKey_Returns401()
    {
        var response = await CreateInternalClient("wrong-key").PostAsJsonAsync("/api/servers/register", NewServer("gs-wrongkey"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegisterServer_PlayerJwtIsNotEnough_Returns401()
    {
        var response = await factory.CreateClientFor(NewUsername()).PostAsJsonAsync("/api/servers/register", NewServer("gs-jwt"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegisterServer_WithApiKey_Returns204_AndHeartbeatIsAccepted()
    {
        var client = CreateInternalClient();

        var register = await client.PostAsJsonAsync("/api/servers/register", NewServer("gs-key"));
        var heartbeat = await client.PostAsJsonAsync("/api/servers/gs-key/heartbeat", new HeartbeatRequest(1));

        Assert.Equal(HttpStatusCode.NoContent, register.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, heartbeat.StatusCode);
    }

    [Fact]
    public async Task RegisterServer_InvalidRequest_Returns400()
    {
        var response = await CreateInternalClient().PostAsJsonAsync("/api/servers/register",
            new RegisterServerRequest("gs-bad", "localhost", 0, "not-a-url", 8));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_UnknownServer_Returns404SoServerReRegisters()
    {
        var response = await CreateInternalClient().PostAsJsonAsync("/api/servers/unknown/heartbeat", new HeartbeatRequest(0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetServers_AsPlayer_Returns403()
    {
        var response = await factory.CreateClientFor(NewUsername()).GetAsync("/api/servers");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetServers_AsAdmin_Returns200()
    {
        var response = await factory.CreateClientFor("admin", Roles.Admin).GetAsync("/api/servers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_Returns200_EvenWithoutRabbitMq()
    {
        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TwoPlayers_AreMatchedToRegisteredHealthyServer()
    {
        // Egen instans, så en sund server ikke påvirker de andre tests i klassen
        await using var isolated = new ServiceFactory<MatchmakingController>();
        var internalClient = isolated.CreateClient();
        internalClient.DefaultRequestHeaders.Add(InternalApiKeyAttribute.HeaderName, ServiceFactory<MatchmakingController>.InternalApiKey);
        await internalClient.PostAsJsonAsync("/api/servers/register", NewServer("gs-flow"));

        // Simulerer HealthCheckInvoker, som er fjernet i testene
        isolated.Services.GetRequiredService<ServerRegistry>().ReportHealth("gs-flow", healthy: true);

        var alice = isolated.CreateClientFor(NewUsername());
        var bob = isolated.CreateClientFor(NewUsername());
        var aliceTicket = await (await alice.PostAsync("/api/matchmaking/queue", null)).Content.ReadFromJsonAsync<TicketResponse>();
        var bobTicket = await (await bob.PostAsync("/api/matchmaking/queue", null)).Content.ReadFromJsonAsync<TicketResponse>();
        var alicePolled = await alice.GetFromJsonAsync<TicketResponse>($"/api/matchmaking/tickets/{aliceTicket!.TicketId}");

        Assert.Equal(nameof(TicketStatus.Matched), bobTicket!.Status);
        Assert.Equal(nameof(TicketStatus.Matched), alicePolled!.Status);
        Assert.Equal(("localhost", 7777), (alicePolled.ServerHost, alicePolled.ServerPort));
    }
}
