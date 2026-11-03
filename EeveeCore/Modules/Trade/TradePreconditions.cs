using EeveeCore.Common.Enums;
using EeveeCore.Modules.Trade.Services;
using LinqToDB.Async;
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
        var creds = services.GetRequiredService<BotCredentials>();
        if (creds.IsOwner(context.User))
            return PreconditionResult.FromSuccess();

        var dbProvider = services.GetRequiredService<LinqToDbConnectionProvider>();
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == context.User.Id);

        if (user == null)
            return PreconditionResult.FromError("User not found in database");

        if (!Enum.TryParse<StaffRank>(user.Staff, true, out var userRank))
            return PreconditionResult.FromError("Invalid staff rank");

        return userRank >= requiredRank
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError($"Insufficient permissions. Required: {requiredRank}");
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
