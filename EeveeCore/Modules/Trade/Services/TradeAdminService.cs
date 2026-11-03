using EeveeCore.Database.Linq.Models.Game;
using LinqToDB;
using LinqToDB.Async;
using Serilog;

namespace EeveeCore.Modules.Trade.Services;

/// <summary>
///     Fraud detection counts over a period.
/// </summary>
/// <param name="TotalDetections">Every detection.</param>
/// <param name="BlockedTrades">Detections that blocked the trade.</param>
/// <param name="HighRisk">Detections at or above the high risk score.</param>
/// <param name="MediumRisk">Detections between the flag and high risk scores.</param>
/// <param name="LowRisk">Detections below the flag score.</param>
/// <param name="AltAccountFlags">Alt account trading detections.</param>
/// <param name="RmtFlags">Real money trading detections.</param>
/// <param name="NewbieExploitFlags">Newbie exploitation detections.</param>
public record FraudStats(
    int TotalDetections,
    int BlockedTrades,
    int HighRisk,
    int MediumRisk,
    int LowRisk,
    int AltAccountFlags,
    int RmtFlags,
    int NewbieExploitFlags);

/// <summary>
///     What happened when removing a trade ban.
/// </summary>
public enum DetradeBanOutcome
{
    /// <summary>The user has no row.</summary>
    NotFound,

    /// <summary>The user has neither a trade nor a market ban.</summary>
    NotBanned,

    /// <summary>The update changed nothing.</summary>
    Failed,

    /// <summary>The bans were removed.</summary>
    Removed
}

/// <summary>
///     The result of removing a trade ban.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="PreviousBan">The reason and date of the ban that was removed.</param>
public record DetradeBanResult(DetradeBanOutcome Outcome, string? PreviousBan = null);

/// <summary>
///     The staff tools for trade fraud and trade bans, shared by the trade admin commands and the dashboard.
/// </summary>
/// <param name="dbProvider">The database connection provider.</param>
public sealed class TradeAdminService(LinqToDbConnectionProvider dbProvider) : INService
{
    /// <summary>
    ///     The lowest risk score listed as a recent high-risk detection.
    /// </summary>
    public const double FlagRiskScore = 0.4;

    /// <summary>
    ///     The lowest risk score counted as high risk.
    /// </summary>
    public const double HighRiskScore = 0.6;

    /// <summary>
    ///     Gets detections at or above <see cref="FlagRiskScore" /> from the last hours, riskiest first.
    /// </summary>
    /// <param name="hours">Hours to look back.</param>
    /// <param name="skip">Detections to skip.</param>
    /// <param name="take">Detections to return.</param>
    public async Task<(List<TradeFraudDetection> Items, int Total)> GetRecentDetectionsAsync(int hours, int skip,
        int take)
    {
        var cutoff = DateTime.UtcNow.AddHours(-hours);
        await using var db = await dbProvider.GetConnectionAsync();

        var query = db.TradeFraudDetections.Where(d => d.DetectionTimestamp >= cutoff && d.RiskScore >= FlagRiskScore);
        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(d => d.RiskScore)
            .ThenByDescending(d => d.DetectionTimestamp)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
        return (items, total);
    }

