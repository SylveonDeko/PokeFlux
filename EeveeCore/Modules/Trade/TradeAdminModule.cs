using EeveeCore.Common.Enums;
using EeveeCore.Common.ModuleBases;
using EeveeCore.Database.Linq.Models.Game;
using LinqToDB;
using LinqToDB.Async;

namespace EeveeCore.Modules.Trade;

/// <summary>
///     Staff commands for trade fraud detection and trade bans.
/// </summary>
/// <param name="context">The database connection provider.</param>
[Name("Trade Admin")]
[Group("tradeadmin")]
public class TradeAdminModule(LinqToDbConnectionProvider context) : EeveeModule
{
    /// <summary>
    ///     Shows recent high-risk trade detections.
    /// </summary>
    /// <param name="hours">Hours to look back.</param>
    [Command("recent")]
    [Summary("Show recent high-risk trade detections.")]
    [RequireStaffRank(StaffRank.Mod)]
    public async Task Recent(int hours = 24)
    {
        var cutoffTime = DateTime.UtcNow.AddHours(-hours);

        await using var db = await context.GetConnectionAsync();

        var detections = await db.TradeFraudDetections
            .Where(d => d.DetectionTimestamp >= cutoffTime && d.RiskScore >= 0.4)
            .OrderByDescending(d => d.RiskScore)
            .Take(10)
            .Select(d => new
            {
                d.Id,
                d.PrimaryUserId,
                d.SecondaryUserId,
                d.RiskScore,
                d.FraudType,
                d.InvestigationStatus,
                d.DetectionTimestamp,
                d.TradeBlocked
            })
            .ToListAsync();

        if (!detections.Any())
        {
            await SendStaffAsync($"No high-risk trade detections found in the last {hours} hours.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"🚨 High-Risk Trade Detections (Last {hours}h)")
            .WithColor(Color.Red)
            .WithTimestamp(DateTimeOffset.UtcNow);

        foreach (var detection in detections)
        {
            var status = detection.TradeBlocked ? "🚫 BLOCKED" : "⚠️ FLAGGED";
            var statusColor = detection.InvestigationStatus switch
            {
                InvestigationStatus.Pending => "🔍",
                InvestigationStatus.InProgress => "⏳",
                InvestigationStatus.Completed => "✅",
                InvestigationStatus.Dismissed => "❌",
                _ => "❓"
            };

            embed.AddField(
                $"{status} Detection #{detection.Id} {statusColor}",
                $"**Users:** <@{detection.PrimaryUserId}> ↔ <@{detection.SecondaryUserId}>\n" +
                $"**Risk:** {detection.RiskScore:P1} | **Type:** {detection.FraudType}\n" +
                $"**Time:** <t:{((DateTimeOffset)detection.DetectionTimestamp).ToUnixTimeSeconds()}:R>");
        }

        await SendStaffAsync(embed);
    }

    /// <summary>
    ///     Investigates a specific fraud detection case.
    /// </summary>
    /// <param name="detectionId">The detection ID.</param>
    [Command("investigate")]
    [Summary("Investigate a specific fraud detection case.")]
    [RequireStaffRank(StaffRank.Investigator)]
    public async Task Investigate(int detectionId)
    {
        await using var db = await context.GetConnectionAsync();

        var detection = await db.TradeFraudDetections
            .LoadWithAsTable(d => d.SuspiciousTradeAnalytics)
            .FirstOrDefaultAsync(d => d.Id == detectionId);

        if (detection == null)
        {
            await SendStaffAsync($"Detection #{detectionId} not found.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"🔍 Investigation: Detection #{detectionId}")
            .WithColor(detection.TradeBlocked ? Color.Red : Color.Orange)
            .WithTimestamp(detection.DetectionTimestamp)
            .AddField("Users Involved",
                $"**Primary:** <@{detection.PrimaryUserId}>\n" +
                $"**Secondary:** {(detection.SecondaryUserId.HasValue ? $"<@{detection.SecondaryUserId}>" : "None")}",
                true)
            .AddField("Risk Assessment",
                $"**Score:** {detection.RiskScore:P1}\n" +
                $"**Confidence:** {detection.ConfidenceLevel:P1}\n" +
                $"**Type:** {detection.FraudType}",
                true)
            .AddField("Actions Taken",
                $"**Automated:** {detection.AutomatedAction}\n" +
                $"**Trade Blocked:** {(detection.TradeBlocked ? "Yes" : "No")}\n" +
                $"**Status:** {detection.InvestigationStatus}",
                true);

        if (detection.SuspiciousTradeAnalytics != null)
        {
            var analytics = detection.SuspiciousTradeAnalytics;
            embed.AddField("Trade Analysis",
                $"**Sender Value:** ${analytics.SenderTotalValue:N0}\n" +
                $"**Receiver Value:** ${analytics.ReceiverTotalValue:N0}\n" +
                $"**Value Ratio:** {analytics.ValueRatio:F2}x\n" +
                $"**Imbalance Score:** {analytics.ValueImbalanceScore:P1}");

            var flags = new List<string>();
            if (analytics.FlaggedAltAccount) flags.Add("Alt Account");
            if (analytics.FlaggedRmt) flags.Add("RMT");
            if (analytics.FlaggedNewbieExploitation) flags.Add("Newbie Exploitation");
            if (analytics.FlaggedUnusualBehavior) flags.Add("Unusual Behavior");
            if (analytics.FlaggedBotActivity) flags.Add("Bot Activity");

            embed.AddField("Detection Flags", flags.Any() ? string.Join(", ", flags) : "None", true);
        }

        if (!string.IsNullOrEmpty(detection.AdminNotes))
            embed.AddField("Admin Notes", detection.AdminNotes);

        await SendStaffAsync(embed);
    }

    /// <summary>
    ///     Shows the trade relationship between two users.
    /// </summary>
    /// <param name="user1">The first user.</param>
    /// <param name="user2">The second user.</param>
    [Command("relationship")]
    [Summary("Show trade relationship between two users.")]
    [RequireStaffRank(StaffRank.Mod)]
    public async Task Relationship(IUser user1, IUser user2)
    {
        var (lowerId, higherId) = user1.Id < user2.Id ? (user1.Id, user2.Id) : (user2.Id, user1.Id);

        await using var db = await context.GetConnectionAsync();

        var relationship = await db.UserTradeRelationships
            .FirstOrDefaultAsync(r => r.User1Id == lowerId && r.User2Id == higherId);

        if (relationship == null)
        {
            await SendStaffAsync($"No trade relationship found between {user1.Mention} and {user2.Mention}.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("📊 Trade Relationship Analysis")
            .WithDescription($"**{user1.Mention} ↔ {user2.Mention}**")
            .WithColor(relationship.RelationshipRiskScore > 0.6 ? Color.Red :
                relationship.RelationshipRiskScore > 0.3 ? Color.Orange : Color.Green)
            .WithTimestamp(relationship.LastUpdated)
            .AddField("Trade Statistics",
                $"**Total Trades:** {relationship.TotalTrades}\n" +
                $"**First Trade:** <t:{((DateTimeOffset)relationship.FirstTradeTimestamp).ToUnixTimeSeconds()}:d>\n" +
                $"**Last Trade:** <t:{((DateTimeOffset)relationship.LastTradeTimestamp).ToUnixTimeSeconds()}:R>\n" +
                $"**Frequency:** {relationship.TradingFrequencyScore:F2} trades/day",
                true)
            .AddField("Value Analysis",
                $"**{(lowerId == user1.Id ? user1.Username : user2.Username)} Given:** ${relationship.User1TotalGivenValue:N0}\n" +
                $"**{(lowerId == user1.Id ? user2.Username : user1.Username)} Given:** ${relationship.User2TotalGivenValue:N0}\n" +
                $"**Imbalance Ratio:** {relationship.ValueImbalanceRatio:F2}x\n" +
                $"**Balanced Trades:** {relationship.BalancedTrades}/{relationship.TotalTrades}",
                true)
            .AddField("Risk Assessment",
                $"**Risk Score:** {relationship.RelationshipRiskScore:P1}\n" +
                $"**Account Age Diff:** {relationship.AccountAgeDifferenceDays} days\n" +
                $"**Suspicious Timing:** {(relationship.SuspiciousTimingPattern ? "Yes" : "No")}\n" +
                $"**Whitelisted:** {(relationship.Whitelisted ? "Yes" : "No")}",
                true);

        var flags = new List<string>();
        if (relationship.FlaggedPotentialAlts) flags.Add("Potential Alts");
        if (relationship.FlaggedPotentialRmt) flags.Add("Potential RMT");
        if (relationship.FlaggedNewbieExploitation) flags.Add("Newbie Exploitation");

        if (flags.Any())
            embed.AddField("⚠️ Flags", string.Join(", ", flags));

        await SendStaffAsync(embed);
    }

    /// <summary>
    ///     Whitelists a user relationship to bypass fraud detection.
    /// </summary>
    /// <param name="user1">The first user.</param>
    /// <param name="user2">The second user.</param>
    [Command("whitelist")]
    [Summary("Whitelist a user relationship to bypass fraud detection.")]
    [RequireStaffRank(StaffRank.Admin)]
    public async Task Whitelist(IUser user1, IUser user2)
    {
        var (lowerId, higherId) = user1.Id < user2.Id ? (user1.Id, user2.Id) : (user2.Id, user1.Id);

        await using var db = await context.GetConnectionAsync();

        var relationship = await db.UserTradeRelationships
            .FirstOrDefaultAsync(r => r.User1Id == lowerId && r.User2Id == higherId);

        if (relationship == null)
        {
            await SendStaffAsync($"No trade relationship found between {user1.Mention} and {user2.Mention}. They must trade at least once first.");
            return;
        }

        await db.UserTradeRelationships
            .Where(r => r.User1Id == lowerId && r.User2Id == higherId)
            .Set(r => r.Whitelisted, true)
            .Set(r => r.AdminReviewed, true)
            .Set(r => r.AdminVerdict, true)
            .Set(r => r.AdminNotes, $"Whitelisted by {ctx.User.Username} on {DateTime.UtcNow:yyyy-MM-dd}")
            .Set(r => r.LastUpdated, DateTime.UtcNow)
            .UpdateAsync();

        await SendStaffAsync($"✅ Trade relationship between {user1.Mention} and {user2.Mention} has been whitelisted. Future trades will bypass fraud detection.");
    }

    /// <summary>
    ///     Shows fraud detection statistics.
    /// </summary>
    /// <param name="days">Days to analyze.</param>
    [Command("stats")]
    [Summary("Show fraud detection statistics.")]
    [RequireStaffRank(StaffRank.Mod)]
    public async Task Stats(int days = 7)
    {
        var cutoffTime = DateTime.UtcNow.AddDays(-days);

        await using var db = await context.GetConnectionAsync();

        var stats = await db.TradeFraudDetections
            .Where(d => d.DetectionTimestamp >= cutoffTime)
            .GroupBy(d => 1)
            .Select(g => new
            {
                TotalDetections = g.Count(),
                BlockedTrades = g.Count(d => d.TradeBlocked),
                HighRisk = g.Count(d => d.RiskScore >= 0.6),
                MediumRisk = g.Count(d => d.RiskScore >= 0.4 && d.RiskScore < 0.6),
                LowRisk = g.Count(d => d.RiskScore < 0.4),
                AltAccountFlags = g.Count(d => d.FraudType == FraudType.AltAccountTrading),
                RmtFlags = g.Count(d => d.FraudType == FraudType.RealMoneyTrading),
                NewbieExploitFlags = g.Count(d => d.FraudType == FraudType.NewbieExploitation)
            })
            .FirstOrDefaultAsync();

        if (stats == null || stats.TotalDetections == 0)
        {
            await SendStaffAsync($"No fraud detections found in the last {days} days.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"📈 Fraud Detection Statistics (Last {days} days)")
            .WithColor(Color.Blue)
            .WithTimestamp(DateTimeOffset.UtcNow)
            .AddField("Detection Summary",
                $"**Total Detections:** {stats.TotalDetections}\n" +
                $"**Blocked Trades:** {stats.BlockedTrades}\n" +
                $"**Block Rate:** {(double)stats.BlockedTrades / stats.TotalDetections:P1}",
                true)
            .AddField("Risk Distribution",
                $"**High Risk:** {stats.HighRisk}\n" +
                $"**Medium Risk:** {stats.MediumRisk}\n" +
                $"**Low Risk:** {stats.LowRisk}",
                true)
            .AddField("Fraud Types",
                $"**Alt Accounts:** {stats.AltAccountFlags}\n" +
                $"**RMT:** {stats.RmtFlags}\n" +
                $"**Newbie Exploit:** {stats.NewbieExploitFlags}",
                true);

        await SendStaffAsync(embed);
    }

    /// <summary>
    ///     Removes a user's trade and market bans while keeping fraud detection active.
    /// </summary>
    /// <param name="user">The user to unban.</param>
    /// <param name="reason">Why the ban is removed.</param>
    [Command("detradeban")]
    [Summary("Remove trade ban from a user (keeps fraud detection active).")]
    [RequireStaffRank(StaffRank.Admin)]
    public async Task DetradeBan(IUser user, [Remainder] string reason = "Admin discretion")
    {
        await using var db = await context.GetConnectionAsync();

        var userData = await db.Users.FirstOrDefaultAsync(u => u.UserId == user.Id);

        if (userData == null)
        {
            await SendStaffAsync($"User {user.Mention} not found in the database.");
            return;
        }

        if (userData.TradeBanned != true && userData.MarketBanned != true)
        {
            await SendStaffAsync($"User {user.Mention} is not currently trade banned.");
            return;
        }

        var banHistory = $"Previous ban: {userData.TradeBanReason ?? userData.MarketBanReason} " +
                         $"(banned on {userData.TradeBanDate ?? userData.MarketBanDate:yyyy-MM-dd})";

        var updated = await db.Users
            .Where(u => u.UserId == user.Id)
            .Set(u => u.TradeBanned, false)
            .Set(u => u.TradeBanReason, "")
            .Set(u => u.TradeBanDate, DateTime.Now)
            .Set(u => u.MarketBanned, false)
            .Set(u => u.MarketBanReason, "")
            .Set(u => u.MarketBanDate, DateTime.Now)
            .UpdateAsync();

        if (updated == 0)
        {
            await SendStaffAsync($"Failed to remove trade ban for {user.Mention}. Please try again.");
            return;
        }

        Serilog.Log.Warning("🔓 Trade ban removed by admin: User={UserId} ({Username}), Admin={AdminId} ({AdminUsername}), Reason={Reason}, PreviousBan={PreviousBan}",
            user.Id, user.Username, ctx.User.Id, ctx.User.Username, reason, banHistory);

        await SendStaffAsync(new EmbedBuilder()
            .WithTitle("✅ Trade Ban Removed")
            .WithColor(Color.Green)
            .WithDescription($"**User:** {user.Mention} ({user.Username}#{user.Discriminator})")
            .AddField("Action Taken",
                "• Trade ban removed\n" +
                "• Market ban removed\n" +
                "• ⚠️ **Fraud detection remains ACTIVE**\n" +
                "• User's future trades will still be analyzed")
            .AddField("Admin", ctx.User.Mention, true)
            .AddField("Reason", reason, true)
            .AddField("Previous Ban Info", banHistory)
            .WithFooter("Note: This does NOT whitelist the user - they are still subject to fraud detection")
            .WithTimestamp(DateTimeOffset.UtcNow));
    }

    /// <summary>
    ///     Trade bans a user and also market bans them.
    /// </summary>
    /// <param name="user">The user to ban.</param>
    /// <param name="reason">Why they are banned.</param>
    [Command("tradeban")]
    [Summary("Apply a trade ban to a user. Also bans them from the market.")]
    [RequireStaffRank(StaffRank.Admin)]
    public Task TradeBan(IUser user, [Remainder] string reason) => ApplyTradeBanAsync(user, reason, true);

    /// <summary>
    ///     Trade bans a user, choosing whether to market ban them too.
    /// </summary>
    /// <param name="user">The user to ban.</param>
    /// <param name="marketBan">Whether to also ban them from the market.</param>
    /// <param name="reason">Why they are banned.</param>
    [Command("tradeban")]
    [Summary("Apply a trade ban to a user, with true or false for a market ban.")]
    [RequireStaffRank(StaffRank.Admin)]
    [Priority(1)]
    public Task TradeBan(IUser user, bool marketBan, [Remainder] string reason) =>
        ApplyTradeBanAsync(user, reason, marketBan);

    private async Task ApplyTradeBanAsync(IUser user, string reason, bool marketBan)
    {
        await using var db = await context.GetConnectionAsync();

        var userData = await db.Users.FirstOrDefaultAsync(u => u.UserId == user.Id);

        var banDate = DateTime.UtcNow;
        int updated;

        if (userData == null)
        {
            await db.InsertAsync(new Database.Linq.Models.Bot.User
            {
                UserId = user.Id,
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
                .Where(u => u.UserId == user.Id)
                .Set(u => u.TradeBanned, true)
                .Set(u => u.TradeBanReason, reason)
                .Set(u => u.TradeBanDate, banDate)
                .Set(u => u.MarketBanned, marketBan)
                .Set(u => u.MarketBanReason, marketBan ? reason : userData.MarketBanReason)
                .Set(u => u.MarketBanDate, marketBan ? banDate : userData.MarketBanDate)
                .UpdateAsync();
        }

        if (updated == 0)
        {
            await SendStaffAsync($"Failed to apply trade ban to {user.Mention}. Please try again.");
            return;
        }

        Serilog.Log.Warning("🔨 Trade ban applied by admin: User={UserId} ({Username}), Admin={AdminId} ({AdminUsername}), Reason={Reason}, MarketBan={MarketBan}",
            user.Id, user.Username, ctx.User.Id, ctx.User.Username, reason, marketBan);

        await SendStaffAsync(new EmbedBuilder()
            .WithTitle("🔨 Trade Ban Applied")
            .WithColor(Color.Red)
            .WithDescription($"**User:** {user.Mention} ({user.Username}#{user.Discriminator})")
            .AddField("Restrictions Applied",
                "• Trade ban: **Yes**\n" +
                $"• Market ban: **{(marketBan ? "Yes" : "No")}**",
                true)
            .AddField("Admin", ctx.User.Mention, true)
            .AddField("Reason", reason)
            .WithTimestamp(DateTimeOffset.UtcNow));
    }

    private Task SendStaffAsync(string text) =>
        SendStaffAsync(new EmbedBuilder().WithOkColor().WithDescription(text));

    private async Task SendStaffAsync(EmbedBuilder embed)
    {
        if (!await SendPrivateAsync(embed: embed))
        {
            await ErrorAsync("I couldn't DM you the result. Open your DMs to see trade admin results.");
            return;
        }

        if (ctx.Guild != null)
            await ctx.Message.AddReactionAsync("📬");
    }
}
