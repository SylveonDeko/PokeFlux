using System.Text.Json;
using EeveeCore.Modules.Pokemon.Services;
using EeveeCore.Modules.Shop.Common;
using EeveeCore.Modules.Shop.Models;
using EeveeCore.Services.Impl;
using LinqToDB;
using LinqToDB.Async;
using MongoDB.Driver;
using Serilog;
using ChestStore = EeveeCore.Database.Linq.Models.Game.ChestStore;
using RedeemStore = EeveeCore.Database.Linq.Models.Game.RedeemStore;

namespace EeveeCore.Modules.Shop.Services;

/// <summary>
///     Service for handling shop operations including item browsing, purchasing, and inventory management.
///     Provides functionality for the radiant shop system with Pokemon-themed items and filtering.
///     Consolidates all buying functionality from the Items module.
/// </summary>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="mongoService">The MongoDB service for Pokemon data.</param>
/// <param name="pokemonService">The Pokemon service for evolution checks.</param>
public class ShopService(LinqToDbConnectionProvider dbProvider, IMongoService mongoService, PokemonService pokemonService) : INService
{
    /// <summary>
    ///     The maximum number of market slots a user can have.
    /// </summary>
    private const int MaxMarketSlots = 10;

    /// <summary>
    ///     Set of items that can be actively applied to Pokémon for evolution or other effects,
    ///     rather than being equipped as held items.
    /// </summary>
    private readonly HashSet<string> _activeItemList =
    [
        "fire-stone", "water-stone", "thunder-stone", "leaf-stone", "moon-stone",
        "sun-stone", "shiny-stone", "dusk-stone", "dawn-stone", "ice-stone", "black-augurite",
        "kings-rock", "metal-coat", "dragon-scale", "upgrade", "protector", "electirizer",
        "magmarizer", "dubious-disc", "reaper-cloth", "oval-stone", "razor-claw",
        "razor-fang", "prism-scale", "whipped-dream", "sachet", "strawberry-sweet",
        "love-sweet", "berry-sweet", "clover-sweet", "flower-sweet", "star-sweet",
        "ribbon-sweet", "sweet-apple", "tart-apple", "cracked-pot", "chipped-pot",
        "galarica-cuff", "galarica-wreath", "auspicious-armor", "malicious-armor",
        "gimmighoul-coin", "deep-sea-scale", "deep-sea-tooth", "friendship-stone"
    ];

    /// <summary>
    ///     Set of all berry items that can be used in the game.
    /// </summary>
    private readonly HashSet<string> _berryList =
    [
        "cheri-berry", "chesto-berry", "pecha-berry", "rawst-berry", "aspear-berry",
        "leppa-berry", "oran-berry", "persim-berry", "lum-berry", "sitrus-berry",
        "figy-berry", "wiki-berry", "mago-berry", "aguav-berry", "iapapa-berry",
        "razz-berry", "bluk-berry", "nanab-berry", "wepear-berry", "pinap-berry",
        "pomeg-berry", "kelpsy-berry", "qualot-berry", "hondew-berry", "grepa-berry",
        "tamato-berry", "cornn-berry", "magost-berry", "rabuta-berry", "nomel-berry",
        "spelon-berry", "pamtre-berry", "watmel-berry", "durin-berry", "belue-berry",
        "occa-berry", "passho-berry", "wacan-berry", "rindo-berry", "yache-berry",
        "chople-berry", "kebia-berry", "shuca-berry", "coba-berry", "payapa-berry",
        "tanga-berry", "charti-berry", "kasib-berry", "haban-berry", "colbur-berry",
        "babiri-berry", "chilan-berry", "liechi-berry", "ganlon-berry", "salac-berry",
        "petaya-berry", "apicot-berry", "lansat-berry", "starf-berry", "enigma-berry",
        "micle-berry", "custap-berry", "jaboca-berry", "rowap-berry", "roseli-berry",
        "kee-berry", "maranga-berry"
    ];

    /// <summary>
    ///     Random number generator for various operations.
    /// </summary>
    private readonly Random _random = new();

    /// <summary>
    ///     Determines if a Pokémon is in a formed state (mega, primal, etc.).
    ///     Used to prevent certain item operations on formed Pokémon.
    /// </summary>
    /// <param name="pokemonName">The name of the Pokémon to check.</param>
    /// <returns>True if the Pokémon is in a formed state, false otherwise.</returns>
    private static bool IsFormed(string? pokemonName)
    {
        return pokemonName.EndsWith("-mega") || pokemonName.EndsWith("-x") || pokemonName.EndsWith("-y") ||
               pokemonName.EndsWith("-origin") || pokemonName.EndsWith("-10") || pokemonName.EndsWith("-complete") ||
               pokemonName.EndsWith("-ultra") || pokemonName.EndsWith("-crowned") ||
               pokemonName.EndsWith("-eternamax") ||
               pokemonName.EndsWith("-blade");
    }
    /// <summary>
    ///     Gets shop items for a specific category with optional filtering.
    /// </summary>
    /// <param name="category">The category to get items for (General, Radiant, CrystalSlime).</param>
    /// <param name="filters">The filters to apply to the item list.</param>
    /// <param name="sortOrder">The sort order for the items.</param>
    /// <param name="userCredits">The user's available credits for affordability filtering.</param>
    /// <returns>A list of shop items matching the specified criteria.</returns>
    public async Task<List<ShopItem>> GetShopItemsByCategoryAsync(string category, ShopFilters? filters = null, ShopSortOrder sortOrder = ShopSortOrder.PriceAscending, int userCredits = 0)
    {
        try
        {
            var items = category switch
            {
                "General" => await GetGeneralItemsAsync(),
                "Radiant" => await GetRadiantItemsAsync(),
                "CrystalSlime" => await GetCrystalSlimeItemsAsync(),
                _ => new List<ShopItem>()
            };

            // Apply filters
            if (filters != null)
            {
                items = ApplyFilters(items, filters, userCredits);
            }

            // Apply sorting
            items = ApplySorting(items, sortOrder);

            return items;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error retrieving shop items for category {Category}", category);
            throw;
        }
    }

