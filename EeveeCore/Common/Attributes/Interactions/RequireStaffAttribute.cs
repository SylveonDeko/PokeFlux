using Discord.Interactions;
using EeveeCore.Common.Enums;

namespace EeveeCore.Common.Attributes.Interactions;

/// <summary>
///     Attribute to check if user has the required staff rank before executing a command or method.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class RequireStaffAttribute : PreconditionAttribute
{
    private readonly StaffRank _requiredRank;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RequireStaffAttribute" /> class.
    /// </summary>
    /// <param name="requiredRank">The minimum staff rank required to execute the command.</param>
    public RequireStaffAttribute(StaffRank requiredRank)
    {
        _requiredRank = requiredRank;
    }

    /// <summary>
    ///     Checks the requirements before executing a command or method.
    /// </summary>
    /// <param name="context">The interaction context.</param>
    /// <param name="command">The command being executed.</param>
    /// <param name="services">The service provider.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the precondition result.</returns>
    public override async Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context,
        ICommandInfo command,
        IServiceProvider services)
    {
        var error = await services.GetRequiredService<StaffRankService>().CheckAsync(context.User.Id, _requiredRank);
        return error is null ? PreconditionResult.FromSuccess() : PreconditionResult.FromError(error);
    }
}
