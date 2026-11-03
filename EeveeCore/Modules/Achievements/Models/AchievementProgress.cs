using MongoDB.Bson.Serialization.Attributes;

namespace EeveeCore.Modules.Achievements.Models;

/// <summary>
///     Represents a user's achievement progress and milestone tracking.
/// </summary>
public class AchievementProgress
{
    /// <summary>
    ///     User ID for this achievement progress.
    /// </summary>
    [BsonElement("u_id")]
    [BsonId]
    public ulong UserId { get; set; }

    /// <summary>
    ///     Dictionary tracking the highest milestone reached for each achievement.
    /// </summary>
    [BsonElement("milestones")]
    public Dictionary<string, int> Milestones { get; set; } = new();

    /// <summary>
    ///     Dictionary tracking the last notification sent for each achievement to prevent spam.
    /// </summary>
    [BsonElement("last_notified")]
    public Dictionary<string, int> LastNotified { get; set; } = new();

    /// <summary>
    ///     Dictionary tracking consecutive daily completion counts for streak bonuses.
    /// </summary>
    [BsonElement("streaks")]
    public Dictionary<string, int> Streaks { get; set; } = new();

    /// <summary>
    ///     Total loyalty points accumulated across all systems.
    /// </summary>
    [BsonElement("loyalty_points")]
    public int LoyaltyPoints { get; set; }

    /// <summary>
    ///     Last login date for daily streak tracking.
    /// </summary>
    [BsonElement("last_login")]
    public DateTime? LastLogin { get; set; }

    /// <summary>
    ///     Current daily streak count.
    /// </summary>
    [BsonElement("daily_streak")]
    public int DailyStreak { get; set; }

    /// <summary>
    ///     Dictionary tracking weekly activity completion.
    /// </summary>
    [BsonElement("weekly_completion")]
    public Dictionary<string, bool> WeeklyCompletion { get; set; } = new();

    /// <summary>
    ///     Last week reset date for weekly tracking.
    /// </summary>
    [BsonElement("last_week_reset")]
    public DateTime? LastWeekReset { get; set; }
}

/// <summary>
///     Represents an achievement milestone completion event.
/// </summary>
public class MilestoneCompletion
{
    /// <summary>
    ///     Achievement type that was completed.
    /// </summary>
    public string Achievement { get; set; } = string.Empty;

    /// <summary>
    ///     Milestone value reached.
    /// </summary>
    public int Milestone { get; set; }

    /// <summary>
    ///     User's current value for this achievement.
    /// </summary>
    public int CurrentValue { get; set; }

    /// <summary>
    ///     MewCoin reward amount.
    /// </summary>
    public int MewCoinReward { get; set; }

    /// <summary>
    ///     Redeem token reward amount.
    /// </summary>
    public int RedeemReward { get; set; }

    /// <summary>
    ///     Skin token reward amount.
    /// </summary>
    public int SkinTokenReward { get; set; }

    /// <summary>
    ///     Whether this milestone grants special rewards.
    /// </summary>
    public bool HasSpecialReward { get; set; }

    /// <summary>
    ///     Display name for the achievement.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    ///     Color for the milestone tier.
    /// </summary>
    public uint TierColor { get; set; }
}

/// <summary>
///     Represents daily streak bonus configuration.
/// </summary>
public class StreakBonus
{
    /// <summary>
    ///     Number of consecutive days required.
    /// </summary>
    public int RequiredDays { get; set; }

    /// <summary>
    ///     MewCoin bonus amount.
    /// </summary>
    public int MewCoinBonus { get; set; }

    /// <summary>
    ///     Redeem token bonus amount.
    /// </summary>
    public int RedeemBonus { get; set; }

    /// <summary>
    ///     XP multiplier bonus.
    /// </summary>
    public double XpMultiplier { get; set; }

    /// <summary>
    ///     Special title awarded at this streak.
    /// </summary>
    public string? SpecialTitle { get; set; }
}

/// <summary>
///     Represents loyalty point reward tiers.
/// </summary>
public class LoyaltyTier
{
    /// <summary>
    ///     Points required to reach this tier.
    /// </summary>
    public int RequiredPoints { get; set; }

    /// <summary>
    ///     Tier name for display.
    /// </summary>
    public string TierName { get; set; } = string.Empty;

    /// <summary>
    ///     Global XP multiplier for this tier.
    /// </summary>
    public double XpMultiplier { get; set; }

    /// <summary>
    ///     Shop discount percentage.
    /// </summary>
    public int ShopDiscount { get; set; }

    /// <summary>
    ///     Special color for this tier.
    /// </summary>
    public uint TierColor { get; set; }

    /// <summary>
    ///     Monthly MewCoin bonus.
    /// </summary>
    public int MonthlyBonus { get; set; }
}

/// <summary>
///     A trainer's value for one achievement.
/// </summary>
public class AchievementScore
{
    /// <summary>
    ///     The trainer's user ID.
    /// </summary>
    public ulong UserId { get; init; }

    /// <summary>
    ///     The achievement's value.
    /// </summary>
    public long Value { get; init; }
}

/// <summary>
///     A row of an achievement leaderboard.
/// </summary>
/// <param name="Rank">The trainer's rank.</param>
/// <param name="UserId">The trainer's user ID.</param>
/// <param name="Name">The trainer's nickname, or null when they have none.</param>
/// <param name="Value">The achievement's value.</param>
public record AchievementLeaderboardEntry(int Rank, ulong UserId, string? Name, long Value);