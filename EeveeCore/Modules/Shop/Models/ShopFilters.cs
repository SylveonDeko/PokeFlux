namespace EeveeCore.Modules.Shop.Models;

/// <summary>
///     Represents the filters applied to shop browsing.
/// </summary>
public class ShopFilters
{
    /// <summary>
    ///     Gets or sets the Pokemon type filter.
    /// </summary>
    public string? PokemonType { get; set; }

    /// <summary>
    ///     Gets or sets the item category filter.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    ///     Gets or sets the minimum price filter.
    /// </summary>
    public int? MinPrice { get; set; }

    /// <summary>
    ///     Gets or sets the maximum price filter.
    /// </summary>
    public int? MaxPrice { get; set; }

    /// <summary>
    ///     Gets or sets the rarity filter.
    /// </summary>
    public string? Rarity { get; set; }

    /// <summary>
    ///     Gets or sets the item name search filter.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether to show only radiant items.
    /// </summary>
    public bool ShowRadiantOnly { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether to show only available items.
    /// </summary>
    public bool ShowAvailableOnly { get; set; } = true;

    /// <summary>
    ///     Gets or sets a value indicating whether to show only affordable items.
    /// </summary>
    public bool ShowAffordableOnly { get; set; }

    /// <summary>
    ///     Checks if any filters are currently active.
    /// </summary>
    public bool HasActiveFilters => 
        !string.IsNullOrEmpty(PokemonType) ||
        !string.IsNullOrEmpty(Category) ||
        MinPrice.HasValue ||
        MaxPrice.HasValue ||
        !string.IsNullOrEmpty(Rarity) ||
        !string.IsNullOrEmpty(ItemName) ||
        ShowRadiantOnly ||
        ShowAffordableOnly;

    /// <summary>
    ///     Clears all active filters.
    /// </summary>
    public void ClearFilters()
    {
        PokemonType = null;
        Category = null;
        MinPrice = null;
        MaxPrice = null;
        Rarity = null;
        ItemName = null;
        ShowRadiantOnly = false;
        ShowAffordableOnly = false;
        ShowAvailableOnly = true;
    }
}

/// <summary>
///     Represents the different sort orders for shop items.
/// </summary>
public enum ShopSortOrder
{
    /// <summary>
    ///     Sort by price in ascending order.
    /// </summary>
    PriceAscending,

    /// <summary>
    ///     Sort by price in descending order.
    /// </summary>
    PriceDescending,

    /// <summary>
    ///     Sort by name in alphabetical order.
    /// </summary>
    NameAscending,

    /// <summary>
    ///     Sort by name in reverse alphabetical order.
    /// </summary>
    NameDescending,

    /// <summary>
    ///     Sort by rarity (common to legendary).
    /// </summary>
    RarityAscending,

    /// <summary>
    ///     Sort by rarity (legendary to common).
    /// </summary>
    RarityDescending,

    /// <summary>
    ///     Sort by category.
    /// </summary>
    Category,

    /// <summary>
    ///     Sort by availability (in stock first).
    /// </summary>
    Availability
}

/// <summary>
///     Represents the result of a shop purchase operation.
/// </summary>
public class PurchaseResult
{
    /// <summary>
    ///     Gets or sets a value indicating whether the purchase was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    ///     Gets or sets the message describing the purchase result.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the purchased item information.
    /// </summary>
    public Common.ShopItem? Item { get; set; }

    /// <summary>
    ///     Gets or sets the quantity purchased.
    /// </summary>
    public int Quantity { get; set; }

    /// <summary>
    ///     Gets or sets the total cost of the purchase.
    /// </summary>
    public int TotalCost { get; set; }

    /// <summary>
    ///     Gets or sets the remaining credits after the purchase.
    /// </summary>
    public int RemainingCredits { get; set; }

    /// <summary>
    ///     Gets or sets the error code if the purchase failed.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    ///     Gets or sets additional details about the purchase.
    /// </summary>
    public Dictionary<string, object> Details { get; set; } = new();
}