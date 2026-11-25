using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthService.Controllers;
using Shared.Auth;
using Shared.Contracts;
using Shared.Hosting;

namespace OnlineArena.Tests.Api;

public class AuthApiTests(ServiceFactory<AuthController> factory) : IClassFixture<ServiceFactory<AuthController>>
{
    private sealed record MeResponse(string Id, string Name, string Role);

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<TokenResponse> RegisterAsync(string username, string password = "secret123")
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private async Task<HttpResponseMessage> GetMeAsync(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task Register_ReturnsTokens_ThatAuthenticateMe()
    {
        var username = ServiceFactory<AuthController>.NewUsername();

        var tokens = await RegisterAsync(username);
        var me = await (await GetMeAsync(tokens.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();

        Assert.Equal(username, me!.Name);
        Assert.Equal(Roles.Player, me.Role);
        Assert.True(Guid.TryParse(me.Id, out _));
    }

    [Fact]
    public async Task Register_ExistingUsername_Returns409()
    {
        var username = ServiceFactory<AuthController>.NewUsername();
        await RegisterAsync(username);

        var response = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username.ToUpperInvariant(), "secret123"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("ab", "secret123")]                       // brugernavn for kort
    [InlineData("averyveryverylongusername", "secret123")] // brugernavn for langt
    [InlineData("validname", "12345")]                     // adgangskode for kort
    public async Task Register_InvalidInput_Returns400(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_CorrectPassword_Returns200()
    {
        var username = ServiceFactory<AuthController>.NewUsername();
        await RegisterAsync(username);

        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, "secret123"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var username = ServiceFactory<AuthController>.NewUsername();
        await RegisterAsync(username);

        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SeededAdmin_CanLogIn_WithAdminRole()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("admin", ServiceFactory<AuthController>.AdminPassword));
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();

        var me = await (await GetMeAsync(tokens!.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();

        Assert.Equal(Roles.Admin, me!.Role);
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndRejectsReuseOfOldToken()
    {
        var tokens = await RegisterAsync(ServiceFactory<AuthController>.NewUsername());

        var first = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        var reuse = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tokens.RefreshToken));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task Me_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithTamperedToken_Returns401()
    {
        var tokens = await RegisterAsync(ServiceFactory<AuthController>.NewUsername());

        var response = await GetMeAsync(tokens.AccessToken[..^2] + "xx");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_Return200(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CorrelationId_IsEchoedBack()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(HostingExtensions.CorrelationHeader, "test-correlation-id");

        var response = await _client.SendAsync(request);

        Assert.Equal("test-correlation-id", Assert.Single(response.Headers.GetValues(HostingExtensions.CorrelationHeader)));
    }

    [Fact]
    public async Task CorrelationId_IsGeneratedWhenMissing()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(response.Headers.GetValues(HostingExtensions.CorrelationHeader))));
    }
}
