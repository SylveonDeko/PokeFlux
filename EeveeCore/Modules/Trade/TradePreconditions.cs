using EeveeCore.Common.Enums;
using EeveeCore.Modules.Trade.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EeveeCore.Modules.Trade;

/// <summary>
///     Requires a bot owner or a staff member of at least the given rank.
/// </summary>
/// <param name="requiredRank">The lowest staff rank allowed.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireStaffRankAttribute(StaffRank requiredRank) : PreconditionAttribute
{
    /// <inheritdoc />
    public override async Task<PreconditionResult> CheckPermissionsAsync(ICommandContext context,
        CommandInfo command, IServiceProvider services)
    {
        var error = await services.GetRequiredService<StaffRankService>().CheckAsync(context.User.Id, requiredRank);
        return error is null ? PreconditionResult.FromSuccess() : PreconditionResult.FromError(error);
    }
}

/// <summary>
///     Blocks a command while its user is in a trade, clearing locks left behind by a trade that no longer exists.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireNotTradeLockedAttribute : PreconditionAttribute
{
    /// <inheritdoc />
    public override async Task<PreconditionResult> CheckPermissionsAsync(ICommandContext context,
        CommandInfo command, IServiceProvider services)
    {
        var tradeLocks = services.GetRequiredService<ITradeLockService>();
        if (!await tradeLocks.IsUserTradeLockedAsync(context.User.Id))
            return PreconditionResult.FromSuccess();

        var tradeService = services.GetService<TradeService>();
        if (tradeService != null && !await tradeService.HasActiveTradeSessionAsync(context.User.Id))
        {
            await tradeService.ClearOrphanedTradeLocksAsync(context.User.Id);
            return PreconditionResult.FromSuccess();
        }

        return PreconditionResult.FromError($"{context.User.Username} is currently in a trade!");
    }
}
