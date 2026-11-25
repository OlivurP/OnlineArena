using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using OnlineArena.Tests.Infrastructure;
using Shared.Contracts;
using Shared.Hosting;
using Shared.Protocol;
using StatsService.Domain;

namespace OnlineArena.Tests.E2E;

/// <summary>
/// Kører mod hele systemet startet med docker compose (se tests/e2e/docker-compose.e2e.yml).
/// Kræver E2E_GATEWAY_URL, fx http://localhost:8080 – ellers springes testene over.
/// Bemærk: auth-ruten er rate-limited til 10 kald/min pr. IP, så testene registrerer få brugere.
/// </summary>
[Trait("Category", TestCategories.E2E)]
public sealed class EndToEndTests : IDisposable
{
    private readonly HttpClient _gateway = new() { BaseAddress = new Uri(E2EFactAttribute.GatewayUrl ?? "http://localhost:8080") };

    [E2EFact]
    public async Task TwoPlayers_RegisterQueuePlayAMatch_AndStatsAreUpdated()
    {
        var names = new[] { NewUsername(), NewUsername() };
        var players = new List<Player>();
        foreach (var name in names) players.Add(await RegisterAsync(name));

        // Matchmaking: begge spillere i kø, og de placeres på samme gameserver
        foreach (var player in players)
        {
            var response = await player.Http.PostAsync("/api/matchmaking/queue", null);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            player.TicketId = (await response.Content.ReadFromJsonAsync<TicketResponse>())!.TicketId;
        }
        foreach (var player in players) player.Ticket = await WaitForMatchAsync(player);
        Assert.Equal(players[0].Ticket!.ServerPort, players[1].Ticket!.ServerPort);

        // Spil over UDP med JWT'en fra AuthService
        using var stopInputs = new CancellationTokenSource();
        var udpClients = new List<UdpTestClient>();
        try
        {
            foreach (var player in players)
            {
                var udp = new UdpTestClient(player.Ticket!.ServerHost!, player.Ticket.ServerPort!.Value);
                udpClients.Add(udp);
                var welcome = await JoinAsync(udp, player.AccessToken);
                Assert.False(string.IsNullOrEmpty(welcome.PlayerId));
            }
            var inputLoops = udpClients.Select(udp => SendInputsAsync(udp, stopInputs.Token)).ToList();

            var results = await udpClients[0].ReceiveAsync<MatchEndedMessage>(TimeSpan.FromSeconds(60));
            Assert.Equal(names.Order(), results.Results.Select(r => r.Username).Order());

            await stopInputs.CancelAsync();
            await Task.WhenAll(inputLoops);
        }
        finally
        {
            foreach (var udp in udpClients) await udp.DisposeAsync();
        }

        // Eventual consistency: MatchEnded går via RabbitMQ til StatsService og MatchmakingService
        var alice = players[0];
        PlayerStatsView? stats = null;
        await Eventually.UntilAsync(async () =>
        {
            var response = await alice.Http.GetAsync($"/api/stats/players/{names[0]}");
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            stats = await response.Content.ReadFromJsonAsync<PlayerStatsView>();
            return stats!.MatchesPlayed >= 1;
        }, TimeSpan.FromSeconds(30), "stats to be updated from the MatchEnded event");
        Assert.Equal(1, stats!.MatchesPlayed);

        await Eventually.UntilAsync(async () =>
        {
            var recent = await alice.Http.GetFromJsonAsync<List<RecentMatch>>("/api/matchmaking/recent-matches");
            return recent!.Any(m => m.Players == 2 && names.Contains(m.Winner));
        }, TimeSpan.FromSeconds(30), "the match to appear in recent matches");
    }

    [E2EFact]
    public async Task GameServer_RejectsTokenNotIssuedByAuthService()
    {
        await using var udp = new UdpTestClient("localhost", 7777);
        var forged = TestJwt.CreateAccessToken("mallory"); // signeret med testens hemmelighed, ikke stackens

        RejectedMessage? rejected = null;
        for (var attempt = 0; attempt < 5 && rejected is null; attempt++)
        {
            await udp.SendAsync(new JoinMessage(forged));
            rejected = await udp.TryReceiveAsync<RejectedMessage>(TimeSpan.FromSeconds(2));
        }

        Assert.NotNull(rejected);
    }

    [E2EFact]
    public async Task Gateway_RequiresAuthentication_ForMatchmaking()
    {
        var response = await _gateway.PostAsync("/api/matchmaking/queue", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [E2EFact]
    public async Task Gateway_DoesNotExposeInternalServerEndpoints()
    {
        var response = await _gateway.GetAsync("/api/servers");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [E2EFact]
    public async Task Gateway_EchoesCorrelationId()
    {
        var correlationId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stats/leaderboard");
        request.Headers.Add(HostingExtensions.CorrelationHeader, correlationId);

        var response = await _gateway.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues(HostingExtensions.CorrelationHeader)));
    }

    private async Task<Player> RegisterAsync(string username)
    {
        var response = await _gateway.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, "e2e-password-123"));
        Assert.True(response.IsSuccessStatusCode, $"Register failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;

        var http = new HttpClient { BaseAddress = _gateway.BaseAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return new Player(username, tokens.AccessToken, http);
    }

    private static async Task<TicketResponse> WaitForMatchAsync(Player player)
    {
        TicketResponse? ticket = null;
        await Eventually.UntilAsync(async () =>
        {
            ticket = await player.Http.GetFromJsonAsync<TicketResponse>($"/api/matchmaking/tickets/{player.TicketId}");
            return ticket!.Status != "Waiting";
        }, TimeSpan.FromSeconds(60), $"{player.Username} to be matched");
        Assert.Equal("Matched", ticket!.Status);
        return ticket;
    }

    /// <summary>UDP kan tabe pakker – gensend join indtil serveren svarer (som GameClient gør).</summary>
    private static async Task<WelcomeMessage> JoinAsync(UdpTestClient udp, string token)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await udp.SendAsync(new JoinMessage(token));
            if (await udp.TryReceiveAsync<WelcomeMessage>(TimeSpan.FromSeconds(1)) is { } welcome) return welcome;
        }
        throw new TimeoutException("The game server did not answer the join request.");
    }

    /// <summary>Holder sessionen i live (client timeout) og bevæger spilleren rundt.</summary>
    private static async Task SendInputsAsync(UdpTestClient udp, CancellationToken ct)
    {
        var sequence = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var angle = sequence * 0.3;
                await udp.SendAsync(new InputMessage(++sequence, (float)Math.Cos(angle), (float)Math.Sin(angle)));
                await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string NewUsername() => $"e2e{Guid.NewGuid():N}"[..16];

    public void Dispose() => _gateway.Dispose();

    private sealed class Player(string username, string accessToken, HttpClient http)
    {
        public string Username { get; } = username;
        public string AccessToken { get; } = accessToken;
        public HttpClient Http { get; } = http;
        public Guid TicketId { get; set; }
        public TicketResponse? Ticket { get; set; }
    }

    private sealed record RecentMatch(Guid MatchId, string ServerId, string? Winner, int Players, DateTime EndedAtUtc);
}
