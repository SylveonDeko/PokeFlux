using EeveeCore.Common.Enums;
using LinqToDB;
using LinqToDB.Async;

namespace EeveeCore.Services;

/// <summary>
///     Reads staff ranks from the user table. Bot owners pass every rank check.
/// </summary>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="credentials">The bot credentials, for the owner list.</param>
public sealed class StaffRankService(LinqToDbConnectionProvider dbProvider, BotCredentials credentials) : INService
{
    /// <summary>
    ///     Whether the user is a bot owner.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    public bool IsOwner(ulong userId) => credentials.OwnerIds.Contains(userId);

    /// <summary>
    ///     Gets a user's staff rank, with bot owners as <see cref="StaffRank.Developer" /> and unknown users as
    ///     <see cref="StaffRank.User" />.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    public async Task<StaffRank> GetRankAsync(ulong userId)
    {
        if (IsOwner(userId))
            return StaffRank.Developer;

        var (found, staff) = await ReadStaffAsync(userId);
        return found && Enum.TryParse<StaffRank>(staff, true, out var rank) ? rank : StaffRank.User;
    }

    /// <summary>
    ///     Checks that a user is a bot owner or holds at least the given rank.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="required">The lowest rank allowed.</param>
    /// <returns>Null when allowed, otherwise why not.</returns>
    public async Task<string?> CheckAsync(ulong userId, StaffRank required)
    {
        if (IsOwner(userId))
            return null;

        var (found, staff) = await ReadStaffAsync(userId);
        if (!found)
            return "User not found in database";

        if (!Enum.TryParse<StaffRank>(staff, true, out var rank))
            return "Invalid staff rank";

        return rank >= required ? null : $"Insufficient permissions. Required: {required}";
    }

    private async Task<(bool Found, string? Staff)> ReadStaffAsync(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var row = await db.Users
            .Where(u => u.UserId == userId)
            .Select(u => new { u.Staff })
            .FirstOrDefaultAsync();
        return row is null ? (false, null) : (true, row.Staff);
    }
}
