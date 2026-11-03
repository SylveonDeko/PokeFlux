using EeveeCore.Database.Linq.Models.Bot;
using LinqToDB;

namespace EeveeCore.Services;

/// <summary>
///     Records staff actions taken through the dashboard.
/// </summary>
/// <param name="dbProvider">The database connection provider.</param>
public sealed class StaffAuditService(LinqToDbConnectionProvider dbProvider) : INService
{
    /// <summary>
    ///     Records a staff action.
    /// </summary>
    /// <param name="staffId">The staff member.</param>
    /// <param name="staffName">The staff member's name.</param>
    /// <param name="action">The action, such as <c>tradeban</c>.</param>
    /// <param name="reason">The reason given.</param>
    /// <param name="targetUserId">The user acted on.</param>
    /// <param name="details">More about the action.</param>
    public async Task RecordAsync(ulong staffId, string? staffName, string action, string reason,
        ulong? targetUserId = null, string? details = null)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        await db.InsertAsync(new StaffAuditEntry
        {
            StaffId = staffId,
            StaffName = staffName,
            Action = action,
            Reason = reason,
            TargetUserId = targetUserId,
            Details = details,
            CreatedAt = DateTime.UtcNow
        });
    }
}