    /// <summary>
    ///     Gets all available shop items with optional filtering.
    /// </summary>
    /// <param name="filters">The filters to apply to the item list.</param>
    /// <param name="sortOrder">The sort order for the items.</param>
    /// <param name="userCredits">The user's available credits for affordability filtering.</param>
    /// <returns>A list of shop items matching the specified criteria.</returns>
    public async Task<List<ShopItem>> GetShopItemsAsync(ShopFilters? filters = null, ShopSortOrder sortOrder = ShopSortOrder.PriceAscending, int userCredits = 0)
    {
        try
        {
            var items = new List<ShopItem>(ShopConstants.DefaultItems);

            // Add seasonal/event items
            var eventItems = await GetEventItemsAsync();
            items.AddRange(eventItems);

            // Add shop items from MongoDB if available
            var mongoItems = await GetMongoShopItemsAsync();
            items.AddRange(mongoItems);

            // Apply filters
            if (filters != null)
            {
                items = ApplyFilters(items, filters, userCredits);
            }

            // Apply sorting
            items = ApplySorting(items, sortOrder);

            return items;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error retrieving shop items");
            throw;
        }
    }

    /// <summary>
    ///     Gets a specific shop item by its ID.
    /// </summary>
    /// <param name="itemId">The unique identifier of the item.</param>
    /// <returns>The shop item if found, null otherwise.</returns>
    public async Task<ShopItem?> GetShopItemAsync(string itemId)
    {
        try
        {
            var allItems = await GetShopItemsAsync();
            return allItems.FirstOrDefault(i => i.Id == itemId);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error retrieving shop item {ItemId}", itemId);
            return null;
        }
    }

