namespace EeveeCore.Modules.Missions.Common;

/// <summary>
///     Contains constants and configuration values for the missions system.
/// </summary>
public static class MissionConstants
{
    #region XP System Constants

    /// <summary>
    ///     Base XP value used in level calculations.
    /// </summary>
    public const int BaseXp = 100;

    /// <summary>
    ///     Exponent used in XP calculation formula.
    /// </summary>
    public const double LevelExponent = 1.5;

    /// <summary>
    ///     Bonus XP modifier (currently not implemented).
    /// </summary>
    public const int BonusXp = 0;

    #endregion

    #region Mission XP Rewards

    /// <summary>
    ///     XP gained from breeding Pokemon.
    /// </summary>
    public const int BreedXp = 3;

    /// <summary>
    ///     XP gained from catching Pokemon.
    /// </summary>
    public const int CatchXp = 1;

    /// <summary>
    ///     XP gained from hatching Pokemon.
    /// </summary>
    public const int HatchXp = 5;

    /// <summary>
    ///     XP gained from fishing Pokemon.
    /// </summary>
    public const int FishXp = 3;

    /// <summary>
    ///     XP gained from completing duels.
    /// </summary>
    public const int DuelXp = 1;

    /// <summary>
    ///     XP gained from NPC battles.
    /// </summary>
    public const int NpcXp = 1;

    /// <summary>
    ///     XP gained from EV training.
    /// </summary>
    public const int EvTrainingXp = 1;

    /// <summary>
    ///     XP gained from Pokemon setup.
    /// </summary>
    public const int PokemonSetupXp = 1;

    /// <summary>
    ///     XP gained from party registration.
    /// </summary>
    public const int PartyRegistrationXp = 1;

    /// <summary>
    ///     XP gained from voting.
    /// </summary>
    public const int VoteXp = 1;

    /// <summary>
    ///     XP gained from completing word search games.
    /// </summary>
    public const int GameWordSearchXp = 2;

    /// <summary>
    ///     XP gained from playing slot machine games.
    /// </summary>
    public const int GameSlotsXp = 1;

    /// <summary>
    ///     XP gained from winning slot machine games.
    /// </summary>
    public const int GameSlotsWinXp = 3;

    #endregion

    #region Mission System

    /// <summary>
    ///     Number of hours between mission rotations.
    /// </summary>
    public const int MissionRotationHours = 24;

    /// <summary>
    ///     IV threshold for high-IV breeding bonus.
    /// </summary>
    public const int HighIvThreshold = 160;

    /// <summary>
    ///     Total possible IV points (6 stats * 31 max IV).
    /// </summary>
    public const int MaxIvTotal = 186;

    #endregion

    #region Store System

    /// <summary>
    ///     Store items available for purchase with crystal slime.
    /// </summary>
    public static readonly Dictionary<string, StoreItemConfig> StoreItems = new()
    {
        {
            "friendship-stone", new StoreItemConfig
            {
                Name = "Friendship Stone",
                Cost = 10,
                Description = "Increases friendship/happiness instantly!",
                Type = StoreItemType.Item
            }
        },
        {
            "credits_small", new StoreItemConfig
            {
                Name = "100000 Credits",
                Cost = 150,
                Reward = 100000,
                Description = "Instant Coins boost!",
                Type = StoreItemType.Credits
            }
        },
        {
            "shadow-essence", new StoreItemConfig
            {
                Name = "Shadow Essence",
                Cost = 100,
                Description = "Increase your shadow-chain instantly! (+15-75 chain)",
                Type = StoreItemType.ShadowEssence,
                MinChainIncrease = 15,
                MaxChainIncrease = 75
            }
        },
        {
            "small_chance_ticket", new StoreItemConfig
            {
                Name = "Meowth Ticket",
                Cost = 75,
                Description = "Try your luck! Hidden credits ranging from 100 to 150,000",
                Type = StoreItemType.Lottery
            }
        },
        {
            "vip_token_single", new StoreItemConfig
            {
                Name = "VIP Token",
                Cost = 1000,
                VipTokensAmount = 1,
                Description = "Get exclusive benefits!",
                Type = StoreItemType.VipTokens
            }
        },
        {
            "vip_token_pack", new StoreItemConfig
            {
                Name = "VIP Token x3",
                Cost = 2500,
                VipTokensAmount = 3,
                Description = "3 VIP Tokens at a discount!",
                Type = StoreItemType.VipTokens
            }
        }
    };

