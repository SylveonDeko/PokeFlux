using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EeveeCore.AuthHandlers;

/// <summary>
///     Verifies the HS256 user token the dashboard mints for each request.
/// </summary>
/// <param name="options">The authentication scheme options.</param>
/// <param name="logger">The logger factory.</param>
/// <param name="encoder">The URL encoder.</param>
/// <param name="credentials">The bot credentials holding the shared <c>JwtSecret</c>.</param>
public class DashJwtAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    BotCredentials credentials)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly JsonWebTokenHandler TokenHandler = new() { MapInboundClaims = false };

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        if (string.IsNullOrWhiteSpace(credentials.JwtSecret))
            return AuthenticateResult.Fail("JwtSecret is not configured");

        var result = await TokenHandler.ValidateTokenAsync(header["Bearer ".Length..].Trim(),
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = DashJwtConstants.Issuer,
                ValidateAudience = true,
                ValidAudience = DashJwtConstants.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(credentials.JwtSecret)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = DashJwtConstants.UserIdClaim
            });

        if (!result.IsValid)
            return AuthenticateResult.Fail(result.Exception?.Message ?? "Invalid token");

        var identity = new ClaimsIdentity(result.ClaimsIdentity.Claims, Scheme.Name, DashJwtConstants.UserIdClaim,
            null);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