    /// <summary>
    ///     Purchases an item from the shop for a user.
    /// </summary>
    /// <param name="userId">The user ID making the purchase.</param>
    /// <param name="itemId">The ID of the item to purchase.</param>
    /// <param name="quantity">The quantity to purchase.</param>
    /// <returns>The result of the purchase operation.</returns>
    public async Task<PurchaseResult> PurchaseItemAsync(ulong userId, string itemId, int quantity = 1)
    {
        try
        {
            var item = await GetShopItemAsync(itemId);
            if (item == null)
            {
                return new PurchaseResult 
                { 
                    Success = false, 
                    Message = "Item not found in shop.",
                    ErrorCode = "ITEM_NOT_FOUND"
                };
            }

            // Check stock availability
            if (item.Stock != -1 && item.Stock < quantity)
            {
                return new PurchaseResult 
                { 
                    Success = false, 
                    Message = $"Not enough stock available. Only {item.Stock} remaining.",
                    ErrorCode = "INSUFFICIENT_STOCK"
                };
            }

            // Calculate total cost
            var totalCost = item.Price * quantity;

            // Check user credits
            var userCredits = await GetUserCreditsAsync(userId);
            if (userCredits < totalCost)
            {
                return new PurchaseResult 
                { 
                    Success = false, 
                    Message = $"Not enough credits. You need {totalCost} credits but only have {userCredits}.",
                    ErrorCode = "INSUFFICIENT_CREDITS",
                    TotalCost = totalCost,
                    RemainingCredits = userCredits
                };
            }

            // Process the purchase
            await using var db = await dbProvider.GetConnectionAsync();
            await using var transaction = await db.BeginTransactionAsync();

            try
            {
                // Deduct credits
                await db.GetTable<Database.Linq.Models.Bot.User>()
                    .Where(u => u.UserId == userId)
                    .Set(u => u.MewCoins, u => u.MewCoins - (ulong)totalCost)
                    .UpdateAsync();

                // Add item to user's inventory
                await AddItemToInventoryAsync(db, userId, item, quantity);

                // Update stock if applicable
                if (item.Stock != -1)
                {
                    await UpdateItemStockAsync(db, itemId, item.Stock - quantity);
                }

                // Log the purchase
                await LogPurchaseAsync(db, userId, item, quantity, totalCost);

                await transaction.CommitAsync();

                return new PurchaseResult 
                { 
                    Success = true, 
                    Message = $"Successfully purchased {quantity}x {item.Name}!",
                    Item = item,
                    Quantity = quantity,
                    TotalCost = totalCost,
                    RemainingCredits = userCredits - totalCost
                };
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error purchasing item {ItemId} for user {UserId}", itemId, userId);
            return new PurchaseResult 
            { 
                Success = false, 
                Message = "An error occurred while processing your purchase.",
                ErrorCode = "PURCHASE_ERROR"
            };
        }
    }

    /// <summary>
    ///     Checks if a user can access the radiant shop.
    /// </summary>
    /// <param name="userId">The user ID to check.</param>
    /// <returns>True if the user can access the radiant shop, false otherwise.</returns>
    public async Task<bool> CanAccessRadiantShopAsync(ulong userId)
    {
        try
        {
            return await GetCollectionSizeAsync(userId) >= ShopConstants.MinimumPokemonRequired;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error checking radiant shop access for user {UserId}", userId);
            return false;
        }
    }

    /// <summary>
    ///     Gets the user's current credit balance.
    /// </summary>
    /// <param name="userId">The user ID to check.</param>
    /// <returns>The user's current credit balance.</returns>
    public async Task<int> GetUserCreditsAsync(ulong userId)
    {
        try
        {
            await using var db = await dbProvider.GetConnectionAsync();
            
            var user = await db.GetTable<Database.Linq.Models.Bot.User>()
                .Where(u => u.UserId == userId)
                .FirstOrDefaultAsync();

            return (int)(user?.MewCoins ?? 0);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error retrieving credits for user {UserId}", userId);
            return 0;
        }
    }

    /// <summary>
    ///     Gets shop items from MongoDB if available.
    /// </summary>
    /// <returns>A list of shop items from the database.</returns>
    private async Task<List<ShopItem>> GetMongoShopItemsAsync()
    {
        try
        {
            var mongoItems = await mongoService.Shop.Find(_ => true).ToListAsync();
            var items = new List<ShopItem>();

            foreach (var mongoItem in mongoItems)
            {
                var item = new ShopItem
                {
                    Id = mongoItem.Id?.ToString() ?? Guid.NewGuid().ToString(),
                    Name = mongoItem.Item ?? "Unknown Item",
                    Description = mongoItem.Description ?? "No description available.",
                    Price = (int)mongoItem.Price,
                    Category = "Radiant", // MongoDB items are radiant shop items
                    IsConsumable = true,
                    Stock = -1, // MongoDB items typically have unlimited stock
                    Rarity = "Rare", // MongoDB items are typically rare
                    ImageUrl = null,
                    Metadata = new Dictionary<string, object>
                    {
                        ["MongoId"] = mongoItem.Id?.ToString() ?? "",
                        ["Type"] = mongoItem.Type,
                        ["SecondType"] = mongoItem.SecondType
                    }
                };

                items.Add(item);
            }

            return items;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error retrieving MongoDB shop items");
            return new List<ShopItem>();
        }
    }

    /// <summary>
    ///     Gets event-specific items that are currently available.
    /// </summary>
    /// <returns>A list of event shop items.</returns>
    private async Task<List<ShopItem>> GetEventItemsAsync()
    {
        try
        {
            var items = new List<ShopItem>();

            // Add seasonal items based on current date
            var currentDate = DateTimeOffset.UtcNow;
            var month = currentDate.Month;

            switch (month)
            {
                case 12 or 1: // Winter/Holiday
                    items.Add(new ShopItem
                    {
                        Id = "winter_boost",
                        Name = "Winter Boost",
                        Description = "Increased ice-type Pokemon encounter rate during winter months.",
                        Price = 15000,
                        Category = "Seasonal",
                        IsConsumable = true,
                        Stock = 10,
                        PokemonType = "Ice",
                        Rarity = "Rare"
                    });
                    break;

                case 6 or 7 or 8: // Summer
                    items.Add(new ShopItem
                    {
                        Id = "summer_boost",
                        Name = "Summer Boost",
                        Description = "Increased fire-type Pokemon encounter rate during summer months.",
                        Price = 15000,
                        Category = "Seasonal",
                        IsConsumable = true,
                        Stock = 10,
                        PokemonType = "Fire",
                        Rarity = "Rare"
                    });
                    break;
            }

            return items;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error generating event items");
            return new List<ShopItem>();
        }
    }

    /// <summary>
    ///     Applies filters to the shop items list.
    /// </summary>
    /// <param name="items">The items to filter.</param>
    /// <param name="filters">The filters to apply.</param>
    /// <param name="userCredits">The user's available credits.</param>
    /// <returns>The filtered list of items.</returns>
    private static List<ShopItem> ApplyFilters(List<ShopItem> items, ShopFilters filters, int userCredits)
    {
        var filteredItems = items.AsEnumerable();

        if (!string.IsNullOrEmpty(filters.PokemonType))
        {
            filteredItems = filteredItems.Where(i => i.PokemonType?.Equals(filters.PokemonType, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (!string.IsNullOrEmpty(filters.Category))
        {
            filteredItems = filteredItems.Where(i => i.Category.Equals(filters.Category, StringComparison.OrdinalIgnoreCase));
        }

        if (filters.MinPrice.HasValue)
        {
            filteredItems = filteredItems.Where(i => i.Price >= filters.MinPrice.Value);
        }

        if (filters.MaxPrice.HasValue)
        {
            filteredItems = filteredItems.Where(i => i.Price <= filters.MaxPrice.Value);
        }

        if (!string.IsNullOrEmpty(filters.Rarity))
        {
            filteredItems = filteredItems.Where(i => i.Rarity.Equals(filters.Rarity, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(filters.ItemName))
        {
            filteredItems = filteredItems.Where(i => i.Name.Contains(filters.ItemName, StringComparison.OrdinalIgnoreCase));
        }

        if (filters.ShowRadiantOnly)
        {
            filteredItems = filteredItems.Where(i => i.IsRadiant);
        }

        if (filters.ShowAvailableOnly)
        {
            filteredItems = filteredItems.Where(i => i.Stock == -1 || i.Stock > 0);
        }

        if (filters.ShowAffordableOnly)
        {
            filteredItems = filteredItems.Where(i => i.Price <= userCredits);
        }

        return filteredItems.ToList();
    }

    /// <summary>
    ///     Applies sorting to the shop items list.
    /// </summary>
    /// <param name="items">The items to sort.</param>
    /// <param name="sortOrder">The sort order to apply.</param>
    /// <returns>The sorted list of items.</returns>
    private static List<ShopItem> ApplySorting(List<ShopItem> items, ShopSortOrder sortOrder)
    {
        return sortOrder switch
        {
            ShopSortOrder.PriceAscending => items.OrderBy(i => i.Price).ToList(),
            ShopSortOrder.PriceDescending => items.OrderByDescending(i => i.Price).ToList(),
            ShopSortOrder.NameAscending => items.OrderBy(i => i.Name).ToList(),
            ShopSortOrder.NameDescending => items.OrderByDescending(i => i.Name).ToList(),
            ShopSortOrder.RarityAscending => items.OrderBy(i => GetRarityOrder(i.Rarity)).ToList(),
            ShopSortOrder.RarityDescending => items.OrderByDescending(i => GetRarityOrder(i.Rarity)).ToList(),
            ShopSortOrder.Category => items.OrderBy(i => i.Category).ThenBy(i => i.Name).ToList(),
            ShopSortOrder.Availability => items.OrderByDescending(i => i.Stock == -1 ? int.MaxValue : i.Stock).ToList(),
            _ => items
        };
    }

    /// <summary>
    ///     Gets the sort order value for a rarity string.
    /// </summary>
    /// <param name="rarity">The rarity string.</param>
    /// <returns>The sort order value.</returns>
    private static int GetRarityOrder(string rarity)
    {
        return rarity switch
        {
            "Common" => 1,
            "Uncommon" => 2,
            "Rare" => 3,
            "Epic" => 4,
            "Legendary" => 5,
            "Mythical" => 6,
            _ => 0
        };
    }


    /// <summary>
    ///     Adds an item to a user's inventory.
    /// </summary>
    /// <param name="db">The database connection.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="item">The item to add.</param>
    /// <param name="quantity">The quantity to add.</param>
    private static async Task AddItemToInventoryAsync(DittoDataConnection db, ulong userId, ShopItem item, int quantity)
    {
        // Implementation would depend on your inventory system
        // This is a placeholder for the actual inventory logic
        await Task.CompletedTask;
    }

    /// <summary>
    ///     Updates the stock count for an item.
    /// </summary>
    /// <param name="db">The database connection.</param>
    /// <param name="itemId">The item ID.</param>
    /// <param name="newStock">The new stock count.</param>
    private static async Task UpdateItemStockAsync(DittoDataConnection db, string itemId, int newStock)
    {
        // Implementation would depend on your item stock tracking system
        // This is a placeholder for the actual stock update logic
        await Task.CompletedTask;
    }

    /// <summary>
    ///     Logs a purchase transaction.
    /// </summary>
    /// <param name="db">The database connection.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="item">The purchased item.</param>
    /// <param name="quantity">The quantity purchased.</param>
    /// <param name="totalCost">The total cost.</param>
    private static async Task LogPurchaseAsync(DittoDataConnection db, ulong userId, ShopItem item, int quantity, int totalCost)
    {
        // Implementation would depend on your transaction logging system
        // This is a placeholder for the actual purchase logging logic
        await Task.CompletedTask;
    }

    #region Buy Methods (Consolidated from Items Module)

    /// <summary>
    ///     The vitamins that can be bought by name.
    /// </summary>
    public static readonly string[] Vitamins = ["hp-up", "protein", "iron", "calcium", "zinc", "carbos"];

    /// <summary>
    ///     Buys an item by name, routing vitamins, rare candy, daycare spaces and redeems to their own purchases.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="itemName">The item's name, with words joined by hyphens.</param>
    /// <param name="amount">How many to buy, or null for the default.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> BuyByNameAsync(ulong userId, string itemName, int? amount)
    {
        itemName = itemName.Trim().Replace(' ', '-').ToLowerInvariant();
        if (Vitamins.Contains(itemName))
            return await BuyVitaminsAsync(userId, itemName, amount ?? 1);
        if (itemName is "candy" or "rare-candy")
            return await BuyCandyAsync(userId, amount ?? 1);
        if (itemName is "daycare" or "daycare-space")
            return await BuyDaycareAsync(userId, amount ?? 1);
        if (itemName is "redeems" or "redeem")
            return await BuyRedeemsAsync(userId, (ulong?)amount);
        if (amount is > 1)
            return CommandResult.Error($"You can only buy one {itemName} at a time.");
        return await BuyItemAsync(userId, itemName);
    }

    /// <summary>
    ///     Buys an item from the shop and either equips it to the selected Pokemon or adds it to inventory.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="itemName">The name of the item to buy.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> BuyItemAsync(ulong userId, string itemName)
    {
        itemName = itemName.Replace(" ", "-").ToLower();

        if (itemName == "daycare-space")
            return CommandResult.Error("Use `/shop buy daycare`, not `/shop buy item daycare-space`.");

        var item = await mongoService.Shop.Find(x => x.Item == itemName).FirstOrDefaultAsync();
        if (item == null) return CommandResult.Error("That Item is not in the market");

        var price = (ulong)item.Price;
        await using var db = await dbProvider.GetConnectionAsync();

        var data = await (
            from user in db.Users
            where user.UserId == userId
            select new { user.Items, user.MewCoins, user.Selected }
        ).FirstOrDefaultAsync();

        if (data == null) return CommandResult.Error("You have not started!\nStart with `/start` first.");

        if (data.MewCoins < price) return CommandResult.Error($"You don't have {price} credits!");

        if (itemName == "market-space")
        {
            if (data.MewCoins < 30000)
                return CommandResult.Error($"You need 30,000 credits to buy a market space! You only have {data.MewCoins}...");

            var marketLimit = await db.Users
                .Where(u => u.UserId == userId)
                .Select(u => u.MarketLimit)
                .FirstOrDefaultAsync();

            if (marketLimit >= MaxMarketSlots)
                return CommandResult.Error("You already have the maximum number of market spaces!");

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.MarketLimit, u => u.MarketLimit + 1)
                .Set(u => u.MewCoins, u => u.MewCoins - 30000)
                .UpdateAsync();

            return CommandResult.Success("You have successfully bought an extra market space!");
        }

        if (itemName.EndsWith("-rod"))
        {
            var fishingLevel = await db.Users
                .Where(u => u.UserId == userId)
                .Select(u => u.FishingLevel)
                .FirstOrDefaultAsync();

            switch (itemName)
            {
                case "supreme-rod" when fishingLevel < 105:
                    return CommandResult.Error("You need to be fishing level 105 to use this item!");
                case "epic-rod" when fishingLevel < 150:
                    return CommandResult.Error("You need to be fishing level 150 to use this item!");
                case "master-rod" when fishingLevel < 200:
                    return CommandResult.Error("You need to be fishing level 200 to use this item!");
            }

            var items = JsonSerializer.Deserialize<Dictionary<string, int>>(data.Items ?? "{}") ??
                        new Dictionary<string, int>();
            items[itemName] = items.GetValueOrDefault(itemName, 0) + 1;

            var serializedItems = JsonSerializer.Serialize(items);

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.MewCoins, u => u.MewCoins - price)
                .Set(u => u.Items, serializedItems)
                .UpdateAsync();

            return CommandResult.Success($"You have successfully bought the {itemName}!");
        }

        if (_activeItemList.Contains(itemName))
        {
            var items = JsonSerializer.Deserialize<Dictionary<string, int>>(data.Items ?? "{}") ??
                        new Dictionary<string, int>();
            items[itemName] = items.GetValueOrDefault(itemName, 0) + 1;

            var serializedItems = JsonSerializer.Serialize(items);

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.MewCoins, u => u.MewCoins - price)
                .Set(u => u.Items, serializedItems)
                .UpdateAsync();

            return CommandResult.Success($"You have successfully bought a {itemName}! Use it with `/items apply {itemName}`.");
        }

        if (data.Selected == null)
            return CommandResult.Error(
                "You do not have a selected pokemon and the item you are trying to buy requires one!\nUse `/select` to select a pokemon.");

        var selectedPokemon = await db.UserPokemon.FirstOrDefaultAsync(p => p.Id == data.Selected);
        if (selectedPokemon == null) return CommandResult.Error("Selected pokemon not found!");

        switch (itemName)
        {
            case "ability-capsule":
            {
                var formInfo = await mongoService.Forms.Find(f => f.Identifier == selectedPokemon.PokemonName.ToLower())
                    .FirstOrDefaultAsync();

                var abilityIds = await mongoService.PokeAbilities
                    .Find(a => a.PokemonId == formInfo.PokemonId)
                    .ToListAsync();

                if (abilityIds.Count <= 1)
                    return CommandResult.Error("That Pokemon cannot have its ability changed!");

                var newIndex = (selectedPokemon.AbilityIndex + 1) % abilityIds.Count;
                var newAbilityId = abilityIds[newIndex].AbilityId;

                await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.AbilityIndex, newIndex).UpdateAsync();

                var newAbility = await mongoService.Abilities.Find(a => a.AbilityId == newAbilityId).FirstOrDefaultAsync();

                await db.Users.Where(u => u.UserId == userId)
                    .Set(u => u.MewCoins, x => x.MewCoins - price).UpdateAsync();

                return CommandResult.Success($"You have Successfully changed your Pokémon's ability to {newAbility.Identifier}");
            }
            case "ev-reset":
                await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.HpEv, 0)
                    .Set(p => p.AttackEv, 0)
                    .Set(p => p.DefenseEv, 0)
                    .Set(p => p.SpecialAttackEv, 0)
                    .Set(p => p.SpecialDefenseEv, 0)
                    .Set(p => p.SpeedEv, 0)
                    .UpdateAsync();

                await db.Users.Where(u => u.UserId == userId)
                    .Set(u => u.MewCoins, x => x.MewCoins - price).UpdateAsync();

                return CommandResult.Success("You have successfully reset the Effort Values (EVs) of your selected Pokemon!");
        }

        if (IsFormed(selectedPokemon.PokemonName))
            return CommandResult.Error("You can not buy an item for a Form. Use `/deform` to de-form your Pokemon!");

        if (selectedPokemon.HeldItem.ToLower() != "none")
            return CommandResult.Error("You already have an item equipped!");

        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.MewCoins, x => x.MewCoins - price).UpdateAsync();

        await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
            .Set(p => p.HeldItem, itemName).UpdateAsync();

        var evolveResult = await pokemonService.TryEvolve(selectedPokemon.Id);

        return CommandResult.Success($"You have successfully bought the {itemName} for your {selectedPokemon.PokemonName}");
    }

