namespace EeveeCore.Modules.Achievements.Common;

/// <summary>
///     Constants and configuration for the achievement system including milestones and rewards.
/// </summary>
public static class AchievementConstants
{
    #region Achievement Categories

    /// <summary>
    ///     Achievement categories for organization and display.
    /// </summary>
    public enum AchievementCategory
    {
        /// <summary>Battle-related achievements including duels and NPC fights.</summary>
        Battle,
        /// <summary>Breeding-related achievements including IV breeding.</summary>
        Breeding,
        /// <summary>Pokemon catching achievements including type-specific catching.</summary>
        Catching,
        /// <summary>Special achievements and milestones.</summary>
        Special,
        /// <summary>Market and trading related achievements.</summary>
        Market,
        /// <summary>General activity achievements like fishing and missions.</summary>
        Activities,
        /// <summary>Event-specific achievements and seasonal content.</summary>
        Events
    }

    #endregion

    #region Milestone Definitions

    /// <summary>
    ///     Milestone thresholds for each achievement type.
    /// </summary>
    public static readonly Dictionary<string, int[]> Milestones = new()
    {
        // Battle Achievements
        ["duel_party_wins"] = [10, 50, 100, 500, 1000, 3000, 5000, 10000, 50000],
        ["duel_single_wins"] = [10, 50, 100, 500, 1000, 3000, 5000, 10000, 50000],
        ["duel_inverse_wins"] = [10, 50, 100, 500, 1000, 3000, 5000, 10000, 50000],
        ["npc_wins"] = [10, 50, 100, 500, 1000, 3000, 5000, 10000, 50000],
        ["gym_wins"] = [10, 50, 100, 500],
        ["npc_duels"] = [10, 50, 100, 500, 1000, 3000, 5000, 10000, 50000],
        ["duels_total"] = [10, 50, 100, 500, 1000, 3000, 5000, 10000, 50000],
        ["duel_total_xp"] = [1000, 5000, 10000, 50000, 100000, 500000, 1000000, 5000000],

        // Breeding Achievements
        ["breed_titan"] = [1, 5, 10, 15, 30, 50, 100, 150, 200, 300],
        ["breed_hexa"] = [1, 5, 10, 15, 30, 50, 100, 150, 200, 300],
        ["breed_penta"] = [1, 5, 10, 15, 30, 50, 100, 150, 200, 400],
        ["breed_quad"] = [1, 5, 10, 50, 100, 200, 400, 800, 1200, 2000],
        ["breed_success"] = [10, 25, 50, 75, 100, 150, 300, 450, 500, 650, 800, 1000, 1200, 1450, 1700, 2000, 2350, 2700, 3000, 5000, 10000, 25000, 50000],
        ["shiny_bred"] = [1, 5, 10, 50, 100, 200, 400, 800, 1200, 2000],
        ["shadow_bred"] = [1, 5, 10, 50, 100, 200, 400, 800, 1200, 2000],

        // Catching Achievements
        ["pokemon_caught"] = [10, 50, 100, 500, 1000, 1500, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000, 6500, 7000, 7500, 8000, 8500, 9000, 9500, 10000, 15000, 20000, 25000, 30000, 35000, 40000, 45000, 50000, 55000, 60000, 65000, 70000, 75000, 80000, 85000, 90000, 95000, 100000, 150000, 200000, 250000, 300000],
        ["shiny_caught"] = [1, 5, 10, 50, 100, 200, 400, 800, 1200, 2000],
        ["shadow_caught"] = [1, 5, 10, 50, 100, 200, 400, 800, 1200, 2000],

        // Type-Specific Catching
        ["pokemon_normal"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_fighting"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_flying"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_poison"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_ground"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_rock"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_bug"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_ghost"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_steel"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_fire"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_water"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_grass"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_electric"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_psychic"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_ice"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_dragon"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_dark"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],
        ["pokemon_fairy"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],

        // Special Achievements
        ["dex_complete"] = [1],

        // Market Achievements
        ["market_purchased"] = [10, 50, 100, 500, 1000, 3000, 5000, 10000, 25000, 50000],
        ["market_sold"] = [10, 50, 100, 500, 1000, 3000, 5000, 10000, 25000, 50000],
        ["pokemon_released"] = [10, 50, 100, 500, 1000, 3000, 5000, 10000, 25000, 50000],
        ["pokemon_released_ivtotal"] = [1000, 2000, 3500, 5000, 7500, 10000, 13500, 15000, 17500, 20000],

        // Activity Achievements
        ["fishing_success"] = [10, 50, 100, 300, 500, 1000, 3000, 5000, 10000, 50000],
        ["missions"] = [10, 50, 100, 300, 500, 1000, 3000, 5000, 10000, 50000],
        ["votes"] = [10, 30, 50, 100, 250, 500, 1000, 2000, 3000, 5000],

        // Donation/Chest Achievements
        ["chests_legend"] = [5, 30, 100, 200, 400, 800, 1600, 3000],
        ["chests_voucher"] = [1, 5, 10, 15],
        ["chests_mythic"] = [5, 30, 100, 200, 400, 800, 1600, 3000],
        ["chests_rare"] = [10, 50, 100, 500, 1000, 3000],
        ["chests_common"] = [10, 50, 100, 500, 1000, 3000],
        ["redeems_used"] = [10, 30, 100, 400, 800, 1200, 2000, 3000, 5000, 10000, 20000],
        ["donation_amount"] = [10, 50, 100, 500, 1000],

        // Event Achievements
        ["unown_event"] = [1, 5, 10, 15, 30, 50, 100, 150, 200, 400, 800, 1200, 2000],

        // Game Achievements
        ["game_wordsearch"] = [1, 5, 10, 25, 50, 100, 250, 500, 1000],
        ["game_slots"] = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500],
        ["game_slots_win"] = [1, 5, 10, 25, 50, 100, 200, 400, 800]
    };

    #endregion

    #region Reward Configuration

    /// <summary>
    ///     Milestones that grant MewCoin rewards.
    /// </summary>
    public static readonly Dictionary<string, object> RewardMilestones = new()
    {
        // Fishing
        ["fishing_success"] = new int[] { 100, 500, 1000, 3000, 5000, 10000, 50000 },

        // Breeding - all milestones
        ["breed_hexa"] = "all",
        ["breed_titan"] = "all", 
        ["breed_penta"] = "all",
        ["breed_quad"] = "all",
        ["breed_success"] = new int[] { 500, 3000, 10000 },

        // Catching - all milestones
        ["pokemon_caught"] = "all",
        ["pokemon_normal"] = "all", ["pokemon_fighting"] = "all", ["pokemon_flying"] = "all",
        ["pokemon_poison"] = "all", ["pokemon_ground"] = "all", ["pokemon_rock"] = "all",
        ["pokemon_bug"] = "all", ["pokemon_ghost"] = "all", ["pokemon_steel"] = "all",
        ["pokemon_fire"] = "all", ["pokemon_water"] = "all", ["pokemon_grass"] = "all",
        ["pokemon_electric"] = "all", ["pokemon_psychic"] = "all", ["pokemon_ice"] = "all",
        ["pokemon_dragon"] = "all", ["pokemon_dark"] = "all", ["pokemon_fairy"] = "all",

        // Market
        ["market_sold"] = new int[] { 50, 10000 },

        // Chests
        ["chests_common"] = new int[] { 100, 400, 800, 3000 },
        ["chests_rare"] = new int[] { 100, 400, 800, 3000 },
        ["chests_mythic"] = new int[] { 100, 400, 800, 3000 },
        ["chests_legend"] = new int[] { 100, 400, 800, 3000 },
        ["donation_amount"] = "all",

        // Releasing
        ["pokemon_released_ivtotal"] = new int[] { 1000, 2000, 5000, 10000, 20000 },
        ["pokemon_released"] = new int[] { 1000, 5000, 10000 },

        // Games
        ["game_wordsearch"] = new int[] { 5, 25, 100, 500 },
        ["game_slots"] = new int[] { 25, 100, 500, 2500 },
        ["game_slots_win"] = new int[] { 10, 50, 200, 800 }
    };

    /// <summary>
    ///     Milestones that grant Redeem token rewards.
    /// </summary>
    public static readonly Dictionary<string, int[]> RedeemMilestones = new()
    {
        ["pokemon_caught"] = [1000, 2000, 3000, 4000, 5000, 6000, 7000, 8000, 9000, 10000, 15000, 20000, 25000, 30000, 35000, 40000, 45000, 50000, 55000, 60000, 65000, 70000, 75000, 80000, 85000, 90000, 95000, 100000, 150000, 200000, 250000, 300000],
        ["fishing_success"] = [1000, 3000, 5000, 10000, 50000],
        ["breed_titan"] = [1, 150],
        ["breed_hexa"] = [1, 150],
        ["breed_penta"] = [5],

        // Type-specific catching
        ["pokemon_normal"] = [100, 500, 1000, 2000, 5000], ["pokemon_fighting"] = [100, 500, 1000, 2000, 5000],
        ["pokemon_flying"] = [100, 500, 1000, 2000, 5000], ["pokemon_poison"] = [100, 500, 1000, 2000, 5000],
        ["pokemon_ground"] = [100, 500, 1000, 2000, 5000], ["pokemon_rock"] = [100, 500, 1000, 2000, 5000],
        ["pokemon_bug"] = [100, 500, 1000, 2000, 5000], ["pokemon_ghost"] = [100, 500, 1000, 2000, 5000],
        ["pokemon_steel"] = [100, 500, 1000, 2000, 5000], ["pokemon_fire"] = [100, 500, 1000, 2000, 5000],
        ["pokemon_water"] = [100, 500, 1000, 2000, 5000], ["pokemon_grass"] = [100, 500, 1000, 2000, 5000],
        ["pokemon_electric"] = [100, 500, 1000, 2000, 5000], ["pokemon_psychic"] = [100, 500, 1000, 2000, 5000],
        ["pokemon_ice"] = [100, 500, 1000, 2000, 5000], ["pokemon_dragon"] = [100, 500, 1000, 2000, 5000],
        ["pokemon_dark"] = [100, 500, 1000, 2000, 5000], ["pokemon_fairy"] = [100, 500, 1000, 2000, 5000],

        // Games
        ["game_wordsearch"] = [25, 100, 500],
        ["game_slots"] = [100, 500, 2500],
        ["game_slots_win"] = [50, 200, 800]
    };

    /// <summary>
    ///     Milestones that grant Skin token rewards.
    /// </summary>
    public static readonly Dictionary<string, int[]> SkinMilestones = new()
    {
        ["pokemon_caught"] = [80000, 200000],
        ["fishing_success"] = [10000, 30000],
        ["pokemon_released_ivtotal"] = [1000, 10000],
        ["chests_legend"] = [400, 3000],
        ["chests_mythic"] = [400, 3000],
        ["chests_rare"] = [800, 3000],
        ["breed_titan"] = [10, 100],
        ["breed_hexa"] = [10, 100],
        ["game_wordsearch"] = [100, 500],
        ["game_slots_win"] = [200, 800]
    };

    #endregion

    #region Reward Amounts

    /// <summary>
    ///     Base reward amounts for different achievement types.
    /// </summary>
    public static readonly Dictionary<string, (int MewCoins, double Redeems)> BaseRewards = new()
    {
        ["pokemon_caught"] = (10, 0.01),
        ["fishing_success"] = (50, 0.01),
        ["breed_titan"] = (50, 1.2),
        ["breed_hexa"] = (50, 1.2),
        ["breed_penta"] = (25, 0.5),
        ["breed_quad"] = (10, 0.1),

        // Type-specific catching
        ["pokemon_normal"] = (10, 0.01), ["pokemon_fighting"] = (10, 0.01), ["pokemon_flying"] = (10, 0.01),
        ["pokemon_poison"] = (10, 0.01), ["pokemon_ground"] = (10, 0.01), ["pokemon_rock"] = (10, 0.01),
        ["pokemon_bug"] = (10, 0.01), ["pokemon_ghost"] = (10, 0.01), ["pokemon_steel"] = (10, 0.01),
        ["pokemon_fire"] = (10, 0.01), ["pokemon_water"] = (10, 0.01), ["pokemon_grass"] = (10, 0.01),
        ["pokemon_electric"] = (10, 0.01), ["pokemon_psychic"] = (10, 0.01), ["pokemon_ice"] = (10, 0.01),
        ["pokemon_dragon"] = (10, 0.01), ["pokemon_dark"] = (10, 0.01), ["pokemon_fairy"] = (10, 0.01),

        // Games
        ["game_wordsearch"] = (5, 0.002),
        ["game_slots"] = (2, 0.001),
        ["game_slots_win"] = (10, 0.005),

        // Market and others
        ["market_sold"] = (5, 0.001),
        ["pokemon_released"] = (1, 0.0001),
        ["donation_amount"] = (100, 0.1),
        ["chests_common"] = (2, 0.001),
        ["chests_rare"] = (5, 0.002),
        ["chests_mythic"] = (10, 0.005),
        ["chests_legend"] = (25, 0.01)
    };

    /// <summary>
    ///     Maximum reward caps to prevent excessive payouts.
    /// </summary>
    public const int MaxMewCoinReward = 50000;
    
    /// <summary>
    ///     Maximum redeem tokens that can be awarded from a single milestone.
    /// </summary>
    public const int MaxRedeemReward = 300;
    
    /// <summary>
    ///     Maximum skin tokens that can be awarded from a single milestone.
    /// </summary>
    public const int MaxSkinTokenReward = 5;

    #endregion

    #region Pokemon Type Mapping

    /// <summary>
    ///     Maps Pokemon type IDs to achievement names.
    /// </summary>
    public static readonly Dictionary<int, string> TypeToAchievement = new()
    {
        { 1, "pokemon_normal" }, { 2, "pokemon_fighting" }, { 3, "pokemon_flying" },
        { 4, "pokemon_poison" }, { 5, "pokemon_ground" }, { 6, "pokemon_rock" },
        { 7, "pokemon_bug" }, { 8, "pokemon_ghost" }, { 9, "pokemon_steel" },
        { 10, "pokemon_fire" }, { 11, "pokemon_water" }, { 12, "pokemon_grass" },
        { 13, "pokemon_electric" }, { 14, "pokemon_psychic" }, { 15, "pokemon_ice" },
        { 16, "pokemon_dragon" }, { 17, "pokemon_dark" }, { 18, "pokemon_fairy" }
    };

    #endregion

    #region Display Configuration

    /// <summary>
    ///     Display names for achievements.
    /// </summary>
    public static readonly Dictionary<string, string> AchievementDisplayNames = new()
    {
        ["pokemon_caught"] = "Pokemon Caught",
        ["shiny_caught"] = "Shiny Pokemon Caught",
        ["shadow_caught"] = "Shadow Pokemon Caught",
        ["breed_success"] = "Successful Breeds",
        ["breed_hexa"] = "Perfect IV Breeds",
        ["breed_penta"] = "5IV Breeds", 
        ["breed_quad"] = "4IV Breeds",
        ["breed_titan"] = "Titan Breeds",
        ["shiny_bred"] = "Shiny Breeds",
        ["shadow_bred"] = "Shadow Breeds",
        ["duel_party_wins"] = "Party Duel Wins",
        ["duel_single_wins"] = "Single Duel Wins",
        ["duel_inverse_wins"] = "Inverse Duel Wins",
        ["npc_wins"] = "NPC Battle Wins",
        ["npc_duels"] = "NPC Duels",
        ["duels_total"] = "Total Duels",
        ["duel_total_xp"] = "Duel XP Earned",
        ["dex_complete"] = "Pokedex Completed",
        ["pokemon_released_ivtotal"] = "Released IV Total",
        ["fishing_success"] = "Successful Fishing",
        ["market_sold"] = "Market Sales",
        ["game_wordsearch"] = "Word Search Games",
        ["game_slots"] = "Slot Machine Plays",
        ["game_slots_win"] = "Slot Machine Wins"
    };

    /// <summary>
    ///     Emoji representations for achievement categories.
    /// </summary>
    public static readonly Dictionary<AchievementCategory, string> CategoryEmojis = new()
    {
        [AchievementCategory.Battle] = "⚔️",
        [AchievementCategory.Breeding] = "🥚",
        [AchievementCategory.Catching] = "⚡",
        [AchievementCategory.Special] = "✨",
        [AchievementCategory.Market] = "💰",
        [AchievementCategory.Activities] = "🎮",
        [AchievementCategory.Events] = "🎉"
    };

    #endregion

    #region Achievement Colors

    /// <summary>
    ///     Colors for different milestone tiers.
    /// </summary>
    public const uint BronzeTierColor = 0xCD7F32;
    
    /// <summary>
    ///     Silver tier color for achievement milestones.
    /// </summary>
    public const uint SilverTierColor = 0xC0C0C0;
    
    /// <summary>
    ///     Gold tier color for achievement milestones.
    /// </summary>
    public const uint GoldTierColor = 0xFFD700;
    
    /// <summary>
    ///     Platinum tier color for achievement milestones.
    /// </summary>
    public const uint PlatinumTierColor = 0xE5E4E2;
    
    /// <summary>
    ///     Diamond tier color for achievement milestones.
    /// </summary>
    public const uint DiamondTierColor = 0xB9F2FF;

    /// <summary>
    ///     Gets the color for a milestone based on its position in the achievement.
    /// </summary>
    public static uint GetMilestoneColor(int milestoneIndex, int totalMilestones)
    {
        var percentage = (double)milestoneIndex / totalMilestones;
        return percentage switch
        {
            < 0.2 => BronzeTierColor,
            < 0.4 => SilverTierColor,
            < 0.6 => GoldTierColor,
            < 0.8 => PlatinumTierColor,
            _ => DiamondTierColor
        };
    }

    #endregion
}