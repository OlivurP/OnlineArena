using AuthService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts;

namespace AuthService.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAccountService accounts) : ControllerBase
{
    [HttpPost("register")]
    [EndpointSummary("Opret spiller")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var result = await accounts.RegisterAsync(request.Username, request.Password);
        return result.Status == AuthStatus.Success
            ? Ok(result.Tokens)
            : Problem(statusCode: StatusCodes.Status409Conflict, title: "Brugernavnet er optaget");
    }

    [HttpPost("login")]
    [EndpointSummary("Log ind og få JWT + refresh token")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var result = await accounts.LoginAsync(request.Username, request.Password);
        return result.Status == AuthStatus.Success
            ? Ok(result.Tokens)
            : Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Forkert brugernavn eller adgangskode");
    }

    [HttpPost("refresh")]
    [EndpointSummary("Byt refresh token til nyt tokenpar (rotation)")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(RefreshRequest request)
    {
        var result = await accounts.RefreshAsync(request.RefreshToken);
        return result.Status == AuthStatus.Success
            ? Ok(result.Tokens)
            : Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Ugyldig eller udløbet refresh token");
    }

    [Authorize]
    [HttpGet("me")]
    [EndpointSummary("Hvem er jeg?")]
    public IActionResult Me() => Ok(new
    {
        id = User.FindFirst("sub")?.Value,
        name = User.Identity?.Name,
        role = User.FindFirst("role")?.Value
    });
}
