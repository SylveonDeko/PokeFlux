using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Achievements.Common;
using EeveeCore.Modules.Achievements.Models;
using EeveeCore.Modules.Achievements.Services;
using LinqToDB.Async;
using Serilog;

namespace EeveeCore.Modules.Achievements;

/// <summary>
///     Achievement progress, milestones and loyalty.
/// </summary>
[Group("achievements")]
[Alias("achievement", "ach")]
[Name("Achievements")]
public class AchievementsModule : EeveeModule<AchievementService>
{
    private const int LeaderboardSize = 10;
    private static readonly string[] Categories = AchievementService.Categories;

    /// <summary>
    ///     Shows a player's achievements.
    /// </summary>
    /// <param name="user">The player, or you.</param>
    [Command("view")]
    [Priority(1)]
    [Summary("View your achievement progress and milestones.")]
    public async Task View(User? user = null) => await ViewAsync(null, user ?? ctx.User);

    /// <summary>
    ///     Shows a player's achievements in one category.
    /// </summary>
    /// <param name="category">battle, breeding, catching, special, market, activities or events.</param>
    /// <param name="user">The player, or you.</param>
    [Command("view")]
    [Summary("View your achievements in one category.")]
    public async Task View(string category, User? user = null)
    {
        category = category.ToLower();
        if (!Categories.Contains(category))
        {
            await ErrorAsync($"`{category}` is not a category. Pick one of: {string.Join(", ", Categories)}.");
            return;
        }

        await ViewAsync(category, user ?? ctx.User);
    }

    /// <summary>
    ///     Shows a player's detailed progress on one achievement.
    /// </summary>
    /// <param name="user">The player.</param>
    /// <param name="achievement">The achievement.</param>
    [Command("progress")]
    [Priority(1)]
    [Summary("View another player's progress on an achievement.")]
    public async Task Progress(User user, [Remainder] string achievement) => await ProgressAsync(achievement, user);

    /// <summary>
    ///     Shows your detailed progress on one achievement.
    /// </summary>
    /// <param name="achievement">The achievement.</param>
    [Command("progress")]
    [Summary("View your detailed progress on an achievement.")]
    public async Task Progress([Remainder] string achievement) => await ProgressAsync(achievement, ctx.User);

    /// <summary>
    ///     Shows a player's loyalty points and tier.
    /// </summary>
    /// <param name="user">The player, or you.</param>
    [Command("loyalty")]
    [Summary("View your loyalty points and tier benefits.")]
    public async Task Loyalty(User? user = null)
    {
        try
        {
            var targetUser = user ?? ctx.User;
            var loyalty = await Service.GetLoyaltyAsync(targetUser.Id);
            var loyaltyPoints = loyalty.LoyaltyPoints;
            var dailyStreak = loyalty.DailyStreak;
            var tier = CalculateLoyaltyTier(loyaltyPoints);

            var embed = new EmbedBuilder()
                .WithTitle($"💎 {targetUser.Username}'s Loyalty Status")
                .WithColor(tier.TierColor)
                .WithThumbnailUrl(targetUser.GetAvatarOrDefaultUrl())
                .AddField("🏅 Current Tier",
                    $"**{tier.TierName}**\n" +
                    $"Points: {loyaltyPoints:N0}\n" +
                    $"XP Multiplier: {tier.XpMultiplier:F1}x\n" +
                    $"Shop Discount: {tier.ShopDiscount}%", true)
                .AddField("🔥 Daily Streak",
                    $"**{dailyStreak} days**\n" +
                    $"Next bonus at: {GetNextStreakBonus(dailyStreak)} days", true);

            if (GetNextLoyaltyTier(loyaltyPoints) is { } nextTier)
                embed.AddField("🎯 Next Tier",
                    $"**{nextTier.TierName}**\n" +
                    $"Points needed: {nextTier.RequiredPoints - loyaltyPoints:N0}\n" +
                    $"Bonus: {nextTier.MonthlyBonus:N0} monthly Coins");

            embed.WithFooter("Earn loyalty points by completing achievements!");
            await SendAsync(embed: embed);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error in view loyalty command for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while retrieving loyalty data.");
        }
    }

