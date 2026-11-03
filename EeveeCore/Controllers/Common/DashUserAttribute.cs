using EeveeCore.AuthHandlers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EeveeCore.Controllers.Common;

/// <summary>
///     Requires a valid dashboard user token and makes its Fluxer user id available to the action.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DashUserAttribute : Attribute, IAsyncAuthorizationFilter
{
    /// <summary>
    ///     The <see cref="HttpContext.Items" /> key the verified user id is stored under.
    /// </summary>
    public const string UserIdKey = "PokeFluxUserId";

    /// <summary>
    ///     The <see cref="HttpContext.Items" /> key the token's username is stored under.
    /// </summary>
    public const string UserNameKey = "PokeFluxUserName";

    /// <inheritdoc />
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var result = await context.HttpContext.AuthenticateAsync(DashJwtConstants.SchemeName);
        var principal = result.Succeeded ? result.Principal : null;

        if (principal?.FindFirst(DashJwtConstants.ScopeClaim)?.Value != DashJwtConstants.BackendScope ||
            !ulong.TryParse(principal.FindFirst(DashJwtConstants.UserIdClaim)?.Value, out var userId) || userId == 0)
        {
            context.Result = new ObjectResult(new ApiError("A valid dashboard user token is required."))
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
            return;
        }

        context.HttpContext.Items[UserIdKey] = userId;
        context.HttpContext.Items[UserNameKey] = principal.FindFirst(DashJwtConstants.UserNameClaim)?.Value;
    }
}