    #endregion

    #region Lottery System

    /// <summary>
    ///     Number of choices in Meowth ticket lottery.
    /// </summary>
    public const int LotteryChoiceCount = 9;

    /// <summary>
    ///     Timeout for lottery game interactions in seconds.
    /// </summary>
    public const int LotteryTimeoutSeconds = 180;

    /// <summary>
    ///     Possible credit amounts for Meowth ticket lottery.
    /// </summary>
    public static readonly int[] LotteryAmounts = [100, 250, 350, 1000, 5000, 25000, 50000, 100000, 150000];

    /// <summary>
    ///     Weights for lottery amounts (higher = more likely).
    /// </summary>
    public static readonly int[] LotteryWeights = [20, 25, 20, 20, 20, 20, 20, 20, 20];

    #endregion

    #region Discord Configuration

    /// <summary>
    ///     Channel ID for level up notifications.
    /// </summary>
    public const ulong LevelUpChannelId = 1175759511739453470;

    /// <summary>
    ///     Channel ID for TopGG vote events.
    /// </summary>
    public const ulong VoteChannelId = 1004571515204927558;

    /// <summary>
    ///     Crystal Slime emoji ID.
    /// </summary>
    public const string CrystalSlimeEmojiId = Emojis.CrystallizedSlime;

    /// <summary>
    ///     Mission completion emoji ID.
    /// </summary>
    public const string MissionEmojiId = Emojis.Mission;

    #endregion

    #region UI Colors

    /// <summary>
    ///     Color for XP progress displays.
    /// </summary>
    public const uint XpProgressColor = 0x3BB374; // Green

    /// <summary>
    ///     Color for mission completion embeds.
    /// </summary>
    public const uint MissionCompleteColor = 0xFFB6C1; // Light pink

    /// <summary>
    ///     Color for store embeds.
    /// </summary>
    public const uint StoreColor = 0x10; // Gold

    /// <summary>
    ///     Color for lottery success (high amount).
    /// </summary>
    public const uint LotteryHighColor = 0xFFD700; // Gold

    /// <summary>
    ///     Color for lottery success (medium amount).
    /// </summary>
    public const uint LotteryMediumColor = 0x00FF00; // Green

    /// <summary>
    ///     Color for lottery success (low amount).
    /// </summary>
    public const uint LotteryLowColor = 0xFF4500; // Orange red

    #endregion

    #region Default Values

    /// <summary>
    ///     Default user title for new users.
    /// </summary>
    public const string DefaultUserTitle = "Minion of Ditto";

    /// <summary>
    ///     Default user level.
    /// </summary>
    public const int DefaultUserLevel = 1;

    #endregion
}

/// <summary>
///     Configuration for store items.
/// </summary>
public class StoreItemConfig
{
    /// <summary>
    ///     Display name of the item.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Cost in crystal slime.
    /// </summary>
    public int Cost { get; set; }

    /// <summary>
    ///     Credit reward amount (for credit items).
    /// </summary>
    public ulong Reward { get; set; }

    /// <summary>
    ///     Number of VIP tokens (for VIP token items).
    /// </summary>
    public int VipTokensAmount { get; set; }

    /// <summary>
    ///     Description of the item.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     Type of store item.
    /// </summary>
    public StoreItemType Type { get; set; }

    /// <summary>
    ///     Minimum chain increase (for shadow essence).
    /// </summary>
    public int MinChainIncrease { get; set; }

    /// <summary>
    ///     Maximum chain increase (for shadow essence).
    /// </summary>
    public int MaxChainIncrease { get; set; }
}

/// <summary>
///     Types of store items.
/// </summary>
public enum StoreItemType
{
    /// <summary>
    ///     Regular inventory item.
    /// </summary>
    Item,
    
    /// <summary>
    ///     Credits/MewCoins reward.
    /// </summary>
    Credits,
    
    /// <summary>
    ///     Shadow essence for chain increases.
    /// </summary>
    ShadowEssence,
    
    /// <summary>
    ///     Lottery ticket item.
    /// </summary>
    Lottery,
    
    /// <summary>
    ///     VIP tokens.
    /// </summary>
    VipTokens
}