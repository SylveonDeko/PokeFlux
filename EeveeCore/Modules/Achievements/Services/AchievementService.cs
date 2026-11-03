using EeveeCore.Common.ModuleBehaviors;
using EeveeCore.Modules.Achievements.Common;
using EeveeCore.Modules.Achievements.Models;
using EeveeCore.Services.Impl;
using LinqToDB;
using LinqToDB.Async;
using Serilog;

namespace EeveeCore.Modules.Achievements.Services;

/// <summary>
///     Service for tracking achievements, milestones, and cross-system rewards.
/// </summary>
public class AchievementService(
    IMongoService mongoService,
    LinqToDbConnectionProvider dbProvider) : INService, IReadyExecutor
{

    /// <summary>
    ///     Initializes the achievement service and registers event handlers.
    /// </summary>
    public async Task OnReadyAsync()
    {
        await Task.CompletedTask;
        RegisterEventHandlers();
    }

    #region Event Registration

    /// <summary>
    ///     Registers event handlers for tracking achievements across all systems.
    /// </summary>
    private void RegisterEventHandlers()
    {
        // These would be registered from other services when they fire events
        // For now, we'll create public methods that other services can call
    }

    #endregion

    #region Achievement Tracking

    /// <summary>
    ///     Updates an achievement value for a user and checks for milestone completions.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="achievement">The achievement type.</param>
    /// <param name="increment">The amount to increment by.</param>
    /// <param name="context">The channel to announce reached milestones in, if any.</param>
    public async Task UpdateAchievementAsync(ulong userId, string achievement, int increment = 1, Channel? context = null)
    {
        try
        {
            // Get current achievement value from PostgreSQL
            await using var db = await dbProvider.GetConnectionAsync();
            var userAchievement = await db.Achievements.FirstOrDefaultAsync(a => a.UserId == userId);
            
            if (userAchievement == null)
            {
                // Create new achievement record
                userAchievement = new Database.Linq.Models.Pokemon.Achievement
                {
                    UserId = userId
                };
                await db.InsertAsync(userAchievement);
            }

            // Update the specific achievement field
            var currentValue = GetAchievementValue(userAchievement, achievement);
            var newValue = currentValue + increment;
            await SetAchievementValue(userAchievement, achievement, newValue);

            // Check for milestone completions
            await CheckMilestonesAsync(userId, achievement, newValue, context);

            // Update daily streak and loyalty points
            await UpdateDailyActivityAsync(userId);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error updating achievement {Achievement} for user {UserId}", achievement, userId);
        }
    }

    /// <summary>
    ///     Gets the current value for a specific achievement from the achievement record.
    /// </summary>
    private static int GetAchievementValue(Database.Linq.Models.Pokemon.Achievement achievement, string achievementType)
    {
        return achievementType switch
        {
            "pokemon_caught" => achievement.PokemonCaught,
            "shiny_caught" => achievement.ShinyCaught,
            "shadow_caught" => achievement.ShadowCaught,
            "breed_success" => achievement.BreedSuccess,
            "breed_hexa" => achievement.BreedHexa,
            "breed_penta" => achievement.BreedPenta,
            "breed_quad" => achievement.BreedQuad,
            "breed_titan" => achievement.BreedTitan,
            "shiny_bred" => achievement.ShinyBred,
            "shadow_bred" => achievement.ShadowBred,
            "duel_party_wins" => achievement.DuelPartyWins,
            "duel_single_wins" => achievement.DuelSingleWins,
            "duel_inverse_wins" => achievement.DuelInverseWins,
            "npc_wins" => achievement.NpcWins,
            "gym_wins" => achievement.GymWins,
            "npc_duels" => achievement.NpcDuels,
            "duels_total" => achievement.DuelsTotal,
            "duel_total_xp" => (int)achievement.DuelTotalXp,
            "fishing_success" => achievement.FishingSuccess,
            "missions" => achievement.Missions,
            "votes" => achievement.Votes,
            "market_purchased" => achievement.MarketPurchased,
            "market_sold" => achievement.MarketSold,
            "pokemon_released" => achievement.PokemonReleased,
            "pokemon_released_ivtotal" => achievement.PokemonReleasedIvTotal,
            "chests_legend" => achievement.ChestsLegend,
            "chests_mythic" => achievement.ChestsMythic,
            "chests_rare" => achievement.ChestsRare,
            "chests_common" => achievement.ChestsCommon,
            "chests_voucher" => achievement.ChestsVoucher,
            "redeems_used" => achievement.RedeemsUsed,
            "donation_amount" => achievement.DonationAmount,
            "unown_event" => achievement.UnownEvent,
            
            // Game achievements
            "game_wordsearch" => achievement.GameWordsearch,
            "game_slots" => achievement.GameSlots,
            "game_slots_win" => achievement.GameSlotsWin,
            
            // Type-specific catching
            "pokemon_normal" => achievement.PokemonNormal,
            "pokemon_fighting" => achievement.PokemonFighting,
            "pokemon_flying" => achievement.PokemonFlying,
            "pokemon_poison" => achievement.PokemonPoison,
            "pokemon_ground" => achievement.PokemonGround,
            "pokemon_rock" => achievement.PokemonRock,
            "pokemon_bug" => achievement.PokemonBug,
            "pokemon_ghost" => achievement.PokemonGhost,
            "pokemon_steel" => achievement.PokemonSteel,
            "pokemon_fire" => achievement.PokemonFire,
            "pokemon_water" => achievement.PokemonWater,
            "pokemon_grass" => achievement.PokemonGrass,
            "pokemon_electric" => achievement.PokemonElectric,
            "pokemon_psychic" => achievement.PokemonPsychic,
            "pokemon_ice" => achievement.PokemonIce,
            "pokemon_dragon" => achievement.PokemonDragon,
            "pokemon_dark" => achievement.PokemonDark,
            "pokemon_fairy" => achievement.PokemonFairy,
            
            _ => 0
        };
    }

    /// <summary>
    ///     Sets the value for a specific achievement in the database.
    /// </summary>
    private async Task SetAchievementValue(Database.Linq.Models.Pokemon.Achievement achievement, string achievementType, int value)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        
        var updateQuery = achievementType switch
        {
            "pokemon_caught" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonCaught, value),
            "shiny_caught" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.ShinyCaught, value),
            "shadow_caught" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.ShadowCaught, value),
            "breed_success" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.BreedSuccess, value),
            "breed_hexa" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.BreedHexa, value),
            "breed_penta" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.BreedPenta, value),
            "breed_quad" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.BreedQuad, value),
            "breed_titan" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.BreedTitan, value),
            "shiny_bred" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.ShinyBred, value),
            "shadow_bred" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.ShadowBred, value),
            "duel_party_wins" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.DuelPartyWins, value),
            "duel_single_wins" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.DuelSingleWins, value),
            "duel_inverse_wins" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.DuelInverseWins, value),
            "npc_wins" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.NpcWins, value),
            "gym_wins" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.GymWins, value),
            "npc_duels" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.NpcDuels, value),
            "duels_total" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.DuelsTotal, value),
            "duel_total_xp" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.DuelTotalXp, (float)value),
            "fishing_success" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.FishingSuccess, value),
            "missions" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.Missions, value),
            "votes" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.Votes, value),
            "market_purchased" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.MarketPurchased, value),
            "market_sold" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.MarketSold, value),
            "pokemon_released" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonReleased, value),
            "pokemon_released_ivtotal" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonReleasedIvTotal, value),
            "chests_legend" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.ChestsLegend, value),
            "chests_mythic" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.ChestsMythic, value),
            "chests_rare" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.ChestsRare, value),
            "chests_common" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.ChestsCommon, value),
            "chests_voucher" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.ChestsVoucher, value),
            "redeems_used" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.RedeemsUsed, value),
            "donation_amount" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.DonationAmount, value),
            "unown_event" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.UnownEvent, value),
            
            // Game achievements
            "game_wordsearch" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.GameWordsearch, value),
            "game_slots" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.GameSlots, value),
            "game_slots_win" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.GameSlotsWin, value),
            
            // Type-specific catching
            "pokemon_normal" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonNormal, value),
            "pokemon_fighting" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonFighting, value),
            "pokemon_flying" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonFlying, value),
            "pokemon_poison" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonPoison, value),
            "pokemon_ground" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonGround, value),
            "pokemon_rock" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonRock, value),
            "pokemon_bug" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonBug, value),
            "pokemon_ghost" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonGhost, value),
            "pokemon_steel" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonSteel, value),
            "pokemon_fire" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonFire, value),
            "pokemon_water" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonWater, value),
            "pokemon_grass" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonGrass, value),
            "pokemon_electric" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonElectric, value),
            "pokemon_psychic" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonPsychic, value),
            "pokemon_ice" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonIce, value),
            "pokemon_dragon" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonDragon, value),
            "pokemon_dark" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonDark, value),
            "pokemon_fairy" => db.Achievements.Where(a => a.UserId == achievement.UserId).Set(a => a.PokemonFairy, value),
            
            _ => throw new ArgumentException($"Unknown achievement type: {achievementType}")
        };

        await updateQuery.UpdateAsync();
    }

    #endregion

    #region Milestone Checking

    /// <summary>
    ///     Checks for milestone completions and distributes rewards.
    /// </summary>
    private async Task CheckMilestonesAsync(ulong userId, string achievement, int currentValue, Channel? context = null)
    {
        try
        {
            if (!AchievementConstants.Milestones.TryGetValue(achievement, out var milestones))
                return;

            // Get the last completed milestone for this achievement
            await using var db = await dbProvider.GetConnectionAsync();
            var lastMilestone = await db.MilestoneProgress
                .Where(mp => mp.UserId == userId && mp.AchievementType == achievement)
                .Select(mp => mp.MilestoneValue)
                .OrderByDescending(mp => mp)
                .FirstOrDefaultAsync();

            var completedMilestones = new List<MilestoneCompletion>();

            foreach (var milestone in milestones.Where(m => m > lastMilestone && currentValue >= m))
            {
                var completion = await ProcessMilestoneCompletionAsync(userId, achievement, milestone, currentValue);
                if (completion != null)
                {
                    completedMilestones.Add(completion);
                    
                    // Record the milestone completion in the database
                    await db.InsertAsync(new Database.Linq.Models.Pokemon.MilestoneProgress
                    {
                        UserId = userId,
                        AchievementType = achievement,
                        MilestoneValue = milestone,
                        CompletedAt = DateTime.UtcNow
                    });
                }
            }

            if (completedMilestones.Any())
            {
                // Send notifications
                if (context != null)
                {
                    await SendMilestoneNotificationsAsync(context, completedMilestones);
                }
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error checking milestones for achievement {Achievement} and user {UserId}", achievement, userId);
        }
    }

    /// <summary>
    ///     Processes a single milestone completion and calculates rewards.
    /// </summary>
    private async Task<MilestoneCompletion?> ProcessMilestoneCompletionAsync(ulong userId, string achievement, int milestone, int currentValue)
    {
        try
        {
            var completion = new MilestoneCompletion
            {
                Achievement = achievement,
                Milestone = milestone,
                CurrentValue = currentValue,
                DisplayName = AchievementConstants.AchievementDisplayNames.GetValueOrDefault(achievement, achievement),
                TierColor = CalculateTierColor(achievement, milestone)
            };

            // Calculate rewards
            var (mewCoins, redeems, skinTokens) = CalculateRewards(achievement, milestone);
            completion.MewCoinReward = mewCoins;
            completion.RedeemReward = redeems;
            completion.SkinTokenReward = skinTokens;
            completion.HasSpecialReward = mewCoins > 0 || redeems > 0 || skinTokens > 0;

            // Distribute rewards
            if (completion.HasSpecialReward)
            {
                await DistributeRewardsAsync(userId, mewCoins, redeems, skinTokens);
            }

            // Award loyalty points
            await AwardLoyaltyPointsAsync(userId, CalculateLoyaltyPoints(achievement, milestone));

            return completion;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error processing milestone completion for {Achievement}:{Milestone}", achievement, milestone);
            return null;
        }
    }

    /// <summary>
    ///     Calculates reward amounts for a milestone completion.
    /// </summary>
    private static (int MewCoins, int Redeems, int SkinTokens) CalculateRewards(string achievement, int milestone)
    {
        var mewCoins = 0;
        var redeems = 0;
        var skinTokens = 0;

        // Check for MewCoin rewards
        if (AchievementConstants.RewardMilestones.TryGetValue(achievement, out var rewardConfig))
        {
            var eligible = rewardConfig switch
            {
                "all" => true,
                int[] milestones => milestones.Contains(milestone),
                _ => false
            };

            if (eligible && AchievementConstants.BaseRewards.TryGetValue(achievement, out var baseReward))
            {
                mewCoins = Math.Min((int)(milestone * baseReward.MewCoins), AchievementConstants.MaxMewCoinReward);
                redeems = Math.Min((int)(milestone * baseReward.Redeems), AchievementConstants.MaxRedeemReward);
            }
        }

        // Check for additional redeem rewards
        if (AchievementConstants.RedeemMilestones.TryGetValue(achievement, out var redeemMilestones) &&
            redeemMilestones.Contains(milestone))
        {
            if (AchievementConstants.BaseRewards.TryGetValue(achievement, out var baseReward))
            {
                redeems = Math.Max(redeems, Math.Min((int)(milestone * baseReward.Redeems), AchievementConstants.MaxRedeemReward));
            }
        }

        // Check for skin token rewards
        if (AchievementConstants.SkinMilestones.TryGetValue(achievement, out var skinMilestones) &&
            skinMilestones.Contains(milestone))
        {
            skinTokens = Math.Min(1, AchievementConstants.MaxSkinTokenReward);
        }

        return (mewCoins, redeems, skinTokens);
    }

    /// <summary>
    ///     Distributes rewards to the user's account.
    /// </summary>
    private async Task DistributeRewardsAsync(ulong userId, int mewCoins, int redeems, int skinTokens)
    {
        await using var db = await dbProvider.GetConnectionAsync();

        if (mewCoins > 0)
        {
            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.MewCoins, u => (u.MewCoins ?? 0) + (ulong)mewCoins)
                .UpdateAsync();
        }

        if (redeems > 0)
        {
            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.Redeems, u => (u.Redeems ?? 0) + (ulong)redeems)
                .UpdateAsync();
        }

        if (skinTokens > 0)
        {
            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.SkinTokens, u => u.SkinTokens + skinTokens)
                .UpdateAsync();
        }
    }

    #endregion

    #region Cross-System Integration

    /// <summary>
    ///     Tracks Pokemon catching achievements including type-specific tracking.
    /// </summary>
    public async Task TrackPokemonCaughtAsync(ulong userId, List<int> typeIds, bool isShiny = false, bool isShadow = false, Channel? context = null)
    {
        // Track general catching
        await UpdateAchievementAsync(userId, "pokemon_caught", 1, context);
        
        if (isShiny)
            await UpdateAchievementAsync(userId, "shiny_caught", 1, context);
            
        if (isShadow)
            await UpdateAchievementAsync(userId, "shadow_caught", 1, context);

        // Track type-specific catching
        foreach (var typeId in typeIds)
        {
            if (AchievementConstants.TypeToAchievement.TryGetValue(typeId, out var achievementType))
            {
                await UpdateAchievementAsync(userId, achievementType, 1, context);
            }
        }
    }

    /// <summary>
    ///     Tracks breeding achievements with IV analysis.
    /// </summary>
    public async Task TrackBreedingAsync(ulong userId, int[] ivs, bool isShiny = false, bool isShadow = false, Channel? context = null)
    {
        await UpdateAchievementAsync(userId, "breed_success", 1, context);

        var perfectIvCount = ivs.Count(iv => iv == 31);
        var nearPerfectCount = ivs.Count(iv => iv == 30);

        // Track IV achievements
        switch (perfectIvCount)
        {
            case 6:
                await UpdateAchievementAsync(userId, "breed_hexa", 1, context);
                break;
            case 5 when nearPerfectCount > 0:
                await UpdateAchievementAsync(userId, "breed_titan", 1, context);
                break;
            case 5:
                await UpdateAchievementAsync(userId, "breed_penta", 1, context);
                break;
            case 4:
                await UpdateAchievementAsync(userId, "breed_quad", 1, context);
                break;
        }

        if (isShiny)
            await UpdateAchievementAsync(userId, "shiny_bred", 1, context);
            
        if (isShadow)
            await UpdateAchievementAsync(userId, "shadow_bred", 1, context);
    }

    /// <summary>
    ///     Tracks game achievements.
    /// </summary>
    public async Task TrackGameAsync(ulong userId, string gameType, bool won = false, Channel? context = null)
    {
        await UpdateAchievementAsync(userId, $"game_{gameType}", 1, context);
        
        if (won && gameType == "slots")
        {
            await UpdateAchievementAsync(userId, "game_slots_win", 1, context);
        }
    }

    #endregion

    #region Daily Streaks and Loyalty

    /// <summary>
    ///     Updates daily activity tracking and loyalty points.
    /// </summary>
    private async Task UpdateDailyActivityAsync(ulong userId)
    {
        try
        {
            await using var db = await dbProvider.GetConnectionAsync();
            var today = DateTime.UtcNow.Date;
            
            var loyalty = await db.UserLoyalty.FirstOrDefaultAsync(ul => ul.UserId == userId);
            
            if (loyalty == null)
            {
                loyalty = new Database.Linq.Models.Pokemon.UserLoyalty
                {
                    UserId = userId,
                    LoyaltyPoints = 0,
                    DailyStreak = 1,
                    LastLogin = today
                };
                await db.InsertAsync(loyalty);
                return;
            }
            
            if (loyalty.LastLogin?.Date != today)
            {
                var newStreak = loyalty.LastLogin?.Date == today.AddDays(-1) ? loyalty.DailyStreak + 1 : 1;
                var loyaltyBonus = CalculateDailyLoyaltyBonus(newStreak);
                
                await db.UserLoyalty
                    .Where(ul => ul.UserId == userId)
                    .Set(ul => ul.DailyStreak, newStreak)
                    .Set(ul => ul.LastLogin, today)
                    .Set(ul => ul.LoyaltyPoints, ul => ul.LoyaltyPoints + loyaltyBonus)
                    .Set(ul => ul.UpdatedAt, DateTime.UtcNow)
                    .UpdateAsync();
                    
                Log.Information("Updated daily activity for user {UserId}, streak: {Streak}, bonus: {Bonus}", userId, newStreak, loyaltyBonus);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error updating daily activity for user {UserId}", userId);
        }
    }

    /// <summary>
    ///     Awards loyalty points for achievements.
    /// </summary>
    private async Task AwardLoyaltyPointsAsync(ulong userId, int points)
    {
        if (points <= 0) return;

        await using var db = await dbProvider.GetConnectionAsync();
        
        // Get or create loyalty record
        var loyalty = await db.UserLoyalty.FirstOrDefaultAsync(ul => ul.UserId == userId);
        
        if (loyalty == null)
        {
            loyalty = new Database.Linq.Models.Pokemon.UserLoyalty
            {
                UserId = userId,
                LoyaltyPoints = points,
                DailyStreak = 1,
                LastLogin = DateTime.UtcNow.Date
            };
            await db.InsertAsync(loyalty);
        }
        else
        {
            await db.UserLoyalty
                .Where(ul => ul.UserId == userId)
                .Set(ul => ul.LoyaltyPoints, ul => ul.LoyaltyPoints + points)
                .Set(ul => ul.UpdatedAt, DateTime.UtcNow)
                .UpdateAsync();
        }
        
        Log.Information("Awarded {Points} loyalty points to user {UserId}", points, userId);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    ///     Gets loyalty information for a user.
    /// </summary>
    private async Task<Database.Linq.Models.Pokemon.UserLoyalty> GetOrCreateLoyaltyAsync(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        
        var loyalty = await db.UserLoyalty.FirstOrDefaultAsync(ul => ul.UserId == userId);
        
        if (loyalty == null)
        {
            loyalty = new Database.Linq.Models.Pokemon.UserLoyalty
            {
                UserId = userId,
                LoyaltyPoints = 0,
                DailyStreak = 1,
                LastLogin = DateTime.UtcNow.Date
            };
            await db.InsertAsync(loyalty);
        }
        
        return loyalty;
    }

    /// <summary>
    ///     Calculates the tier color for a milestone.
    /// </summary>
    private static uint CalculateTierColor(string achievement, int milestone)
    {
        if (!AchievementConstants.Milestones.TryGetValue(achievement, out var milestones))
            return AchievementConstants.BronzeTierColor;

        var index = Array.IndexOf(milestones, milestone);
        return AchievementConstants.GetMilestoneColor(index, milestones.Length);
    }

    /// <summary>
    ///     Calculates loyalty points for a milestone completion.
    /// </summary>
    private static int CalculateLoyaltyPoints(string achievement, int milestone)
    {
        // Base loyalty points calculation
        return achievement switch
        {
            var a when a.StartsWith("pokemon_") => milestone / 10,
            var a when a.StartsWith("breed_") => milestone * 2,
            var a when a.StartsWith("game_") => milestone,
            _ => milestone / 5
        };
    }

    /// <summary>
    ///     Calculates daily loyalty bonus based on streak.
    /// </summary>
    private static int CalculateDailyLoyaltyBonus(int streak)
    {
        return streak switch
        {
            >= 30 => 50,
            >= 14 => 25,
            >= 7 => 10,
            >= 3 => 5,
            _ => 1
        };
    }

    /// <summary>
    ///     Sends milestone completion notifications.
    /// </summary>
    private static async Task SendMilestoneNotificationsAsync(Channel context, List<MilestoneCompletion> completions)
    {
        if (!completions.Any()) return;

        var embed = new EmbedBuilder()
            .WithTitle("🏆 Achievement Milestones Reached!")
            .WithColor(completions.First().TierColor);

        var description = string.Join("\n", completions.Select(c =>
        {
            var rewardText = "";
            if (c.MewCoinReward > 0) rewardText += $"{c.MewCoinReward:N0} MewCoins ";
            if (c.RedeemReward > 0) rewardText += $"{c.RedeemReward} Redeems ";
            if (c.SkinTokenReward > 0) rewardText += $"{c.SkinTokenReward} Skin Tokens ";

            return $"**{c.DisplayName}**: {c.Milestone:N0}" + (rewardText.Length > 0 ? $"\n*Rewards: {rewardText.Trim()}*" : "");
        }));

        embed.WithDescription(description);

        await context.SendMessageAsync(embeds: [embed.Build()]);
    }

    #endregion

    #region Public API Methods

    /// <summary>
    ///     Gets loyalty information for a user.
    /// </summary>
    public async Task<Database.Linq.Models.Pokemon.UserLoyalty> GetLoyaltyAsync(ulong userId)
    {
        return await GetOrCreateLoyaltyAsync(userId);
    }

    /// <summary>
    ///     Gets current achievement values for a user.
    /// </summary>
    public async Task<Dictionary<string, int>> GetAchievementValuesAsync(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var achievement = await db.Achievements.FirstOrDefaultAsync(a => a.UserId == userId);
        
        if (achievement == null)
            return new Dictionary<string, int>();

        var values = new Dictionary<string, int>();
        foreach (var achievementType in AchievementConstants.Milestones.Keys)
        {
            values[achievementType] = GetAchievementValue(achievement, achievementType);
        }

        return values;
    }

    /// <summary>
    ///     Gets a database connection for external use.
    /// </summary>
    public async Task<Database.DittoDataConnection> GetDbConnectionAsync()
    {
        return await dbProvider.GetConnectionAsync();
    }

    /// <summary>
    ///     Gets the last completed milestone for a specific achievement.
    /// </summary>
    public async Task<int> GetLastMilestoneAsync(ulong userId, string achievementType)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        return await db.MilestoneProgress
            .Where(mp => mp.UserId == userId && mp.AchievementType == achievementType)
            .Select(mp => mp.MilestoneValue)
            .OrderByDescending(mp => mp)
            .FirstOrDefaultAsync();
    }

    #endregion
}