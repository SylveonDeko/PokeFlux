using EeveeCore.Common.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EeveeCore.Controllers.Common;

/// <summary>
///     Requires the dashboard user to be a bot owner or a staff member of at least the given rank, using the same
///     check as the staff commands.
/// </summary>
/// <param name="requiredRank">The lowest staff rank allowed.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DashStaffAttribute(StaffRank requiredRank) : Attribute, IAsyncAuthorizationFilter, IOrderedFilter
{
    /// <inheritdoc />
    public int Order => 1;

    /// <inheritdoc />
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.Items[DashUserAttribute.UserIdKey] is not ulong userId)
        {
            context.Result = Forbidden("A valid dashboard user token is required.", StatusCodes.Status401Unauthorized);
            return;
        }

        var error = await context.HttpContext.RequestServices.GetRequiredService<StaffRankService>()
            .CheckAsync(userId, requiredRank);
        if (error is not null)
            context.Result = Forbidden(error);
    }

    internal static ObjectResult Forbidden(string message, int status = StatusCodes.Status403Forbidden) =>
        new(new ApiError(message)) { StatusCode = status };
}

/// <summary>
///     Requires the dashboard user to be a bot owner, like the owner-only spawn commands.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DashBotOwnerAttribute : Attribute, IAsyncAuthorizationFilter, IOrderedFilter
{
    /// <inheritdoc />
    public int Order => 1;

    /// <inheritdoc />
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.Items[DashUserAttribute.UserIdKey] is not ulong userId ||
            !context.HttpContext.RequestServices.GetRequiredService<StaffRankService>().IsOwner(userId))
            context.Result = DashStaffAttribute.Forbidden("Only bot owners can use this.");

        return Task.CompletedTask;
    }
}
