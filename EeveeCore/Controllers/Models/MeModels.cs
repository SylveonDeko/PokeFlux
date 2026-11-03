namespace EeveeCore.Controllers.Models;

/// <summary>
///     How many Pokémon of each kind a user owns.
/// </summary>
/// <param name="Total">Every Pokémon.</param>
/// <param name="Shiny">Shiny Pokémon.</param>
/// <param name="Radiant">Radiant Pokémon.</param>
/// <param name="Shadow">Shadow Pokémon.</param>
/// <param name="Legendary">Legendary Pokémon.</param>
public record PokemonCounts(int Total, int Shiny, int Radiant, int Shadow, int Legendary);

/// <summary>
///     The user's trainer profile.
/// </summary>
/// <param name="Id">The user's Fluxer ID.</param>
/// <param name="TrainerNickname">The trainer nickname, or null.</param>
/// <param name="Credits">The credit balance.</param>
/// <param name="Redeems">The redeem balance.</param>
/// <param name="Energy">The energy, from 0 to 10.</param>
/// <param name="FishingLevel">The fishing level.</param>
/// <param name="FishingExp">The fishing experience.</param>
/// <param name="FishingLevelCap">The fishing experience needed for the next level.</param>
/// <param name="SelectedPokemonId">The ID of the selected Pokémon, or null.</param>
/// <param name="Region">The region.</param>
/// <param name="Hunt">The shadow hunt target, or null.</param>
/// <param name="Chain">The shadow hunt chain.</param>
/// <param name="DaycareLimit">How many eggs the daycare holds.</param>
/// <param name="Counts">How many Pokémon of each kind the user owns.</param>
/// <param name="Started">Whether the user has started.</param>
public record MeProfile(
    ulong Id,
    string? TrainerNickname,
    long Credits,
    long Redeems,
    int Energy,
    long FishingLevel,
    long FishingExp,
    long FishingLevelCap,
    ulong? SelectedPokemonId,
    string Region,
    string? Hunt,
    int Chain,
    int DaycareLimit,
    PokemonCounts Counts,
    bool Started);

/// <summary>
///     An item and how many the user has.
/// </summary>
/// <param name="Name">The item's name.</param>
/// <param name="Count">How many the user has.</param>
/// <param name="Category">The item's category from the game's item data, such as <c>held-items</c>, or null.</param>
public record ItemCount(string Name, int Count, string? Category = null);

/// <summary>
///     The user's items.
/// </summary>
/// <param name="Inventory">Chests, boosters and other inventory items.</param>
/// <param name="Bag">The items in the bag.</param>
/// <param name="HeldItems">How many of the user's Pokémon hold each item.</param>
public record InventoryResponse(List<ItemCount> Inventory, List<ItemCount> Bag, List<ItemCount> HeldItems);

/// <summary>
///     The user's progress on one achievement.
/// </summary>
/// <param name="Key">The achievement's key.</param>
/// <param name="Name">The achievement's name.</param>
/// <param name="Value">The current value.</param>
/// <param name="LastMilestone">The last milestone reached, or 0.</param>
/// <param name="NextMilestone">The next milestone, or null when all are reached.</param>
/// <param name="Milestones">Every milestone of the achievement.</param>
public record AchievementProgressInfo(
    string Key,
    string Name,
    int Value,
    int LastMilestone,
    int? NextMilestone,
    int[] Milestones);

/// <summary>
///     The achievements of one category.
/// </summary>
/// <param name="Name">The category.</param>
/// <param name="Achievements">The user's progress on each achievement in it.</param>
public record AchievementCategory(string Name, List<AchievementProgressInfo> Achievements);

/// <summary>
///     A loyalty tier.
/// </summary>
/// <param name="Name">The tier's name.</param>
/// <param name="RequiredPoints">The points the tier starts at.</param>
/// <param name="XpMultiplier">The XP multiplier.</param>
/// <param name="ShopDiscount">The shop discount, in percent.</param>
/// <param name="MonthlyBonus">The monthly credit bonus.</param>
public record LoyaltyTierInfo(string Name, int RequiredPoints, double XpMultiplier, int ShopDiscount, int MonthlyBonus);

/// <summary>
///     The user's loyalty status.
/// </summary>
/// <param name="Points">The loyalty points.</param>
/// <param name="DailyStreak">The daily streak, in days.</param>
/// <param name="NextStreakBonus">The streak of the next bonus, or 0 when none is left.</param>
/// <param name="Tier">The current tier.</param>
/// <param name="NextTier">The next tier, or null at the top.</param>
public record LoyaltyInfo(int Points, int DailyStreak, int NextStreakBonus, LoyaltyTierInfo Tier,
    LoyaltyTierInfo? NextTier);

/// <summary>
///     The user's achievements, milestones and loyalty.
/// </summary>
/// <param name="Categories">The achievements by category.</param>
/// <param name="TotalMilestones">How many milestones the user has reached.</param>
/// <param name="Loyalty">The loyalty status.</param>
public record AchievementsResponse(List<AchievementCategory> Categories, int TotalMilestones, LoyaltyInfo Loyalty);

/// <summary>
///     An active mission and the user's progress on it.
/// </summary>
/// <param name="Key">The mission's key.</param>
/// <param name="Name">The mission's name.</param>
/// <param name="Description">The mission's description.</param>
/// <param name="Target">The amount needed to complete it.</param>
/// <param name="Reward">The reward.</param>
/// <param name="Progress">The user's progress.</param>
public record MissionProgressInfo(string Key, string Name, string Description, int Target, int Reward, int Progress);

/// <summary>
///     An item of the crystal slime exchange.
/// </summary>
/// <param name="Key">The item's key.</param>
/// <param name="Name">The item's name.</param>
/// <param name="Description">What it does.</param>
/// <param name="Cost">Its price in crystallized slime.</param>
/// <param name="CommandOnly">Whether it can only be bought with the missions buy command.</param>
public record MissionStoreItem(string Key, string Name, string Description, int Cost, bool CommandOnly);

/// <summary>
///     The user's missions, mission level and slime.
/// </summary>
/// <param name="Missions">The active missions with the user's progress.</param>
/// <param name="Xp">The mission XP earned in the current level.</param>
/// <param name="Level">The mission level.</param>
/// <param name="LevelStartXp">The XP the current level starts at.</param>
/// <param name="NextLevelXp">The XP that reaches the next level, or null at the top level.</param>
/// <param name="Title">The selected title.</param>
/// <param name="Slime">The slime balance.</param>
/// <param name="CrystalSlime">The crystallized slime balance.</param>
/// <param name="Store">The crystal slime exchange.</param>
public record MissionsResponse(
    List<MissionProgressInfo> Missions,
    int Xp,
    int Level,
    int LevelStartXp,
    int? NextLevelXp,
    string Title,
    int Slime,
    int CrystalSlime,
    List<MissionStoreItem> Store);

/// <summary>
///     Buys an item from the crystal slime exchange.
/// </summary>
/// <param name="Item">The item's key or name.</param>
public record MissionBuyRequest(string Item);
