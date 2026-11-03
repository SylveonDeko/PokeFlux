using System.Text.Json;
using EeveeCore.Modules.Pokemon.Services;
using EeveeCore.Services.Impl;
using LinqToDB;
using LinqToDB.Async;
using MongoDB.Driver;
using Serilog;
using ChestStore = EeveeCore.Database.Linq.Models.Game.ChestStore;
using RedeemStore = EeveeCore.Database.Linq.Models.Game.RedeemStore;

namespace EeveeCore.Modules.Items.Services;

/// <summary>
///     Provides functionality for managing Pokémon items, equipment, and purchases.
///     Handles item operations such as equipping, unequipping, buying, and applying items to Pokémon.
///     Also manages special items like evolution stones, berries, and market spaces.
/// </summary>
public class ItemsService(
    IMongoService mongoDb,
    LinqToDbConnectionProvider dbContextProvider,
    PokemonService pokemonService) : INService
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
    ///     Cache service for storing and retrieving frequently used data.
    /// </summary>
    private readonly IDataCache _cache;

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
    private bool IsFormed(string? pokemonName)
    {
        return pokemonName.EndsWith("-mega") || pokemonName.EndsWith("-x") || pokemonName.EndsWith("-y") ||
               pokemonName.EndsWith("-origin") || pokemonName.EndsWith("-10") || pokemonName.EndsWith("-complete") ||
               pokemonName.EndsWith("-ultra") || pokemonName.EndsWith("-crowned") ||
               pokemonName.EndsWith("-eternamax") ||
               pokemonName.EndsWith("-blade");
    }

    /// <summary>
    ///     Prepares for item removal by validating the user's selected Pokémon and its held item.
    ///     Checks for various conditions that would prevent item removal, such as formed Pokémon.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <returns>
    ///     A tuple containing operation success status, error message if applicable,
    ///     held item, Pokémon name, and the user's inventory.
    /// </returns>
    public async Task<(bool Success, string Message, string HeldItem, string PokemonName, Dictionary<string, int> Items
            )>
        PrepItemRemove(ulong userId)
    {
        await using var db = await dbContextProvider.GetConnectionAsync();

        var data = await (
            from poke in db.UserPokemon
            join user in db.Users on poke.Id equals user.Selected
            where user.UserId == userId
            select new { poke.HeldItem, poke.PokemonName, user.Items }
        ).FirstOrDefaultAsync();

        if (data == null)
            return (false, "You do not have a pokemon selected!\nSelect one with `/select` first.", null, null, null);

        var items = JsonSerializer.Deserialize<Dictionary<string, int>>(data.Items ?? "{}") ??
                    new Dictionary<string, int>();

        if (data.HeldItem.ToLower() is "none" or null)
            return (false, "Your selected Pokemon is not holding any item!", null, null, null);

        if (data.HeldItem is "megastone" or "mega-stone" or "mega-stone-x" or "mega-stone-y" &&
            (data.PokemonName.EndsWith("-mega") || data.PokemonName.EndsWith("-x") || data.PokemonName.EndsWith("-y")))
            return (false, "Deform this Pokemon before Unequipping the item!", null, null, null);

        if (IsFormed(data.PokemonName) && data.HeldItem is "primal-orb" or "blue-orb" or "red-orb" or
                "griseous-orb" or "ultranecronium-z" or "rusty-sword" or "rusty-shield")
            return (false, "Deform this Pokemon before Unequipping the item!", null, null, null);

        return (true, null, data.HeldItem, data.PokemonName, items);
    }

    /// <summary>
    ///     Unequips an item from the user's selected Pokémon and adds it to their inventory.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> Unequip(ulong userId)
    {
        var prepResult = await PrepItemRemove(userId);
        if (!prepResult.Success) return new CommandResult { Message = prepResult.Message };

        await using var db = await dbContextProvider.GetConnectionAsync();
        prepResult.Items[prepResult.HeldItem] = prepResult.Items.GetValueOrDefault(prepResult.HeldItem, 0) + 1;

        var serializedItems = JsonSerializer.Serialize(prepResult.Items);

        await db.Users
            .Where(u => u.UserId == userId)
            .Set(x => x.Items, serializedItems)
            .UpdateAsync();

        await db.UserPokemon
            .Where(p => p.Id == db.Users
                .Where(u => u.UserId == userId)
                .Select(u => u.Selected)
                .FirstOrDefault())
            .Set(x => x.HeldItem, "None")
            .UpdateAsync();

        return new CommandResult { Message = $"Successfully unequipped a {prepResult.HeldItem} from selected Pokemon" };
    }

    /// <summary>
    ///     Removes an item from the user's selected Pokémon without adding it to their inventory.
    ///     The item is effectively discarded.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> Drop(ulong userId)
    {
        var prepResult = await PrepItemRemove(userId);
        if (!prepResult.Success) return new CommandResult { Message = prepResult.Message };

        await using var db = await dbContextProvider.GetConnectionAsync();
        await db.UserPokemon
            .Where(p => p.Id == db.Users
                .Where(u => u.UserId == userId)
                .Select(u => u.Selected)
                .FirstOrDefault())
            .Set(p => p.HeldItem, "None")
            .UpdateAsync();

        return new CommandResult { Message = $"Successfully Dropped the {prepResult.HeldItem}" };
    }

    /// <summary>
    ///     Transfers an item from the user's selected Pokémon to another Pokémon in their collection.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="pokemonNumber">The target Pokémon's number in the user's list.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> Transfer(ulong userId, ulong pokemonNumber)
    {
        var prepResult = await PrepItemRemove(userId);
        if (!prepResult.Success) return new CommandResult { Message = prepResult.Message };

        await using var db = await dbContextProvider.GetConnectionAsync();

        var selectedId = await db.Users.Where(u => u.UserId == userId).Select(u => u.Selected).FirstOrDefaultAsync();
        var targetId = pokemonNumber == 0
            ? null
            : await db.UserPokemonOwnerships
                .Where(o => o.UserId == userId && o.Position == pokemonNumber - 1)
                .Select(o => (ulong?)o.PokemonId)
                .FirstOrDefaultAsync();
        var targetPokemon = targetId is null
            ? null
            : await db.UserPokemon.FirstOrDefaultAsync(p => p.Id == targetId);

        if (targetPokemon == null)
            return new CommandResult { Message = "You do not have that Pokemon!" };

        if (targetPokemon.Id == selectedId)
            return new CommandResult { Message = "That is the Pokemon already holding the item!" };

        if (targetPokemon.HeldItem.ToLower() != "none")
            return new CommandResult { Message = "That Pokemon is already holding an item" };

        // Remove item from source Pokemon
        await db.UserPokemon.Where(p => p.Id == selectedId)
            .Set(p => p.HeldItem, "None")
            .UpdateAsync();

        // Add item to target Pokemon
        await db.UserPokemon.Where(p => p.Id == targetPokemon.Id)
            .Set(p => p.HeldItem, prepResult.HeldItem)
            .UpdateAsync();

        return new CommandResult
        {
            Message =
                $"You have successfully transfered the {prepResult.HeldItem} from your {prepResult.PokemonName} to your {targetPokemon.PokemonName}!"
        };
    }

    /// <summary>
    ///     Equips an item to the user's selected Pokémon.
    ///     Handles special items like ability capsules, vitamins, and various held items.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="itemName">The name of the item to equip.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> Equip(ulong userId, string itemName)
    {
        itemName = string.Join("-", itemName.Split()).ToLower();

        var itemInfo = await mongoDb.Shop.Find(x => x.Item == itemName).FirstOrDefaultAsync();

        if (itemName == "nature-capsule")
            return new CommandResult
                { Message = "Use `/change nature` to use a nature capsule to change your pokemon's nature." };

        if (!_berryList.Contains(itemName) && itemInfo == null && itemName != "glitchy-orb")
            return new CommandResult { Message = "That Item does not exist!" };

        if (_activeItemList.Contains(itemName))
            return new CommandResult
                { Message = $"That item cannot be equipped! Use it on your poke with `/apply {itemName}`." };

        await using var db = await dbContextProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null) return new CommandResult { Message = "You have not started!\nStart with `/start` first." };

        var items = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Items ?? "{}") ??
                    new Dictionary<string, int>();
        if (items.GetValueOrDefault(itemName, 0) == 0)
            return new CommandResult { Message = $"You do not have any {itemName}!" };

        var selectedPokemon = await db.UserPokemon.FirstOrDefaultAsync(p => p.Id == user.Selected);
        if (selectedPokemon == null)
            return new CommandResult
                { Message = "You do not have a pokemon selected!\nSelect one with `/select` first." };

        if (selectedPokemon.HeldItem.ToLower() != "none")
            return new CommandResult
            {
                Message =
                    $"Your pokemon is already holding the {selectedPokemon.HeldItem}! Unequip it with `/unequip` first!"
            };

        items[itemName]--;

        string? serializedItems;
        switch (itemName)
        {
            case "glitchy-orb":
            {
                if (selectedPokemon.Shiny.GetValueOrDefault() || selectedPokemon.Radiant.GetValueOrDefault() ||
                    selectedPokemon.Skin != null)
                    return new CommandResult
                    {
                        Message =
                            "Please select a pokemon which is not **Shiny or Radiant**, and does not have a **Skin** attached."
                    };

                await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.Skin, "glitch")
                    .UpdateAsync();

                serializedItems = JsonSerializer.Serialize(items);

                await db.Users.Where(u => u.UserId == userId)
                    .Set(u => u.Items, serializedItems)
                    .UpdateAsync();

                return new CommandResult { Message = "The orb disappears - something seems off about your pokemon..." };
            }
            case "ability-capsule":
            {
                var formInfo = await mongoDb.Forms.Find(f => f.Identifier == selectedPokemon.PokemonName.ToLower())
                    .FirstOrDefaultAsync();

                var abilityIds = await mongoDb.PokeAbilities
                    .Find(a => a.PokemonId == formInfo.PokemonId)
                    .ToListAsync();

                if (abilityIds.Count <= 1)
                    return new CommandResult { Message = "That Pokemon cannot have its ability changed!" };

                var newIndex = (selectedPokemon.AbilityIndex + 1) % abilityIds.Count;
                var newAbilityId = abilityIds[newIndex].AbilityId;

                await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.AbilityIndex, newIndex)
                    .UpdateAsync();

                var newAbility = await mongoDb.Abilities.Find(a => a.AbilityId == newAbilityId).FirstOrDefaultAsync();

                serializedItems = JsonSerializer.Serialize(items);

                await db.Users.Where(u => u.UserId == userId)
                    .Set(u => u.Items, serializedItems)
                    .UpdateAsync();

                return new CommandResult
                    { Message = $"You have Successfully changed your Pokémon's ability to {newAbility.Identifier}" };
            }
            case "daycare-space":
                serializedItems = JsonSerializer.Serialize(items);

                await db.Users.Where(u => u.UserId == userId)
                    .Set(x => x.Items, serializedItems)
                    .Set(x => x.DaycareLimit, x => x.DaycareLimit + 1)
                    .UpdateAsync();

                return new CommandResult { Message = "You have successfully equipped an Extra Daycare Space!" };
            case "ev-reset":
                serializedItems = JsonSerializer.Serialize(items);

                await db.Users.Where(u => u.UserId == userId)
                    .Set(u => u.Items, serializedItems)
                    .UpdateAsync();

                await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.HpEv, 0)
                    .Set(p => p.AttackEv, 0)
                    .Set(p => p.DefenseEv, 0)
                    .Set(p => p.SpecialAttackEv, 0)
                    .Set(p => p.SpecialDefenseEv, 0)
                    .Set(p => p.SpeedEv, 0)
                    .UpdateAsync();

                return new CommandResult
                    { Message = "You have successfully reset the Effort Values (EVs) of your selected Pokemon!" };
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
                    return new CommandResult { Message = "You need to be fishing level 105 to use this item!" };
                case "epic-rod" when fishingLevel < 150:
                    return new CommandResult { Message = "You need to be fishing level 150 to use this item!" };
                case "master-rod" when fishingLevel < 200:
                    return new CommandResult { Message = "You need to be fishing level 200 to use this item!" };
            }

            serializedItems = JsonSerializer.Serialize(items);

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.Items, serializedItems)
                .Set(u => u.HeldItem, itemName)
                .UpdateAsync();

            return new CommandResult { Message = $"You have successfully equipped your {itemName}" };
        }

        if (itemName is "zinc" or "hp-up" or "protein" or "calcium" or "iron" or "carbos")
            try
            {
                var updated = itemName switch
                {
                    // Validate itemName and set the appropriate property update
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

                if (updated == 0) return new CommandResult { Message = "Your Pokemon has maxed all 510 EVs" };

                serializedItems = JsonSerializer.Serialize(items);

                await db.Users.Where(u => u.UserId == userId)
                    .Set(u => u.Items, serializedItems)
                    .UpdateAsync();

                return new CommandResult { Message = $"You have successfully used your {itemName}" };
            }
            catch
            {
                return new CommandResult { Message = "Your Pokemon has maxed all 510 EVs" };
            }

        await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
            .Set(p => p.HeldItem, itemName)
            .UpdateAsync();

        serializedItems = JsonSerializer.Serialize(items);

        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.Items, serializedItems)
            .UpdateAsync();
        var evolveResult = await pokemonService.TryEvolve(selectedPokemon.Id);

        return new CommandResult { Message = $"You have successfully given your selected Pokemon a {itemName}" };
    }

    /// <summary>
    ///     Applies an item to the user's selected Pokémon.
    ///     Used for evolution stones and other items that trigger immediate effects.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="itemName">The name of the item to apply.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> Apply(ulong userId, string itemName)
    {
        itemName = string.Join("-", itemName.Split()).ToLower();

        if (itemName == "nature-capsule")
            return new CommandResult
                { Message = "Use `/change nature` to use a nature capsule to change your pokemon's nature." };

        if (!_activeItemList.Contains(itemName))
            return new CommandResult
                { Message = $"That item cannot be used on a poke! Try equipping it with `/equip {itemName}`." };

        await using var db = await dbContextProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null) return new CommandResult { Message = "You have not started!\nStart with `/start` first." };

        var items = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Items ?? "{}") ??
                    new Dictionary<string, int>();
        if (items.GetValueOrDefault(itemName, 0) == 0)
            return new CommandResult { Message = $"You do not have any {itemName}!" };

        var selectedPokemon = await db.UserPokemon.FirstOrDefaultAsync(p => p.Id == user.Selected);
        if (selectedPokemon == null)
            return new CommandResult
                { Message = "You do not have a pokemon selected!\nSelect one with `/select` first." };

        items[itemName]--;

        string? serializedItems;
        if (itemName == "friendship-stone")
        {
            await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                .Set(p => p.Happiness, x => x.Happiness + 300).UpdateAsync();

            serializedItems = JsonSerializer.Serialize(items);

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.Items, serializedItems).UpdateAsync();

            return new CommandResult { Message = $"Your {itemName} was consumed!" };
        }

        var evolveResult = await pokemonService.TryEvolve(selectedPokemon.Id, itemName);
        if (!evolveResult.Success) return new CommandResult { Message = $"The {itemName} had no effect!" };

        serializedItems = JsonSerializer.Serialize(items);

        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.Items, serializedItems).UpdateAsync();

        return new CommandResult { Message = $"Your {itemName} was consumed!" };
    }

    /// <summary>
    ///     Buys an item from the shop and either adds it to the user's inventory or
    ///     equips it to their selected Pokémon based on the item type.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="itemName">The name of the item to buy.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> BuyItem(ulong userId, string itemName)
    {
        itemName = itemName.Replace(" ", "-").ToLower();

        if (itemName == "daycare-space")
            return new CommandResult { Message = "Use `/buy daycare`, not `/buy item daycare-space`." };

        var item = await mongoDb.Shop.Find(x => x.Item == itemName).FirstOrDefaultAsync();
        if (item == null) return new CommandResult { Message = "That Item is not in the market" };

        var price = (ulong)item.Price;
        await using var db = await dbContextProvider.GetConnectionAsync();

        var data = await (
            from user in db.Users
            where user.UserId == userId
            select new { user.Items, user.MewCoins, user.Selected }
        ).FirstOrDefaultAsync();

        if (data == null) return new CommandResult { Message = "You have not started!\nStart with `/start` first." };

        if (data.MewCoins < price) return new CommandResult { Message = $"You don't have {price} credits!" };

        if (itemName == "market-space")
        {
            if (data.MewCoins < 30000)
                return new CommandResult
                    { Message = $"You need 30,000 credits to buy a market space! You only have {data.MewCoins}..." };

            var marketLimit = await db.Users
                .Where(u => u.UserId == userId)
                .Select(u => u.MarketLimit)
                .FirstOrDefaultAsync();

            if (marketLimit >= MaxMarketSlots)
                return new CommandResult { Message = "You already have the maximum number of market spaces!" };

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.MarketLimit, u => u.MarketLimit + 1)
                .Set(u => u.MewCoins, u => u.MewCoins - 30000)
                .UpdateAsync();

            return new CommandResult { Message = "You have successfully bought an extra market space!" };
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
                    return new CommandResult { Message = "You need to be fishing level 105 to use this item!" };
                case "epic-rod" when fishingLevel < 150:
                    return new CommandResult { Message = "You need to be fishing level 150 to use this item!" };
                case "master-rod" when fishingLevel < 200:
                    return new CommandResult { Message = "You need to be fishing level 200 to use this item!" };
            }

            var items = JsonSerializer.Deserialize<Dictionary<string, int>>(data.Items ?? "{}") ??
                        new Dictionary<string, int>();
            items[itemName] = items.GetValueOrDefault(itemName, 0) + 1;

            var serializedItems = JsonSerializer.Serialize(items);

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.MewCoins, u => u.MewCoins - price)
                .Set(u => u.Items, serializedItems)
                .UpdateAsync();

            return new CommandResult { Message = $"You have successfully bought the {itemName}!" };
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

            return new CommandResult
                { Message = $"You have successfully bought a {itemName}! Use it with `/apply {itemName}`." };
        }

        if (data.Selected == null)
            return new CommandResult
            {
                Message =
                    "You do not have a selected pokemon and the item you are trying to buy requires one!\nUse `/select` to select a pokemon."
            };

        var selectedPokemon = await db.UserPokemon.FirstOrDefaultAsync(p => p.Id == data.Selected);
        if (selectedPokemon == null) return new CommandResult { Message = "Selected pokemon not found!" };

        switch (itemName)
        {
            case "ability-capsule":
            {
                var formInfo = await mongoDb.Forms.Find(f => f.Identifier == selectedPokemon.PokemonName.ToLower())
                    .FirstOrDefaultAsync();

                var abilityIds = await mongoDb.PokeAbilities
                    .Find(a => a.PokemonId == formInfo.PokemonId)
                    .ToListAsync();

                if (abilityIds.Count <= 1)
                    return new CommandResult { Message = "That Pokemon cannot have its ability changed!" };

                var newIndex = (selectedPokemon.AbilityIndex + 1) % abilityIds.Count;
                var newAbilityId = abilityIds[newIndex].AbilityId;

                await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.AbilityIndex, newIndex).UpdateAsync();

                var newAbility = await mongoDb.Abilities.Find(a => a.AbilityId == newAbilityId).FirstOrDefaultAsync();

                await db.Users.Where(u => u.UserId == userId)
                    .Set(u => u.MewCoins, x => x.MewCoins - price).UpdateAsync();

                return new CommandResult
                    { Message = $"You have Successfully changed your Pokémon's ability to {newAbility.Identifier}" };
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

                return new CommandResult
                    { Message = "You have successfully reset the Effort Values (EVs) of your selected Pokemon!" };
        }

        if (IsFormed(selectedPokemon.PokemonName))
            return new CommandResult
                { Message = "You can not buy an item for a Form. Use `/deform` to de-form your Pokemon!" };

        if (selectedPokemon.HeldItem.ToLower() != "none")
            return new CommandResult { Message = "You already have an item equipped!" };

        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.MewCoins, x => x.MewCoins - price).UpdateAsync();

        await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
            .Set(p => p.HeldItem, itemName).UpdateAsync();

        var evolveResult = await pokemonService.TryEvolve(selectedPokemon.Id);

        return new CommandResult
            { Message = $"You have successfully bought the {itemName} for your {selectedPokemon.PokemonName}" };
    }

    /// <summary>
    ///     Buys additional daycare spaces for the user.
    ///     Each space costs 10,000 credits.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="amount">The number of daycare spaces to buy.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> BuyDaycare(ulong userId, int amount)
    {
        if (amount < 0) return new CommandResult { Message = "Yeah... negative numbers won't work here. Try again" };

        var price = (ulong)(10000 * amount);
        await using var db = await dbContextProvider.GetConnectionAsync();
        var balance = await db.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.MewCoins)
            .FirstOrDefaultAsync();

        if (balance == null) return new CommandResult { Message = "You have not started!\nStart with `/start` first." };

        if (price > balance)
            return new CommandResult
            {
                Message =
                    $"You cannot afford that many daycare spaces! You need {price} credits, but you only have {balance}."
            };

        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.MewCoins, u => u.MewCoins - price)
            .Set(u => u.DaycareLimit, u => u.DaycareLimit + amount)
            .UpdateAsync();

        var plural = amount != 1 ? "s" : "";
        return new CommandResult { Message = $"You have successfully bought {amount} daycare space{plural}!" };
    }

    /// <summary>
    ///     Buys vitamins to increase EVs (Effort Values) of the user's selected Pokémon.
    ///     Each vitamin costs 100 credits and adds 10 EVs to a specific stat.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="itemName">The name of the vitamin to buy.</param>
    /// <param name="amount">The amount of the vitamin to buy.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> BuyVitamins(ulong userId, string itemName, int amount)
    {
        amount = Math.Max(0, amount);
        itemName = itemName.Trim();
        var itemInfo = await mongoDb.Shop.Find(x => x.Item == itemName).FirstOrDefaultAsync();
        if (itemInfo == null) return new CommandResult { Message = "That Item is not in the market" };

        await using var db = await dbContextProvider.GetConnectionAsync();
        var totalPrice = (ulong)(amount * 100);
        var selectedId = await db.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.Selected)
            .FirstOrDefaultAsync();

        var selectedPokemon = await db.UserPokemon
            .FirstOrDefaultAsync(p => p.Id == selectedId);

        if (selectedPokemon == null)
            return new CommandResult
                { Message = "You don't have a pokemon selected!\nSelect one with `/select` first." };

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null || user.MewCoins < totalPrice)
            return new CommandResult { Message = $"You do not have {totalPrice} credits!" };

        try
        {
            var evTotal = selectedPokemon.HpEv + selectedPokemon.AttackEv + selectedPokemon.DefenseEv +
                          selectedPokemon.SpecialAttackEv + selectedPokemon.SpecialDefenseEv + selectedPokemon.SpeedEv;

            if (evTotal + amount > 510)
                return new CommandResult { Message = "Your Pokemon has maxed all 510 EVs or 252 EVs for that stat." };

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.MewCoins, x => x.MewCoins - totalPrice).UpdateAsync();

            var updated = itemName switch
            {
                // Validate itemName and set the appropriate property update
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
                ? new CommandResult { Message = "Your Pokemon has maxed all 510 EVs or 252 EVs for that stat." }
                : new CommandResult
                {
                    Message = $"You have successfully bought {amount} {itemName} for your {selectedPokemon.PokemonName}"
                };
        }
        catch
        {
            return new CommandResult { Message = "Your Pokemon has maxed all 510 EVs or 252 EVs for that stat." };
        }
    }

    /// <summary>
    ///     Buys and applies Rare Candy to level up the user's selected Pokémon.
    ///     Each candy costs 100 credits and adds one level, up to a maximum of level 100.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="amount">The number of Rare Candies to buy and use.</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> BuyCandy(ulong userId, int amount)
    {
        await using var db = await dbContextProvider.GetConnectionAsync();
        var selectedId = await db.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.Selected)
            .FirstOrDefaultAsync();

        if (selectedId == null) return new CommandResult { Message = "You need to select a pokemon first!" };

        var ownedPoke = await db.UserPokemonOwnerships.FirstOrDefaultAsync(p => p.Position == (selectedId-1) && p.UserId == userId);
        if (ownedPoke == null) return new CommandResult { Message = "Selected pokemon not found!" };
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
            return new CommandResult
            {
                Message = $"You do not have {price} credits for {buyAmount} Rare {candyStr}",
                Ephemeral = true
            };

        try
        {
            await db.UserPokemon.Where(p => p.Id == ownedPoke.PokemonId)
                .Set(p => p.Level, x => x.Level + useAmount).UpdateAsync();

            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.MewCoins, x => x.MewCoins - price).UpdateAsync();

            var newLevel = selectedPokemon.Level + useAmount;
            var evolveResult = await pokemonService.TryEvolve(selectedId.GetValueOrDefault(), overrideLvl100: true);

            return new CommandResult
            {
                Message = $"Your {selectedPokemon.PokemonName} has successfully leveled up to Level {newLevel}.",
                Ephemeral = true
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in Buy Candy - Poke: {Id} | Expected level - {Level}", selectedId,
                selectedPokemon.Level + useAmount);
            return new CommandResult
            {
                Message = "Sorry, I can't do that right now. Try again in a moment.",
                Ephemeral = true
            };
        }
    }

    /// <summary>
    ///     Buys a treasure chest that can contain rare Pokémon or items.
    ///     Chests can be purchased with either credits or redeems and have weekly purchase limits.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="chestType">The type of chest to buy (rare, mythic, or legend).</param>
    /// <param name="creditsOrRedeems">The currency to use for purchase (credits or redeems).</param>
    /// <returns>A CommandResult containing the operation result message.</returns>
    public async Task<CommandResult> BuyChest(ulong userId, string chestType, string creditsOrRedeems)
    {
        var ct = chestType.ToLower().Trim();
        var cor = creditsOrRedeems.ToLower();

        if (!new[] { "rare", "mythic", "legend" }.Contains(ct))
            return new CommandResult
            {
                Message = $"`{ct}` is not a valid chest type! Choose one of Rare, Mythic, or Legend.",
                Ephemeral = true
            };

        if (!new[] { "credits", "redeems" }.Contains(cor))
            return new CommandResult
            {
                Message = "Specify either \"credits\" or \"redeems\"!",
                Ephemeral = true
            };

        var prices = new Dictionary<string, Dictionary<string, int>>
        {
            ["credits"] = new()
            {
                ["rare"] = 300000,
                ["mythic"] = 600000,
                ["legend"] = 2000000
            },
            ["redeems"] = new()
            {
                ["rare"] = 7,
                ["mythic"] = 10,
                ["legend"] = 33
            }
        };

        var price = (ulong)prices[cor][ct];
        await using var db = await dbContextProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null)
            return new CommandResult
            {
                Message = "You have not started!\nStart with `/start` first.",
                Ephemeral = true
            };

        if (cor == "credits")
        {
            if (user.MewCoins < price)
                return new CommandResult
                {
                    Message = $"You do not have the {price} credits you need to buy a {ct} chest!",
                    Ephemeral = true
                };

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

            const int maxChests = 5;
            var currentAmount = ct switch
            {
                "rare" => chestStore.Rare,
                "mythic" => chestStore.Mythic,
                "legend" => chestStore.Legend,
                _ => throw new ArgumentException("Invalid chest type")
            };

            if (currentAmount + 1 > maxChests)
                return new CommandResult
                {
                    Message =
                        $"You can't buy more than {maxChests} per week using credits! You've already bought {currentAmount}.",
                    Ephemeral = true
                };

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
                return new CommandResult
                {
                    Message = $"You do not have the {price} redeems you need to buy a {ct} chest!",
                    Ephemeral = true
                };

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

        return new CommandResult
        {
            Message =
                $"You have successfully bought a {ct} chest for {price} {cor}!\nYou can open it with `/open {ct}`."
        };
    }

    /// <summary>
    ///     Buys redeems for the user, which can be used to redeem special Pokémon or items.
    ///     Redeems cost 60,000 credits each and have a weekly purchase limit of 100.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="amount">The number of redeems to buy, or null to show current purchase stats.</param>
    /// <returns>A CommandResult containing the operation result message or status information.</returns>
    public async Task<CommandResult> BuyRedeems(ulong userId, ulong? amount = null)
    {
        if (amount is < 1) return new CommandResult { Message = "Nice try..." };

        await using var db = await dbContextProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null)
            return new CommandResult
            {
                Message = "You have not started!\nStart with `/start` first.",
                Ephemeral = true
            };

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
        const int restock_time = 604800;

        var currentWeek = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / restock_time).ToString();
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
                    desc += "Buy more using `/buy redeems <amount>`!";

                embed.WithTitle("Buy redeems")
                    .WithDescription(desc)
                    .WithColor(new Color(255, 182, 193))
                    .WithFooter("Redeems restock every Wednesday at 8pm ET.");
            }
            else
            {
                embed.WithTitle("Buy redeems")
                    .WithDescription("You haven't bought any redeems yet! Use `/buy redeems <amount>`!")
                    .WithColor(new Color(255, 182, 193));
            }

            return new CommandResult { Embed = embed.Build(), Ephemeral = true };
        }

        if (redeemStore.Bought + amount.Value > maxRedeems)
            return new CommandResult
            {
                Message = $"You can't buy more than {maxRedeems} per week! You've already bought {redeemStore.Bought}.",
                Ephemeral = true
            };

        const int creditsPerRedeem = 60000;
        var price = (ulong)(amount.Value * creditsPerRedeem);

        if (user.MewCoins < price)
            return new CommandResult
            {
                Message = $"You do not have the {price} credits to buy those redeems!"
            };

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

        return new CommandResult
        {
            Message = $"You have successfully bought {amount.Value} redeems for {price} credits!"
        };
    }

    /// <summary>
    ///     Gets the list of items that can be actively applied to Pokemon for evolution or effects.
    /// </summary>
    /// <returns>Collection of active item names.</returns>
    public IReadOnlyCollection<string> GetActiveItems()
    {
        return _activeItemList;
    }

    /// <summary>
    ///     Gets the list of berry items that can be used in the game.
    /// </summary>
    /// <returns>Collection of berry item names.</returns>
    public IReadOnlyCollection<string> GetBerryItems()
    {
        return _berryList;
    }

    /// <summary>
    ///     Gets all usable items (active items + berries).
    /// </summary>
    /// <returns>Collection of all usable item names.</returns>
    public IReadOnlyCollection<string> GetUsableItems()
    {
        return _activeItemList.Concat(_berryList).ToList();
    }

    /// <summary>
    ///     Represents the result of a command operation, containing a message, embed, and success status.
    /// </summary>
    public record CommandResult
    {
        /// <summary>
        ///     Gets the message to display to the user.
        /// </summary>
        public string Message { get; init; }

        /// <summary>
        ///     Gets the embed to display to the user, if any.
        /// </summary>
        public EmbedRequest? Embed { get; init; }

        /// <summary>
        ///     Gets a value indicating whether the message should be ephemeral (only visible to the command user).
        /// </summary>
        public bool Ephemeral { get; init; }

        /// <summary>
        ///     Gets a value indicating whether the operation was successful.
        ///     An operation is considered successful if it produced either a message or an embed.
        /// </summary>
        public bool Success => !string.IsNullOrEmpty(Message) || Embed != null;
    }
}