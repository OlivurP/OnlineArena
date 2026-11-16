using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Auth;

/// <summary>
/// Service-til-service-authentication med API key (fx gameserver -> matchmaking).
/// API keys identificerer en klient/integration – ikke en bruger.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InternalApiKeyAttribute : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "x-api-key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var expected = context.HttpContext.RequestServices
            .GetRequiredService<IConfiguration>()["Internal:ApiKey"];

        if (string.IsNullOrEmpty(expected)
            || !context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var provided)
            || !CryptographicOperations.FixedTimeEquals(
                   Encoding.UTF8.GetBytes(expected),
                   Encoding.UTF8.GetBytes(provided.ToString())))
        {
            context.Result = new UnauthorizedObjectResult("Manglende eller ugyldig intern API key");
            return;
        }

        await next();
    }
}