    /// <summary>
    ///     Buys additional daycare spaces for the user.
    ///     Each space costs 10,000 credits.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="amount">The number of daycare spaces to buy.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> BuyDaycareAsync(ulong userId, int amount)
    {
        if (amount < 0) return CommandResult.Error("Yeah... negative numbers won't work here. Try again");

        var price = (ulong)(10000 * amount);
        await using var db = await dbProvider.GetConnectionAsync();
        var balance = await db.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.MewCoins)
            .FirstOrDefaultAsync();

        if (balance == null) return CommandResult.Error("You have not started!\nStart with `/start` first.");

        if (price > balance)
            return CommandResult.Error(
                $"You cannot afford that many daycare spaces! You need {price} credits, but you only have {balance}.");

        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.MewCoins, u => u.MewCoins - price)
            .Set(u => u.DaycareLimit, u => u.DaycareLimit + amount)
            .UpdateAsync();

        var pluralSpaces = amount == 1 ? "space" : "spaces";
        return CommandResult.Success($"You have successfully bought {amount} daycare {pluralSpaces}!");
    }

    /// <summary>
    ///     Buys vitamins to increase the EVs of the user's selected Pokémon.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="vitaminName">The name of the vitamin to buy.</param>
    /// <param name="amount">The amount of the vitamin to buy.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> BuyVitaminsAsync(ulong userId, string vitaminName, int amount)
    {
        amount = Math.Max(0, amount);
        vitaminName = vitaminName.Trim();
        var itemInfo = await mongoService.Shop.Find(x => x.Item == vitaminName).FirstOrDefaultAsync();
        if (itemInfo == null) return CommandResult.Error("That Item is not in the market");

        await using var db = await dbProvider.GetConnectionAsync();
        var totalPrice = (ulong)(amount * 100);
        var selectedId = await db.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.Selected)
            .FirstOrDefaultAsync();

        var selectedPokemon = await db.UserPokemon
            .FirstOrDefaultAsync(p => p.Id == selectedId);

        if (selectedPokemon == null)
            return CommandResult.Error("You don't have a pokemon selected!\nSelect one with `/select` first.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null || user.MewCoins < totalPrice)
            return CommandResult.Error($"You do not have {totalPrice} credits!");

        try
        {
            var evTotal = selectedPokemon.HpEv + selectedPokemon.AttackEv + selectedPokemon.DefenseEv +
                          selectedPokemon.SpecialAttackEv + selectedPokemon.SpecialDefenseEv + selectedPokemon.SpeedEv;

            if (evTotal + amount > 510)
                return CommandResult.Error("Your Pokemon has maxed all 510 EVs or 252 EVs for that stat.");

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.MewCoins, x => x.MewCoins - totalPrice).UpdateAsync();

            var updated = vitaminName switch
            {
                "calcium" => await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.SpecialAttackEv, x => x.SpecialAttackEv + 10).UpdateAsync(),
                "carbos" => await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.SpeedEv, x => x.SpeedEv + 10).UpdateAsync(),
                "hp-up" => await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.HpEv, x => x.HpEv + 10).UpdateAsync(),
                "iron" => await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.DefenseEv, x => x.DefenseEv + 10).UpdateAsync(),
                "protein" => await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.AttackEv, x => x.AttackEv + 10).UpdateAsync(),
                "zinc" => await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.SpecialDefenseEv, x => x.SpecialDefenseEv + 10).UpdateAsync(),
                _ => throw new ArgumentException("Invalid vitamin type")
            };

            return updated == 0
                ? CommandResult.Error("Your Pokemon has maxed all 510 EVs or 252 EVs for that stat.")
                : CommandResult.Success($"You have successfully bought {amount} {vitaminName} for your {selectedPokemon.PokemonName}");
        }
        catch
        {
            return CommandResult.Error("Your Pokemon has maxed all 510 EVs or 252 EVs for that stat.");
        }
    }

    /// <summary>
    ///     Buys and applies Rare Candy to level up the user's selected Pokémon.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="amount">The number of Rare Candies to buy and use.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> BuyCandyAsync(ulong userId, int amount)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var selectedId = await db.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.Selected)
            .FirstOrDefaultAsync();

        if (selectedId == null) return CommandResult.Error("You need to select a pokemon first!");

        var ownedPoke = await db.UserPokemonOwnerships.FirstOrDefaultAsync(p => p.Position == (selectedId-1) && p.UserId == userId);
        if (ownedPoke == null) return CommandResult.Error("Selected pokemon not found!");
        var selectedPokemon = await db.UserPokemon.FirstOrDefaultAsync(x => x.Id == ownedPoke.PokemonId);

        var credits = await db.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.MewCoins)
            .FirstOrDefaultAsync();

        var useAmount = Math.Max(0, Math.Min(100 - selectedPokemon.Level, amount));
        var buyAmount = useAmount == 0 ? 1 : useAmount;
        var price = (ulong)(buyAmount * 100);
        var candyStr = buyAmount == 1 ? "candy" : "candies";

        if (price > credits)
            return CommandResult.Error($"You do not have {price} credits for {buyAmount} Rare {candyStr}");

        try
        {
            await db.UserPokemon.Where(p => p.Id == ownedPoke.PokemonId)
                .Set(p => p.Level, x => x.Level + useAmount).UpdateAsync();

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.MewCoins, x => x.MewCoins - price).UpdateAsync();

            var newLevel = selectedPokemon.Level + useAmount;
            var evolveResult = await pokemonService.TryEvolve(selectedId.GetValueOrDefault(), overrideLvl100: true);

            return CommandResult.Success($"Your {selectedPokemon.PokemonName} has successfully leveled up to Level {newLevel}.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in Buy Candy - Poke: {Id} | Expected level - {Level}", selectedId,
                selectedPokemon.Level + useAmount);
            return CommandResult.Error("Sorry, I can't do that right now. Try again in a moment.");
        }
    }

    /// <summary>
    ///     The chest types, from cheapest to dearest.
    /// </summary>
    public static readonly string[] ChestTypes = ["rare", "mythic", "legend"];

    /// <summary>
    ///     The price of each chest type, by currency (credits or redeems).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ChestPrices =
        new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["credits"] = new Dictionary<string, int> { ["rare"] = 300000, ["mythic"] = 600000, ["legend"] = 2000000 },
            ["redeems"] = new Dictionary<string, int> { ["rare"] = 7, ["mythic"] = 10, ["legend"] = 33 }
        };

    /// <summary>
    ///     How many chests of each type can be bought with credits each week.
    /// </summary>
    public const int MaxWeeklyCreditChests = 5;

    /// <summary>
    ///     Gets how many chests of each type a user has bought with credits this week.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <returns>The count by chest type.</returns>
    public async Task<Dictionary<string, int>> GetWeeklyChestPurchasesAsync(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var store = await db.ChestStore.FirstOrDefaultAsync(c => c.UserId == userId);
        var currentWeek = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 604800;
        if (store == null || !long.TryParse(store.Restock, out var restock) || restock <= currentWeek)
            return ChestTypes.ToDictionary(t => t, _ => 0);

        return new Dictionary<string, int>
        {
            ["rare"] = store.Rare,
            ["mythic"] = store.Mythic,
            ["legend"] = store.Legend
        };
    }

    /// <summary>
    ///     Counts the Pokémon in a user's collection, for the shop's minimum.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    public async Task<int> GetCollectionSizeAsync(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        return await db.GetTable<Database.Linq.Models.Pokemon.UserPokemonOwnership>()
            .Where(p => p.UserId == userId)
            .CountAsync();
    }

    /// <summary>
    ///     Buys a treasure chest that can contain rare Pokémon or items.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="chestType">The type of chest to buy (rare, mythic, or legend).</param>
    /// <param name="currency">The currency to use for purchase (credits or redeems).</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> BuyChestAsync(ulong userId, string chestType, string currency)
    {
        var ct = chestType.ToLower().Trim();
        var cor = currency.ToLower();

        if (!ChestTypes.Contains(ct))
            return CommandResult.Error($"`{ct}` is not a valid chest type! Choose one of Rare, Mythic, or Legend.");

        if (!new[] { "credits", "redeems" }.Contains(cor))
            return CommandResult.Error("Specify either \"credits\" or \"redeems\"!");

        var price = (ulong)ChestPrices[cor][ct];
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null)
            return CommandResult.Error("You have not started!\nStart with `/start` first.");

        if (cor == "credits")
        {
            if (user.MewCoins < price)
                return CommandResult.Error($"You do not have the {price} credits you need to buy a {ct} chest!");

            var chestStore = await db.ChestStore.FirstOrDefaultAsync(c => c.UserId == userId);
            if (chestStore == null)
            {
                chestStore = new ChestStore
                {
                    UserId = userId,
                    Restock = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 604800 + 1).ToString()
                };
                await db.InsertAsync(chestStore);
            }

            var currentWeek = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 604800).ToString();
            if (long.Parse(chestStore.Restock) <= long.Parse(currentWeek))
            {
                chestStore.Rare = 0;
                chestStore.Mythic = 0;
                chestStore.Legend = 0;
                chestStore.Restock = (long.Parse(currentWeek) + 1).ToString();
            }

            const int maxChests = MaxWeeklyCreditChests;
            var currentAmount = ct switch
            {
                "rare" => chestStore.Rare,
                "mythic" => chestStore.Mythic,
                "legend" => chestStore.Legend,
                _ => throw new ArgumentException("Invalid chest type")
            };

            if (currentAmount + 1 > maxChests)
                return CommandResult.Error(
                    $"You can't buy more than {maxChests} per week using credits! You've already bought {currentAmount}.");

            // Update chest count
            switch (ct)
            {
                case "legend":
                    chestStore.Legend++;
                    break;
                case "mythic":
                    chestStore.Mythic++;
                    break;
                case "rare":
                    chestStore.Rare++;
                    break;
            }

            await db.ChestStore.Where(c => c.UserId == chestStore.UserId)
                .Set(c => c.Rare, chestStore.Rare)
                .Set(c => c.Mythic, chestStore.Mythic)
                .Set(c => c.Legend, chestStore.Legend)
                .Set(c => c.Restock, chestStore.Restock)
                .UpdateAsync();
            
            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.MewCoins, u => u.MewCoins - price)
                .UpdateAsync();
        }
        else // redeems
        {
            if ((ulong)user.Redeems.GetValueOrDefault() < price)
                return CommandResult.Error($"You do not have the {price} redeems you need to buy a {ct} chest!");

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.Redeems, u => u.Redeems.GetValueOrDefault() - price)
                .UpdateAsync();
        }

        var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Inventory ?? "{}") ??
                        new Dictionary<string, int>();
        var item = $"{ct} chest";
        inventory[item] = inventory.GetValueOrDefault(item, 0) + 1;

        var serializedItems = JsonSerializer.Serialize(inventory);

        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.Inventory, serializedItems).UpdateAsync();

        return CommandResult.Success(
            $"You have successfully bought a {ct} chest for {price} {cor}!\nYou can open it with `/open {ct}`.");
    }

    /// <summary>
    ///     Buys redeems for the user, which can be used to redeem special Pokémon or items.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="amount">The number of redeems to buy, or null to show current purchase stats.</param>
    /// <returns>A CommandResult containing the operation result message or status information.</returns>
    public async Task<CommandResult> BuyRedeemsAsync(ulong userId, ulong? amount = null)
    {
        if (amount is < 1) return CommandResult.Error("Nice try...");

        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null)
            return CommandResult.Error("You have not started!\nStart with `/start` first.");

        var redeemStore = await db.RedeemStore.FirstOrDefaultAsync(r => r.UserId == userId);
        if (redeemStore == null)
        {
            redeemStore = new RedeemStore
            {
                UserId = userId,
                Restock = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 604800 + 1).ToString()
            };
            await db.InsertAsync(redeemStore);
        }

        const int maxRedeems = 100;
        const int restockTime = 604800;

        var currentWeek = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / restockTime).ToString();
        if (long.Parse(redeemStore.Restock) <= long.Parse(currentWeek))
        {
            await db.RedeemStore.Where(r => r.UserId == redeemStore.UserId)
                .Set(r => r.Bought, 0UL)
                .Set(r => r.Restock, (long.Parse(currentWeek) + 1).ToString())
                .UpdateAsync();
            redeemStore.Bought = 0;
            redeemStore.Restock = (long.Parse(currentWeek) + 1).ToString();
        }

        if (!amount.HasValue)
        {
            var embed = new EmbedBuilder();
            if (redeemStore.Restock != "0")
            {
                var desc = $"You have bought {redeemStore.Bought} redeems this week.\n";
                if (redeemStore.Bought >= maxRedeems)
                    desc += "You cannot buy any more this week.";
                else
                    desc += "Buy more using `/shop buy redeems <amount>`!";

                embed.WithTitle("Buy redeems")
                    .WithDescription(desc)
                    .WithColor(new Color(255, 182, 193))
                    .WithFooter("Redeems restock every Wednesday at 8pm ET.");
            }
            else
            {
                embed.WithTitle("Buy redeems")
                    .WithDescription("You haven't bought any redeems yet! Use `/shop buy redeems <amount>`!")
                    .WithColor(new Color(255, 182, 193));
            }

            return CommandResult.WithEmbed(embed.Build(), true);
        }

        if (redeemStore.Bought + amount.Value > maxRedeems)
            return CommandResult.Error($"You can't buy more than {maxRedeems} per week! You've already bought {redeemStore.Bought}.");

        const int creditsPerRedeem = 60000;
        var price = (ulong)(amount.Value * creditsPerRedeem);

        if (user.MewCoins < price)
            return CommandResult.Error($"You do not have the {price} credits to buy those redeems!");

        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.Redeems, u => u.Redeems + amount.Value)
            .Set(u => u.MewCoins, u => u.MewCoins - price)
            .UpdateAsync();

        var newBought = redeemStore.Bought + amount.Value;
        var newRestock = redeemStore.Restock == "0" 
            ? (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 604800 + 1).ToString() 
            : redeemStore.Restock;

        await db.RedeemStore.Where(r => r.UserId == redeemStore.UserId)
            .Set(r => r.Bought, newBought)
            .Set(r => r.Restock, newRestock)
            .UpdateAsync();

        return CommandResult.Success($"You have successfully bought {amount.Value} redeem{(amount.Value == 1 ? "" : "s")} for {price} credits!");
    }

    #endregion

    #region Category-Specific Item Retrieval

    /// <summary>
    ///     Gets general shop items (items, vitamins, daycare, etc.).
    /// </summary>
    /// <returns>A list of general shop items.</returns>
    private async Task<List<ShopItem>> GetGeneralItemsAsync()
    {
        try
        {
            var items = new List<ShopItem>();

            // Add basic items
            items.AddRange(new List<ShopItem>
            {
                new() { Id = "market-space", Name = "Market Space", Description = "Expand your market slots to sell more Pokemon", Price = 10000, Category = "General", Stock = -1 },
                new() { Id = "daycare-space", Name = "Daycare Space", Description = "Additional space in the daycare for breeding", Price = 10000, Category = "General", Stock = -1 },
                new() { Id = "hp-up", Name = "HP Up", Description = "Increase HP EVs by 10", Price = 100, Category = "General", Stock = -1 },
                new() { Id = "protein", Name = "Protein", Description = "Increase Attack EVs by 10", Price = 100, Category = "General", Stock = -1 },
                new() { Id = "iron", Name = "Iron", Description = "Increase Defense EVs by 10", Price = 100, Category = "General", Stock = -1 },
                new() { Id = "calcium", Name = "Calcium", Description = "Increase Special Attack EVs by 10", Price = 100, Category = "General", Stock = -1 },
                new() { Id = "zinc", Name = "Zinc", Description = "Increase Special Defense EVs by 10", Price = 100, Category = "General", Stock = -1 },
                new() { Id = "carbos", Name = "Carbos", Description = "Increase Speed EVs by 10", Price = 100, Category = "General", Stock = -1 },
                new() { Id = "rare-candy", Name = "Rare Candy", Description = "Increase Pokemon level by 1", Price = 100, Category = "General", Stock = -1 }
            });

            // Add evolution stones and active items
            foreach (var item in _activeItemList)
            {
                var displayName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(item.Replace("-", " "));
                items.Add(new ShopItem
                {
                    Id = item,
                    Name = displayName,
                    Description = $"Evolution item: {displayName}",
                    Price = 5000,
                    Category = "General",
                    Stock = -1
                });
            }

            // Add berries
            foreach (var berry in _berryList)
            {
                var displayName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(berry.Replace("-", " "));
                items.Add(new ShopItem
                {
                    Id = berry,
                    Name = displayName,
                    Description = $"Berry: {displayName}",
                    Price = 50,
                    Category = "General",
                    Stock = -1
                });
            }

            return items;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error retrieving general items");
            return new List<ShopItem>();
        }
    }

    /// <summary>
    ///     Gets radiant shop items (exclusive and rare items).
    /// </summary>
    /// <returns>A list of radiant shop items.</returns>
    private async Task<List<ShopItem>> GetRadiantItemsAsync()
    {
        try
        {
            var items = new List<ShopItem>(ShopConstants.DefaultItems);

            // Add seasonal/event items
            var eventItems = await GetEventItemsAsync();
            items.AddRange(eventItems);

            // Add shop items from MongoDB if available
            var mongoItems = await GetMongoShopItemsAsync();
            items.AddRange(mongoItems);

            return items;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error retrieving radiant items");
            return new List<ShopItem>();
        }
    }

    /// <summary>
    ///     Gets crystal slime exchange items from the missions system.
    /// </summary>
    /// <returns>A list of crystal slime exchange items.</returns>
    private async Task<List<ShopItem>> GetCrystalSlimeItemsAsync()
    {
        try
        {
            var items = new List<ShopItem>
            {
                new()
                {
                    Id = "credits-exchange",
                    Name = "Credits Exchange",
                    Description = "Exchange 150 Crystallized Slime for 100,000 credits",
                    Price = 150, // In crystallized slime
                    Category = "CrystalSlime",
                    Stock = -1,
                    Metadata = new Dictionary<string, object> { { "RewardType", "Credits" }, { "RewardAmount", 100000 } }
                },
                new()
                {
                    Id = "friendship-stone",
                    Name = "Friendship Stone",
                    Description = "Increases Pokemon friendship/happiness instantly",
                    Price = 10, // In crystallized slime
                    Category = "CrystalSlime",
                    Stock = -1,
                    Metadata = new Dictionary<string, object> { { "RewardType", "Item" } }
                },
                new()
                {
                    Id = "shadow-essence",
                    Name = "Shadow Essence",
                    Description = "Increase your shadow-chain by 15-75 (random)",
                    Price = 100, // In crystallized slime
                    Category = "CrystalSlime",
                    Stock = -1,
                    Metadata = new Dictionary<string, object> { { "RewardType", "ShadowChain" }, { "MinAmount", 15 }, { "MaxAmount", 75 } }
                },
                new()
                {
                    Id = "meowth-ticket",
                    Name = "Meowth Ticket",
                    Description = "Try your luck with hidden credit amounts (up to 150,000)",
                    Price = 75, // In crystallized slime
                    Category = "CrystalSlime",
                    Stock = -1,
                    Metadata = new Dictionary<string, object> { { "RewardType", "Lottery" } }
                },
                new()
                {
                    Id = "vip-token-1",
                    Name = "VIP Token",
                    Description = "Get exclusive VIP benefits and access",
                    Price = 1000, // In crystallized slime
                    Category = "CrystalSlime",
                    Stock = -1,
                    Metadata = new Dictionary<string, object> { { "RewardType", "VIP" }, { "Amount", 1 } }
                },
                new()
                {
                    Id = "vip-token-3",
                    Name = "VIP Token Bundle (x3)",
                    Description = "Get 3 VIP tokens at a discount price",
                    Price = 2500, // In crystallized slime
                    Category = "CrystalSlime",
                    Stock = -1,
                    Metadata = new Dictionary<string, object> { { "RewardType", "VIP" }, { "Amount", 3 } }
                }
            };

            return items;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error retrieving crystal slime items");
            return new List<ShopItem>();
        }
    }

    #endregion
}