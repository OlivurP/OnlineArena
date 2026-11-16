using System.Net.Http.Headers;
using GameClient;

// Brug: dotnet run --project src/GameClient [gateway-url]
var gatewayUrl = args.Length > 0 ? args[0] : "http://localhost:8080";
using var http = new HttpClient { BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/") };

Console.WriteLine($"=== Online Arena ===  (gateway: {gatewayUrl})");
Console.Write("Brugernavn: ");
var username = Console.ReadLine()?.Trim() ?? string.Empty;
Console.Write("Adgangskode: ");
var password = ConsoleHelpers.ReadPassword();

try
{
    var tokens = await Api.LoginOrRegisterAsync(http, username, password);
    if (tokens is null) return;
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

    var ticket = await Api.JoinQueueAsync(http);
    Console.WriteLine("I kø – venter på modspiller (start en klient mere)...");
    while (ticket.Status == "Waiting")
    {
        await Task.Delay(1000);
        ticket = await Api.GetTicketAsync(http, ticket.TicketId);
    }

    if (ticket.Status != "Matched" || ticket.ServerHost is null || ticket.ServerPort is null)
    {
        Console.WriteLine($"Matchmaking afsluttet med status: {ticket.Status}");
        return;
    }

    Console.WriteLine($"Match fundet på {ticket.ServerHost}:{ticket.ServerPort}");
    var session = new GameSession(ticket.ServerHost, ticket.ServerPort.Value, tokens.AccessToken);
    await session.RunAsync();
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"Kunne ikke kontakte gatewayen: {ex.Message}");
}
