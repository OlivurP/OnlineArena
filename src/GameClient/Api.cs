using System.Net;
using System.Net.Http.Json;
using Shared.Contracts;

namespace GameClient;

internal static class Api
{
    public static async Task<TokenResponse?> LoginOrRegisterAsync(HttpClient http, string username, string password)
    {
        var response = await http.PostAsJsonAsync("api/auth/login", new LoginRequest(username, password));

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Console.WriteLine("Login fejlede – forsøger at oprette brugeren...");
            response = await http.PostAsJsonAsync("api/auth/register", new RegisterRequest(username, password));
        }

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Fejl {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return null;
        }

        return await response.Content.ReadFromJsonAsync<TokenResponse>();
    }

    public static async Task<TicketResponse> JoinQueueAsync(HttpClient http)
    {
        var response = await http.PostAsync("api/matchmaking/queue", content: null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TicketResponse>())!;
    }

    public static async Task<TicketResponse> GetTicketAsync(HttpClient http, Guid ticketId) =>
        (await http.GetFromJsonAsync<TicketResponse>($"api/matchmaking/tickets/{ticketId}"))!;
}

internal static class ConsoleHelpers
{
    public static string ReadPassword()
    {
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }
}
