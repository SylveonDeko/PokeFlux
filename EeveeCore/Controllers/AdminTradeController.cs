using EeveeCore.Common.Enums;
using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Trade.Services;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     Trade fraud and trade bans for staff, with the ranks and rules of the trade admin commands.
/// </summary>
/// <param name="tradeAdmin">The trade admin tools the commands use.</param>
/// <param name="views">Builds the admin shapes.</param>
/// <param name="audit">Records staff actions.</param>
/// <param name="dbProvider">The database connection provider.</param>
[Route("api/admin/trade")]
public class AdminTradeController(
    TradeAdminService tradeAdmin,
    AdminViews views,
    StaffAuditService audit,
    LinqToDbConnectionProvider dbProvider) : AdminControllerBase
{
    /// <summary>
    ///     Counts the fraud detections of the last days, like the stats command.
    /// </summary>
    /// <param name="days">Days to look back, from 1 to 365.</param>
    [HttpGet("stats")]
    [DashStaff(StaffRank.Mod)]
    public async Task<IActionResult> Stats(int days = 7)
    {
        days = Math.Clamp(days, 1, 365);
        var s = await tradeAdmin.GetStatsAsync(days);
        return Ok(new AdminFraudStats(days, s.TotalDetections, s.BlockedTrades, s.HighRisk, s.MediumRisk, s.LowRisk,
            s.AltAccountFlags, s.RmtFlags, s.NewbieExploitFlags));
    }

    /// <summary>
    ///     Lists recent high-risk detections, riskiest first, like the recent command.
    /// </summary>
    /// <param name="hours">Hours to look back, up to a year.</param>
    /// <param name="page">The page, from 1.</param>
    /// <param name="pageSize">Detections per page, up to 50.</param>
    [HttpGet("detections")]
    [DashStaff(StaffRank.Mod)]
    public async Task<IActionResult> Detections(int hours = 24, int page = 1, int pageSize = DefaultPageSize)
    {
        hours = Math.Clamp(hours, 1, 24 * 365);
        (page, pageSize) = Paging(page, pageSize);
        var (items, total) = await tradeAdmin.GetRecentDetectionsAsync(hours, (page - 1) * pageSize, pageSize);
        return Ok(new AdminDetectionList(hours, TradeAdminService.FlagRiskScore, await views.DetectionsAsync(items),
            total, page, pageSize));
    }

    /// <summary>
    ///     Gets a detection case with its trade analysis, like the investigate command.
    /// </summary>
    /// <param name="detectionId">The detection ID.</param>
    [HttpGet("detections/{detectionId:int}")]
    [DashStaff(StaffRank.Investigator)]
    public async Task<IActionResult> Detection(int detectionId)
    {
        var detection = await tradeAdmin.GetDetectionAsync(detectionId);
        if (detection is null)
            return Error(404, $"Detection #{detectionId} not found.");

        var summary = (await views.DetectionsAsync([detection]))[0];
        var analytics = detection.SuspiciousTradeAnalytics;
        AdminTradeAnalysis? analysis = null;
        if (analytics is not null)
        {
            var flags = new List<string>();
            if (analytics.FlaggedAltAccount) flags.Add("Alt Account");
            if (analytics.FlaggedRmt) flags.Add("RMT");
            if (analytics.FlaggedNewbieExploitation) flags.Add("Newbie Exploitation");
            if (analytics.FlaggedUnusualBehavior) flags.Add("Unusual Behavior");
            if (analytics.FlaggedBotActivity) flags.Add("Bot Activity");
            analysis = new AdminTradeAnalysis(analytics.SenderTotalValue, analytics.ReceiverTotalValue,
                analytics.ValueRatio, analytics.ValueImbalanceScore, flags);
        }

        return Ok(new AdminDetectionDetail(summary, detection.ConfidenceLevel, detection.AutomatedAction.ToString(),
            detection.TriggeredRules, string.IsNullOrWhiteSpace(detection.AdminNotes) ? null : detection.AdminNotes,
            analysis));
    }

    /// <summary>
    ///     Gets the trade relationship between two users, like the relationship command.
    /// </summary>
    /// <param name="user1">One user's ID.</param>
    /// <param name="user2">The other user's ID.</param>
    [HttpGet("relationship")]
    [DashStaff(StaffRank.Mod)]
    public async Task<IActionResult> Relationship(ulong user1, ulong user2)
    {
        if (user1 == 0 || user2 == 0 || user1 == user2)
            return Error(400, "Two different user IDs are required.");

        var r = await tradeAdmin.GetRelationshipAsync(user1, user2);
        if (r is null)
            return Error(404, "No trade relationship found between these users.");

        var names = await views.NamesAsync([r.User1Id, r.User2Id]);
        var flags = new List<string>();
        if (r.FlaggedPotentialAlts) flags.Add("Potential Alts");
        if (r.FlaggedPotentialRmt) flags.Add("Potential RMT");
        if (r.FlaggedNewbieExploitation) flags.Add("Newbie Exploitation");

        return Ok(new AdminRelationship(AdminViews.Ref(r.User1Id, names), AdminViews.Ref(r.User2Id, names),
            r.TotalTrades, r.FirstTradeTimestamp, r.LastTradeTimestamp, r.TradingFrequencyScore,
            r.User1TotalGivenValue, r.User2TotalGivenValue, r.ValueImbalanceRatio, r.BalancedTrades,
            r.RelationshipRiskScore, r.AccountAgeDifferenceDays, r.SuspiciousTimingPattern, r.Whitelisted, flags,
            string.IsNullOrWhiteSpace(r.AdminNotes) ? null : r.AdminNotes, r.LastUpdated));
    }

    /// <summary>
    ///     Whitelists a relationship so its trades skip fraud detection, like the whitelist command.
    /// </summary>
    /// <param name="request">The two users and the reason.</param>
    [HttpPost("relationship/whitelist")]
    [DashStaff(StaffRank.Admin)]
    public async Task<IActionResult> Whitelist([FromBody] AdminWhitelistRequest request)
    {
        if (ReadReason(request.Reason) is not { } reason)
            return ReasonRequired();
        if (request.User1 == 0 || request.User2 == 0 || request.User1 == request.User2)
            return Error(400, "Two different user IDs are required.");

        if (!await tradeAdmin.WhitelistAsync(request.User1, request.User2, StaffName))
            return Error(404, "No trade relationship found between these users. They must trade at least once first.");

        await audit.RecordAsync(UserId, UserName, "whitelist", reason, request.User1,
            $"Relationship with {request.User2}");
        return Ok(new ApiMessage("The relationship is whitelisted. Future trades between them skip fraud detection."));
    }

    /// <summary>
    ///     Trade bans a user, and market bans them unless told not to, like the tradeban command.
    /// </summary>
    /// <param name="userId">The user to ban.</param>
    /// <param name="request">The reason and whether to market ban.</param>
    [HttpPost("users/{userId}/tradeban")]
    [DashStaff(StaffRank.Admin)]
    public async Task<IActionResult> TradeBan(ulong userId, [FromBody] AdminTradeBanRequest request)
    {
        if (ReadReason(request.Reason) is not { } reason)
            return ReasonRequired();
        if (!await UserExistsAsync(userId))
            return Error(404, "That user has not started.");

        if (!await tradeAdmin.TradeBanAsync(userId, reason, request.MarketBan, UserId))
            return Error(500, "Failed to apply the trade ban. Please try again.");

        await audit.RecordAsync(UserId, UserName, "tradeban", reason, userId,
            request.MarketBan ? "Also market banned" : "Trade ban only");
        return Ok(new ApiMessage(request.MarketBan
            ? "The user is now trade banned and market banned."
            : "The user is now trade banned."));
    }

    /// <summary>
    ///     Removes a user's trade and market bans, like the detradeban command. Fraud detection stays on.
    /// </summary>
    /// <param name="userId">The user to unban.</param>
    /// <param name="request">The reason.</param>
    [HttpPost("users/{userId}/detradeban")]
    [DashStaff(StaffRank.Admin)]
    public async Task<IActionResult> DetradeBan(ulong userId, [FromBody] AdminReasonRequest request)
    {
        if (ReadReason(request.Reason) is not { } reason)
            return ReasonRequired();

        var result = await tradeAdmin.RemoveTradeBanAsync(userId, reason, UserId);
        switch (result.Outcome)
        {
            case DetradeBanOutcome.NotFound:
                return Error(404, "That user has not started.");
            case DetradeBanOutcome.NotBanned:
                return Error(400, "That user is not trade banned.");
            case DetradeBanOutcome.Failed:
                return Error(500, "Failed to remove the trade ban. Please try again.");
        }

        await audit.RecordAsync(UserId, UserName, "detradeban", reason, userId, result.PreviousBan);
        return Ok(new ApiMessage("The trade and market bans are removed. Fraud detection still checks their trades."));
    }

    private async Task<bool> UserExistsAsync(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        return await db.Users.AnyAsync(u => u.UserId == userId);
    }
}