    /// <summary>
    ///     Gets the newest detections a user is the primary or secondary user of.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="take">Detections to return.</param>
    public async Task<List<TradeFraudDetection>> GetDetectionsForUserAsync(ulong userId, int take)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        return await db.TradeFraudDetections
            .Where(d => d.PrimaryUserId == userId || d.SecondaryUserId == userId)
            .OrderByDescending(d => d.DetectionTimestamp)
            .Take(take)
            .ToListAsync();
    }

    /// <summary>
    ///     Gets a detection with its trade analysis.
    /// </summary>
    /// <param name="detectionId">The detection ID.</param>
    public async Task<TradeFraudDetection?> GetDetectionAsync(int detectionId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        return await db.TradeFraudDetections
            .LoadWithAsTable(d => d.SuspiciousTradeAnalytics)
            .FirstOrDefaultAsync(d => d.Id == detectionId);
    }

    /// <summary>
    ///     Gets the trade relationship between two users, in either order.
    /// </summary>
    /// <param name="userA">One user.</param>
    /// <param name="userB">The other user.</param>
    public async Task<UserTradeRelationship?> GetRelationshipAsync(ulong userA, ulong userB)
    {
        var (lowerId, higherId) = Order(userA, userB);
        await using var db = await dbProvider.GetConnectionAsync();
        return await db.UserTradeRelationships.FirstOrDefaultAsync(r => r.User1Id == lowerId && r.User2Id == higherId);
    }

    /// <summary>
    ///     Whitelists the trade relationship between two users so their trades skip fraud detection.
    /// </summary>
    /// <param name="userA">One user.</param>
    /// <param name="userB">The other user.</param>
    /// <param name="staffName">The name of the staff member, for the admin notes.</param>
    /// <returns>False when the users have never traded.</returns>
    public async Task<bool> WhitelistAsync(ulong userA, ulong userB, string staffName)
    {
        var (lowerId, higherId) = Order(userA, userB);
        await using var db = await dbProvider.GetConnectionAsync();

        if (!await db.UserTradeRelationships.AnyAsync(r => r.User1Id == lowerId && r.User2Id == higherId))
            return false;

        await db.UserTradeRelationships
            .Where(r => r.User1Id == lowerId && r.User2Id == higherId)
            .Set(r => r.Whitelisted, true)
            .Set(r => r.AdminReviewed, true)
            .Set(r => r.AdminVerdict, true)
            .Set(r => r.AdminNotes, $"Whitelisted by {staffName} on {DateTime.UtcNow:yyyy-MM-dd}")
            .Set(r => r.LastUpdated, DateTime.UtcNow)
            .UpdateAsync();
        return true;
    }

    /// <summary>
    ///     Counts the detections of the last days.
    /// </summary>
    /// <param name="days">Days to look back.</param>
    public async Task<FraudStats> GetStatsAsync(int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        await using var db = await dbProvider.GetConnectionAsync();

        var stats = await db.TradeFraudDetections
            .Where(d => d.DetectionTimestamp >= cutoff)
            .GroupBy(d => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Blocked = g.Count(d => d.TradeBlocked),
                High = g.Count(d => d.RiskScore >= HighRiskScore),
                Medium = g.Count(d => d.RiskScore >= FlagRiskScore && d.RiskScore < HighRiskScore),
                Low = g.Count(d => d.RiskScore < FlagRiskScore),
                Alt = g.Count(d => d.FraudType == FraudType.AltAccountTrading),
                Rmt = g.Count(d => d.FraudType == FraudType.RealMoneyTrading),
                Newbie = g.Count(d => d.FraudType == FraudType.NewbieExploitation)
            })
            .FirstOrDefaultAsync();

        return stats is null
            ? new FraudStats(0, 0, 0, 0, 0, 0, 0, 0)
            : new FraudStats(stats.Total, stats.Blocked, stats.High, stats.Medium, stats.Low, stats.Alt, stats.Rmt,
                stats.Newbie);
    }

    /// <summary>
    ///     Trade bans a user, and market bans them too when asked.
    /// </summary>
    /// <param name="userId">The user to ban.</param>
    /// <param name="reason">Why they are banned.</param>
    /// <param name="marketBan">Whether to also ban them from the market.</param>
    /// <param name="staffId">The staff member applying the ban.</param>
    /// <returns>False when the update changed nothing.</returns>
    public async Task<bool> TradeBanAsync(ulong userId, string reason, bool marketBan, ulong staffId)
    {
        await using var db = await dbProvider.GetConnectionAsync();

        var userData = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        var banDate = DateTime.UtcNow;
        int updated;

        if (userData == null)
        {
            await db.InsertAsync(new Database.Linq.Models.Bot.User
            {
                UserId = userId,
                TradeBanned = true,
                TradeBanReason = reason,
                TradeBanDate = banDate,
                MarketBanned = marketBan,
                MarketBanReason = marketBan ? reason : null,
                MarketBanDate = marketBan ? banDate : null
            });
            updated = 1;
        }
        else
        {
            updated = await db.Users
                .Where(u => u.UserId == userId)
                .Set(u => u.TradeBanned, true)
                .Set(u => u.TradeBanReason, reason)
                .Set(u => u.TradeBanDate, banDate)
                .Set(u => u.MarketBanned, marketBan)
                .Set(u => u.MarketBanReason, marketBan ? reason : userData.MarketBanReason)
                .Set(u => u.MarketBanDate, marketBan ? banDate : userData.MarketBanDate)
                .UpdateAsync();
        }

        if (updated == 0)
            return false;

        Log.Warning("Trade ban applied by admin: User={UserId}, Admin={AdminId}, Reason={Reason}, MarketBan={MarketBan}",
            userId, staffId, reason, marketBan);
        return true;
    }

    /// <summary>
    ///     Removes a user's trade and market bans. Fraud detection keeps analysing their trades.
    /// </summary>
    /// <param name="userId">The user to unban.</param>
    /// <param name="reason">Why the ban is removed.</param>
    /// <param name="staffId">The staff member removing the ban.</param>
    public async Task<DetradeBanResult> RemoveTradeBanAsync(ulong userId, string reason, ulong staffId)
    {
        await using var db = await dbProvider.GetConnectionAsync();

        var userData = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (userData == null)
            return new DetradeBanResult(DetradeBanOutcome.NotFound);

        if (userData.TradeBanned != true && userData.MarketBanned != true)
            return new DetradeBanResult(DetradeBanOutcome.NotBanned);

        var banHistory = $"Previous ban: {userData.TradeBanReason ?? userData.MarketBanReason} " +
                         $"(banned on {userData.TradeBanDate ?? userData.MarketBanDate:yyyy-MM-dd})";

        var updated = await db.Users
            .Where(u => u.UserId == userId)
            .Set(u => u.TradeBanned, false)
            .Set(u => u.TradeBanReason, "")
            .Set(u => u.TradeBanDate, DateTime.Now)
            .Set(u => u.MarketBanned, false)
            .Set(u => u.MarketBanReason, "")
            .Set(u => u.MarketBanDate, DateTime.Now)
            .UpdateAsync();

        if (updated == 0)
            return new DetradeBanResult(DetradeBanOutcome.Failed);

        Log.Warning("Trade ban removed by admin: User={UserId}, Admin={AdminId}, Reason={Reason}, PreviousBan={PreviousBan}",
            userId, staffId, reason, banHistory);
        return new DetradeBanResult(DetradeBanOutcome.Removed, banHistory);
    }

    private static (ulong Lower, ulong Higher) Order(ulong a, ulong b) => a < b ? (a, b) : (b, a);
}