    /// <summary>
    ///     Shows the leaderboard for one achievement.
    /// </summary>
    /// <param name="achievement">The achievement.</param>
    [Command("leaderboard")]
    [Alias("lb")]
    [Summary("View the leaderboard for an achievement.")]
    public async Task Leaderboard([Remainder] string achievement)
    {
        if (await FindAchievementAsync(achievement) is not { } key)
            return;

        try
        {
            var entries = await Service.GetLeaderboardAsync(key, LeaderboardSize);

            var embed = new EmbedBuilder()
                .WithTitle($"🏆 {AchievementConstants.AchievementDisplayNames.GetValueOrDefault(key, key)} Leaderboard")
                .WithColor(AchievementConstants.GoldTierColor)
                .WithDescription(entries.Count == 0
                    ? "No trainers have made progress on this achievement yet."
                    : string.Join("\n", entries.Select(e =>
                        $"{RankLabel(e.Rank)} {(e.Name is null ? "*Hidden trainer*" : $"**{e.Name}**")}: {e.Value:N0}")));

            if (entries.All(e => e.UserId != ctx.User.Id)
                && await Service.GetLeaderboardRankAsync(key, ctx.User.Id) is { } mine)
                embed.WithFooter($"Your rank: #{mine.Rank:N0} with {mine.Value:N0}");

            await SendAsync(embed: embed);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error in achievement leaderboard command for achievement {Achievement}", key);
            await ErrorAsync("An error occurred while retrieving the leaderboard.");
        }
    }

    private static string RankLabel(int rank) => rank switch
    {
        1 => "🥇",
        2 => "🥈",
        3 => "🥉",
        _ => $"`#{rank}`"
    };

    private async Task ViewAsync(string? category, User targetUser)
    {
        try
        {
            var userId = targetUser.Id;
            var achievementValues = await Service.GetAchievementValuesAsync(userId);
            var loyalty = await Service.GetLoyaltyAsync(userId);

            if (achievementValues.Count == 0)
            {
                await ErrorAsync("No achievement data found for this user.");
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle($"🏆 {targetUser.Username}'s Achievements")
                .WithColor(AchievementConstants.GoldTierColor)
                .WithThumbnailUrl(targetUser.GetAvatarOrDefaultUrl());

            if (category != null)
                embed.WithDescription($"**Category:** {char.ToUpper(category[0]) + category[1..]}");

            await AddAchievementFieldsAsync(embed, FilterAchievementsByCategory(achievementValues, category), userId);

            embed.AddField("📊 Summary",
                $"Total Milestones: {await GetTotalMilestonesAsync(userId):N0}\n" +
                $"Loyalty Points: {loyalty.LoyaltyPoints:N0}\n" +
                $"Daily Streak: {loyalty.DailyStreak} days");

            await SendAsync(embed: embed);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error in view achievements command for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while retrieving achievement data.");
        }
    }

    private async Task ProgressAsync(string achievement, User targetUser)
    {
        if (await FindAchievementAsync(achievement) is not { } key)
            return;

        try
        {
            var userId = targetUser.Id;
            var milestones = AchievementConstants.Milestones[key];
            var achievementValues = await Service.GetAchievementValuesAsync(userId);

            var currentValue = achievementValues.GetValueOrDefault(key, 0);
            var lastMilestone = await Service.GetLastMilestoneAsync(userId, key);
            var displayName = AchievementConstants.AchievementDisplayNames.GetValueOrDefault(key, key);

            var nextMilestone = milestones.FirstOrDefault(m => m > currentValue);
            var progressToNext = nextMilestone > 0 ? (double)currentValue / nextMilestone : 1.0;

            var embed = new EmbedBuilder()
                .WithTitle($"📈 {displayName} Progress")
                .WithColor(AchievementConstants.GetMilestoneColor(
                    Array.IndexOf(milestones, lastMilestone), milestones.Length))
                .WithThumbnailUrl(targetUser.GetAvatarOrDefaultUrl())
                .AddField("Current Progress",
                    $"**Value:** {currentValue:N0}\n" +
                    $"**Last Milestone:** {lastMilestone:N0}\n" +
                    (nextMilestone > 0
                        ? $"**Next Milestone:** {nextMilestone:N0}\n{CreateProgressBar(progressToNext)}"
                        : "**All milestones completed!**"));

            var completedMilestones = milestones.Where(m => currentValue >= m).ToArray();
            if (completedMilestones.Length > 0)
                embed.AddField($"✅ Completed Milestones ({completedMilestones.Length})",
                    string.Join(", ", completedMilestones.Select(m => $"`{m:N0}`")));

            var upcomingMilestones = milestones.Where(m => currentValue < m).Take(5).ToArray();
            if (upcomingMilestones.Length > 0)
                embed.AddField("🎯 Upcoming Milestones",
                    string.Join(", ", upcomingMilestones.Select(m => $"`{m:N0}`")));

            await SendAsync(embed: embed);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error in view progress command for achievement {Achievement}", key);
            await ErrorAsync("An error occurred while retrieving progress data.");
        }
    }

    private async Task<string?> FindAchievementAsync(string input)
    {
        var query = input.Trim().ToLower();
        var asKey = query.Replace(' ', '_').Replace('-', '_');
        if (AchievementConstants.Milestones.ContainsKey(asKey))
            return asKey;

        var byName = AchievementConstants.AchievementDisplayNames
            .FirstOrDefault(a => a.Value.Equals(query, StringComparison.OrdinalIgnoreCase)).Key;
        if (byName != null && AchievementConstants.Milestones.ContainsKey(byName))
            return byName;

        var candidates = AchievementConstants.Milestones.Keys
            .Select(k => (Key: k, Name: AchievementConstants.AchievementDisplayNames.GetValueOrDefault(k, k)))
            .ToList();

        var partial = candidates
            .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Key.Contains(asKey))
            .ToList();
        if (partial.Count == 1)
            return partial[0].Key;

        var suggestions = (partial.Count > 0 ? partial : candidates)
            .OrderBy(c => Math.Min(c.Name.ToLower().LevenshteinDistance(query), c.Key.LevenshteinDistance(asKey)))
            .Take(3)
            .Select(c => $"`{c.Name}`");

        await ErrorAsync($"There is no achievement called `{input}`. Did you mean {string.Join(", ", suggestions)}?");
        return null;
    }

