using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EeveeCore.AuthHandlers;

/// <summary>
///     Authenticates the dashboard by the shared key in the <c>X-API-Key</c> header.
/// </summary>
/// <param name="options">The authentication scheme options.</param>
/// <param name="logger">The logger factory.</param>
/// <param name="encoder">The URL encoder.</param>
/// <param name="apiKeyValidation">The API key validation service.</param>
public class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyValidation apiKeyValidation)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiConstants.HeaderName, out var apiKeyHeaderValues))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!apiKeyValidation.IsValidApiKey(apiKeyHeaderValues.ToString()))
            return Task.FromResult(AuthenticateResult.Fail(
                $"Invalid API Key ({Request.Method} {Request.Path} from {Context.Connection.RemoteIpAddress})"));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "ApiUser")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity),
            Scheme.Name)));
    }
}
