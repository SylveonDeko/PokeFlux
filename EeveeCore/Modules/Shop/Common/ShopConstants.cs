namespace EeveeCore.Modules.Shop.Common;

/// <summary>
///     Contains constants and configuration values for the radiant shop system.
/// </summary>
public static class ShopConstants
{
    /// <summary>
    ///     The number of items shown per page in the shop.
    /// </summary>
    public const int ItemsPerPage = 10;

    /// <summary>
    ///     The minimum number of Pokemon required to unlock the radiant shop.
    /// </summary>
    public const int MinimumPokemonRequired = 50;

    /// <summary>
    ///     The base cost multiplier for radiant Pokemon.
    /// </summary>
    public const decimal RadiantCostMultiplier = 2.5m;

    /// <summary>
    ///     Available Pokemon types for filtering in the shop.
    /// </summary>
    public static readonly string[] PokemonTypes = 
    {
        "Normal", "Fire", "Water", "Electric", "Grass", "Ice",
        "Fighting", "Poison", "Ground", "Flying", "Psychic", "Bug",
        "Rock", "Ghost", "Dragon", "Dark", "Steel", "Fairy"
    };

    /// <summary>
    ///     Available shop categories for the unified interface.
    /// </summary>
    public static readonly string[] ShopCategories = 
    {
        "General", "Radiant", "CrystalSlime"
    };

    /// <summary>
    ///     Category display names and descriptions.
    /// </summary>
    public static readonly Dictionary<string, (string DisplayName, string Description, string Emoji)> CategoryInfo = new()
    {
        { "General", ("General", "Basic items, vitamins, daycare, and everyday supplies", "🛒") },
        { "Radiant", ("Radiant", "Exclusive radiant Pokemon and rare collectibles", "✨") },
        { "CrystalSlime", ("Crystal", "Exchange crystallized slime for special rewards", "🔮") }
    };

    /// <summary>
    ///     Emojis for each Pokemon type.
    /// </summary>
    public static readonly Dictionary<string, string> TypeEmojis = new()
    {
        { "Normal", "⚪" },
        { "Fire", "🔥" },
        { "Water", "💧" },
        { "Electric", "⚡" },
        { "Grass", "🌿" },
        { "Ice", "❄️" },
        { "Fighting", "👊" },
        { "Poison", "☠️" },
        { "Ground", "🌍" },
        { "Flying", "🌪️" },
        { "Psychic", "🔮" },
        { "Bug", "🐛" },
        { "Rock", "🪨" },
        { "Ghost", "👻" },
        { "Dragon", "🐉" },
        { "Dark", "🌙" },
        { "Steel", "⚙️" },
        { "Fairy", "🧚" }
    };

    /// <summary>
    ///     Color scheme for different shop categories.
    /// </summary>
    public static readonly Dictionary<string, Color> CategoryColors = new()
    {
        { "Radiant", Color.Gold },
        { "Shiny", Color.Purple },
        { "Legendary", Color.Red },
        { "Mythical", Color.DarkPurple },
        { "Regular", Color.Blue },
        { "General", Color.Green },
        { "CrystalSlime", Color.Teal }
    };

    /// <summary>
    ///     Rarity multipliers for pricing calculations.
    /// </summary>
    public static readonly Dictionary<string, decimal> RarityMultipliers = new()
    {
        { "Common", 1.0m },
        { "Uncommon", 1.5m },
        { "Rare", 2.0m },
        { "Epic", 3.0m },
        { "Legendary", 5.0m },
        { "Mythical", 10.0m }
    };

    /// <summary>
    ///     Default shop items that are always available.
    /// </summary>
    public static readonly List<ShopItem> DefaultItems = new()
    {
        new ShopItem 
        { 
            Id = "radiant_boost", 
            Name = "Radiant Boost", 
            Description = "Increases your radiant Pokemon encounter rate", 
            Price = 50000,
            Category = "Boost",
            IsConsumable = true,
            Stock = -1 // Unlimited
        },
        new ShopItem 
        { 
            Id = "shiny_charm", 
            Name = "Shiny Charm", 
            Description = "Increases your shiny Pokemon encounter rate", 
            Price = 25000,
            Category = "Charm",
            IsConsumable = false,
            Stock = 1
        },
        new ShopItem 
        { 
            Id = "master_ball", 
            Name = "Master Ball", 
            Description = "Catches any Pokemon with 100% success rate", 
            Price = 10000,
            Category = "Pokeball",
            IsConsumable = true,
            Stock = 5
        }
    };

    /// <summary>
    ///     Navigation emojis for shop interface.
    /// </summary>
    public static class Emojis
    {
        /// <summary>
        ///     Previous page emoji.
        /// </summary>
        public const string PreviousPage = "◀️";

        /// <summary>
        ///     Next page emoji.
        /// </summary>
        public const string NextPage = "▶️";

        /// <summary>
        ///     Purchase emoji.
        /// </summary>
        public const string Purchase = "💰";

        /// <summary>
        ///     Filter emoji.
        /// </summary>
        public const string Filter = "🔍";

        /// <summary>
        ///     Refresh emoji.
        /// </summary>
        public const string Refresh = "🔄";

        /// <summary>
        ///     Close emoji.
        /// </summary>
        public const string Close = "❌";

        /// <summary>
        ///     Radiant star emoji.
        /// </summary>
        public const string RadiantStar = "✨";

        /// <summary>
        ///     Shiny sparkle emoji.
        /// </summary>
        public const string ShinySparkle = "⭐";

        /// <summary>
        ///     Credits emoji.
        /// </summary>
        public const string Credits = "🪙";
    }
}

/// <summary>
///     Represents an item available for purchase in the shop.
/// </summary>
public class ShopItem
{
    /// <summary>
    ///     Gets or sets the unique identifier of the item.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the display name of the item.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the description of the item.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the price of the item in credits.
    /// </summary>
    public int Price { get; set; }

    /// <summary>
    ///     Gets or sets the category of the item.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a value indicating whether the item is consumable.
    /// </summary>
    public bool IsConsumable { get; set; }

    /// <summary>
    ///     Gets or sets the available stock count. -1 means unlimited.
    /// </summary>
    public int Stock { get; set; }

    /// <summary>
    ///     Gets or sets the Pokemon type this item is associated with, if any.
    /// </summary>
    public string? PokemonType { get; set; }

    /// <summary>
    ///     Gets or sets the rarity tier of the item.
    /// </summary>
    public string Rarity { get; set; } = "Common";

    /// <summary>
    ///     Gets or sets a value indicating whether this item is a radiant variant.
    /// </summary>
    public bool IsRadiant { get; set; }

    /// <summary>
    ///     Gets or sets the image URL for the item.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    ///     Gets or sets additional metadata for the item.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}