    private static Dictionary<string, int> FilterAchievementsByCategory(Dictionary<string, int> achievements,
        string? category) => AchievementService.FilterByCategory(achievements, category);

    private async Task AddAchievementFieldsAsync(EmbedBuilder embed, Dictionary<string, int> achievements, ulong userId)
    {
        const int maxFieldsPerEmbed = 10;
        var addedFields = 0;

        foreach (var (achievement, value) in achievements.OrderByDescending(x => x.Value).Take(maxFieldsPerEmbed))
        {
            if (!AchievementConstants.Milestones.TryGetValue(achievement, out var milestones))
                continue;

            var displayName = AchievementConstants.AchievementDisplayNames.GetValueOrDefault(achievement, achievement);
            var lastMilestone = await Service.GetLastMilestoneAsync(userId, achievement);
            var nextMilestone = milestones.FirstOrDefault(m => m > value);

            var fieldValue = $"**Current:** {value:N0}\n";
            if (lastMilestone > 0)
                fieldValue += $"**Last Milestone:** {lastMilestone:N0}\n";
            fieldValue += nextMilestone > 0 ? $"**Next:** {nextMilestone:N0}" : "**Status:** All completed!";

            embed.AddField(displayName, fieldValue, true);
            addedFields++;
        }

        if (addedFields == 0)
            embed.AddField("No Achievements", "No achievement data found for the selected category.");
    }

    private static string CreateProgressBar(double progress, int length = 10)
    {
        var filled = (int)(progress * length);
        return $"`[{new string('█', filled)}{new string('░', length - filled)}]` {progress:P1}";
    }

    private static LoyaltyTier CalculateLoyaltyTier(int points) => AchievementService.GetLoyaltyTier(points);

    private static LoyaltyTier? GetNextLoyaltyTier(int currentPoints) =>
        AchievementService.GetNextLoyaltyTier(currentPoints);

    private static int GetNextStreakBonus(int currentStreak) => AchievementService.GetNextStreakBonus(currentStreak);

    private Task<int> GetTotalMilestonesAsync(ulong userId) => Service.GetTotalMilestonesAsync(userId);
}
