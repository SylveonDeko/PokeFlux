using System.Text.Json;
using EeveeCore.Common.ModuleBehaviors;
using EeveeCore.Modules.Fishing.Common;
using EeveeCore.Modules.Pokemon.Services;
using EeveeCore.Modules.Missions.Services;
using EeveeCore.Services.Impl;
using LinqToDB;
using LinqToDB.Async;
using MongoDB.Driver;
using Serilog;
using StackExchange.Redis;
using User = EeveeCore.Database.Linq.Models.Bot.User;
using Achievement = EeveeCore.Database.Linq.Models.Pokemon.Achievement;

namespace EeveeCore.Modules.Fishing.Services;

/// <summary>
///     Service for handling fishing mechanics and logic.
///     Handles the fishing attempts, user catches, rewards, and Redis-based tracking for the Pokemon fishing system.
/// </summary>
public class FishingService : INService, IReadyExecutor
{
    private readonly IDataCache _cache;
    private readonly FluxerClient _client;
    private readonly LinqToDbConnectionProvider _dbProvider;
    private readonly IMongoService _mongoService;
    private readonly PokemonService _pokemonService;
    private readonly MissionService _missionService;
    private readonly Random _random;
    
    /// <summary>
    ///     Dictionary to track active fishing timers by user ID.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, System.Timers.Timer> _fishingTimers = new();

    /// <summary>
    ///     Initializes a new instance of the FishingService class.
    /// </summary>
    /// <param name="mongoService">The MongoDB service for accessing game data.</param>
    /// <param name="dbProvider">The LinqToDB connection provider for database operations.</param>
    /// <param name="cache">Cache service for Redis operations.</param>
    /// <param name="client">The Fluxer client, for messages and catches.</param>
    /// <param name="pokemonService">Pokemon service for Pokemon-related operations.</param>
    /// <param name="missionService">Mission service for tracking mission progress.</param>
    public FishingService(IMongoService mongoService, LinqToDbConnectionProvider dbProvider, IDataCache cache,
        FluxerClient client, PokemonService pokemonService, MissionService missionService)
    {
        _mongoService = mongoService;
        _dbProvider = dbProvider;
        _cache = cache;
        _client = client;
        _pokemonService = pokemonService;
        _missionService = missionService;
        _random = new Random();
    }

    /// <summary>
    ///     Sets up event handlers when the bot is ready.
    /// </summary>
    public Task OnReadyAsync()
    {
        _client.Gateway.MessageCreated += OnMessageReceived;
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Handles incoming messages to check for fishing catches.
    /// </summary>
    private async Task OnMessageReceived(SocketMessage message)
    {
        if (message.Author is not { IsBot: false } || message.Guild is null)
            return;

        // Check if the message mentions the bot
        var mentions = message.Mentions?.ToList() ?? [];
        if (mentions.All(u => u.Id != _client.Gateway.CurrentUser?.Id))
            return;

        // Remove mentions from content before processing
        var content = message.Content;
        foreach (var mention in mentions)
        {
            content = content.Replace($"<@{mention.Id}>", "").Replace($"<@!{mention.Id}>", "");
        }
        content = content.Trim().ToLower().Replace(" ", "-");
        
        // Check if this user has an active fishing attempt
        var fishingData = await GetUserActiveFishingData(message.Author.Id);
        if (fishingData == null) return;

        // Check if the fishing is in this channel
        if (fishingData.ChannelId != message.Channel.Id) return;

        // Check if the message matches the Pokemon name
        var catchOptions = GetCatchOptions(fishingData.PokemonName);
        if (!catchOptions.Contains(content)) return;

        // Try to mark as caught - this is atomic using Redis
        if (!await TryMarkFishingAsCaught(fishingData.MessageId, message.Author.Id))
            return; // Already caught

        // Process the successful catch
        var result = await HandleSuccessfulCatch(
            message.Author.Id,
            fishingData.GuildId,
            fishingData.PokemonName,
            fishingData.Item,
            fishingData.IsShiny,
            fishingData.ExpGain,
            fishingData.LeveledUp,
            fishingData.NewLevel,
            fishingData.RemainingEnergy);

        // Fire mission event for successful fishing
        if (result.Success)
        {
            await using var db = await _dbProvider.GetConnectionAsync();
            var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == message.Author.Id);
            if (user != null)
            {
                _ = Task.Run(async () => await _missionService.FirePokemonFishedEvent(message, user));
            }
        }

        // Add success reaction
        try
        {
            await message.AddReactionAsync("✅");
        }
        catch
        {
            /* Ignore reaction errors */
        }

        // Update the fishing message
        await UpdateFishingMessage(fishingData.MessageId, message.Channel, result, fishingData);

        // Clean up the fishing data and timer
        await RemoveUserActiveFishingData(message.Author.Id);
        if (_fishingTimers.TryRemove(message.Author.Id, out var timer))
        {
            timer.Dispose();
        }
    }

    /// <summary>
    ///     Checks if a fishing attempt has already been caught using Redis.
    /// </summary>
    /// <param name="messageId">The ID of the fishing message.</param>
    /// <returns>True if the fishing attempt has already been completed, false otherwise.</returns>
    public async Task<bool> IsFishingAlreadyCaught(ulong messageId)
    {
        var key = $"fishing:caught:{messageId}";
        return await _cache.Redis.GetDatabase().KeyExistsAsync(key);
    }

    /// <summary>
    ///     Tries to mark a fishing attempt as caught using Redis.
    ///     Uses NX (Not Exists) flag to ensure atomic operation.
    /// </summary>
    /// <param name="messageId">The ID of the fishing message.</param>
    /// <param name="userId">The ID of the user who caught the Pokemon.</param>
    /// <returns>True if successfully marked (was not previously caught), false otherwise.</returns>
    public async Task<bool> TryMarkFishingAsCaught(ulong messageId, ulong userId)
    {
        var key = $"fishing:caught:{messageId}";

        // Set key only if it doesn't exist, with 15 minute expiry
        var result = await _cache.Redis.GetDatabase().StringSetAsync(
            key,
            userId.ToString(),
            TimeSpan.FromMinutes(15),
            When.NotExists);

        return result;
    }

    /// <summary>
    ///     Handles a fishing attempt for a user.
    /// </summary>
    /// <param name="ctx">The context of the fish command.</param>
    /// <returns>A FishingResult with information about the fishing outcome.</returns>
    public async Task<FishingResult> HandleFishing(CommandContext ctx)
    {
        try
        {
            // Check if this user is already fishing anywhere
            var existingFishing = await GetUserActiveFishingData(ctx.User.Id);
            if (existingFishing != null)
            {
                return new FishingResult(false, "You already have an active fishing attempt! Wait for it to finish or time out.", null);
            }

            await using var context = await _dbProvider.GetConnectionAsync();
            var user = await context.Users.FirstOrDefaultAsync(u => u.UserId == ctx.User.Id);

            if (user == null)
            {
                return new FishingResult(false, "You have not started!\nStart with `/start` first.", null);
            }

            var rod = user.HeldItem;
            if (string.IsNullOrEmpty(rod) || !rod.EndsWith("rod"))
            {
                return new FishingResult(false, "You are not holding a Fishing Rod!\nBuy one in the shop with `/shop rods` first.", null);
            }

            // Check energy
            if ((user.Energy ?? 0) <= 0)
            {
                return new FishingResult(false, "You don't have any energy left!\nEnergy comes back over time.", null);
            }

            // Check rod requirements
            if (!CanUseRod(user.FishingLevel ?? 1, rod))
            {
                return new FishingResult(false, $"You are not high enough level to use the {rod} you have equipped.\nEquip a different rod and try again.", null);
            }

            // Consume energy
            user.Energy = (user.Energy ?? 0) - FishingConstants.EnergyPerFish;

            // Get fishing results
            var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Inventory ?? "{}") ?? new Dictionary<string, int>();
            var (item, pokemon, isShiny) = await DetermineFishingResults(user.FishingLevel ?? 1, inventory);

            // Calculate experience gain
            var expGain = await CalculateExpGain(rod, user.FishingLevel ?? 1);
            var leveledUp = false;
            var newLevel = user.FishingLevel ?? 1;

            // Check for level up
            if ((user.FishingLevelCap ?? 0) < ((user.FishingExp ?? 0) + expGain) && (user.FishingLevel ?? 1) < FishingConstants.MaxLevel)
            {
                newLevel = (user.FishingLevel ?? 1) + 1;
                user.FishingLevel = newLevel;
                user.FishingLevelCap = (ulong)CalculateTotalExp(newLevel);
                user.FishingExp = 0;
                leveledUp = true;
            }
            else
            {
                user.FishingExp = (user.FishingExp ?? 0) + (ulong)expGain;
            }

            await context.GetTable<User>()
                .Where(u => u.UserId == ctx.User.Id)
                .Set(u => u.FishingExp, user.FishingExp)
                .UpdateAsync();

            // Create the fishing embed with scattered Pokemon name
            var scatteredName = ScatterName(pokemon);
            var rodName = rod.Replace("-", " ").ToTitleCase();

            // Calculate rod time bonus
            var (minBonus, maxBonus) = GetRodTimeBonus(rod);
            var rodBonus = _random.Next(minBonus, maxBonus + 1);
            var baseTime = _random.Next(FishingConstants.BaseTimeMin, FishingConstants.BaseTimeMax + 1);
            var totalTime = baseTime + rodBonus;

            var embed = new EmbedBuilder()
                .WithTitle($"You fished up a... ```{scatteredName}```")
                .WithDescription($"You have {totalTime} seconds to guess the Pokemon's name to catch it!\nYour {rodName} gave you {rodBonus} extra seconds!")
                .WithColor(0x00FF00)
                .WithImageUrl("attachment://fishing.gif")
                .Build();

            // Create success result with fishing data
            var result = new FishingResult(true, null, embed)
            {
                Pokemon = pokemon,
                Item = item,
                IsShiny = isShiny,
                ExpGain = expGain,
                LeveledUp = leveledUp,
                NewLevel = newLevel,
                RemainingEnergy = user.Energy ?? 0,
                TimeLimit = totalTime,
                ShowMultiBox = (user.FishingLevel ?? 1) < 100 && _random.Next(0, FishingConstants.MultiBoxChance) == 0,
                CasterId = ctx.User.Id,
                GuildId = ctx.Guild?.Id ?? 0,
                ChannelId = ctx.Channel.Id
            };

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing fishing for user {UserId}", ctx.User.Id);
            return new FishingResult(false, "An error occurred while fishing. Please try again later.", null);
        }
    }


    /// <summary>
    ///     Handles a successful Pokemon catch.
    /// </summary>
    /// <param name="userId">The Discord ID of the user catching the Pokemon.</param>
    /// <param name="guildId">The Discord ID of the guild where the catch occurred.</param>
    /// <param name="pokemonName">The name of the caught Pokemon.</param>
    /// <param name="item">The item the Pokemon was holding.</param>
    /// <param name="isShiny">Whether the Pokemon is shiny.</param>
    /// <param name="expGain">Experience gained from fishing.</param>
    /// <param name="leveledUp">Whether the user leveled up.</param>
    /// <param name="newLevel">The new level if leveled up.</param>
    /// <param name="remainingEnergy">User's remaining energy.</param>
    /// <returns>A CatchResult with information about the catch outcome.</returns>
    private async Task<CatchResult> HandleSuccessfulCatch(ulong userId, ulong guildId, string pokemonName, 
        string item, bool isShiny, double expGain, bool leveledUp, ulong newLevel, int remainingEnergy)
    {
        await using var context = await _dbProvider.GetConnectionAsync();
        
        // Add item to inventory
        var user = await context.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null)
        {
            return new CatchResult(false, "User not found", null, 0);
        }

        if (FishingConstants.ChestItems.Contains(item))
        {
            var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Inventory ?? "{}") ?? new Dictionary<string, int>();
            inventory[item] = inventory.GetValueOrDefault(item, 0) + 1;
            user.Inventory = JsonSerializer.Serialize(inventory);
        }
        else
        {
            var items = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Items ?? "{}") ?? new Dictionary<string, int>();
            items[item] = items.GetValueOrDefault(item, 0) + 1;
            user.Items = JsonSerializer.Serialize(items);
        }

        // Create the Pokemon
        var inventory2 = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Inventory ?? "{}");
        var ivMulti = inventory2?.GetValueOrDefault("iv-multiplier", 0) ?? 0;
        var boosted = _random.Next(500) < ivMulti;
        var level = _random.Next(1, 61);

        var pokemon = await _pokemonService.CreatePokemon(userId, pokemonName, isShiny, boosted, level: level);
        if (pokemon == null)
        {
            return new CatchResult(false, "Failed to create Pokemon", null, 0);
        }

        var ivPercent = Math.Round((pokemon.HpIv + pokemon.AttackIv + pokemon.DefenseIv +
                                   pokemon.SpecialAttackIv + pokemon.SpecialDefenseIv + pokemon.SpeedIv) / 186.0 * 100, 2);

        // Update achievements
        await context.GetTable<Achievement>()
            .Where(a => a.UserId == userId)
            .Set(a => a.FishingSuccess, a => a.FishingSuccess + 1)
            .UpdateAsync();

        var rewardMessages = new List<string>();
        var shinyEmote = isShiny ? Emojis.Shiny : "";

        rewardMessages.Add(
            $"{shinyEmote}Congratulations, you have caught a **{pokemonName}** `({ivPercent}% iv)`!{shinyEmote}");

        if (boosted)
            rewardMessages.Add("It was boosted by your IV multiplier!");

        rewardMessages.Add($"It also dropped a {item.Replace("-", " ").ToTitleCase()}!");
        rewardMessages.Add($"`fishing` (<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>)");

        var embed = new EmbedBuilder()
            .WithDescription(string.Join("\n", rewardMessages))
            .WithColor(new Color(_random.Next(256), _random.Next(256), _random.Next(256)))
            .Build();

        return new CatchResult(true, null, embed, ivPercent);
    }

    /// <summary>
    ///     Handles the multi-box mechanism.
    /// </summary>
    /// <param name="userId">The user ID to handle multi-box for.</param>
    /// <returns>The result message.</returns>
    public async Task<string> HandleMultiBox(ulong userId)
    {
        if (_random.Next(0, FishingConstants.MultiBoxItemChance) != 0)
        {
            return _random.Choice(FishingConstants.FunnyFails);
        }

        await using var context = await _dbProvider.GetConnectionAsync();
        var user = await context.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null) return "You have not started! Start with `/start` first!";

        var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Inventory ?? "{}") ?? new Dictionary<string, int>();
        
        if (inventory.GetValueOrDefault("battle-multiplier", 0) >= FishingConstants.BattleMultiplierCap)
        {
            return "You have hit the cap for battle multiplier!";
        }

        inventory["battle-multiplier"] = Math.Min(inventory.GetValueOrDefault("battle-multiplier", 0) + 1, 
                                                 FishingConstants.BattleMultiplierCap);
        
        await context.GetTable<User>()
            .Where(u => u.UserId == userId)
            .Set(u => u.Inventory, JsonSerializer.Serialize(inventory))
            .UpdateAsync();

        return "You got x1 battle multiplier!";
    }

    #region Helper Methods

    /// <summary>
    ///     Calculates total experience required for a given level.
    /// </summary>
    public static int CalculateTotalExp(ulong level)
    {
        if (level <= 100)
        {
            return (int)Math.Round(FishingConstants.BaseExp * Math.Pow(level, FishingConstants.LevelExponent));
        }
        
        var baseExp = CalculateTotalExp(100);
        return (int)Math.Round(FishingConstants.LevelMultiplierAfter100 * baseExp * level / 
                              FishingConstants.LevelDivisor * FishingConstants.LevelAdjustment) - 
                              FishingConstants.ExpSubtraction;
    }

    /// <summary>
    ///     Checks if a user can use a specific rod based on their level.
    /// </summary>
    public static bool CanUseRod(ulong level, string rod)
    {
        return rod.ToLower() switch
        {
            "supreme-rod" => level >= FishingConstants.SupremeRodMinLevel,
            "epic-rod" => level >= FishingConstants.EpicRodMinLevel,
            "master-rod" => level >= FishingConstants.MasterRodMinLevel,
            _ => true
        };
    }

    /// <summary>
    ///     Scatters a Pokemon name with block characters.
    /// </summary>
    public string ScatterName(string name)
    {
        var result = new List<string>();
        var blockCount = 0;
        var maxBlocks = name.Length / 2;

        foreach (var character in name)
        {
            if (_random.Next(1, FishingConstants.ScatterBlockChance + 1) == 1 && 
                blockCount <= maxBlocks)
            {
                result.Add(FishingConstants.ScatterBlock);
                blockCount++;
            }
            else
            {
                result.Add(character.ToString());
            }
        }

        return string.Join("", result);
    }

    /// <summary>
    ///     Gets the time bonus for a specific rod.
    /// </summary>
    public (int min, int max) GetRodTimeBonus(string rod)
    {
        var rodKey = rod.ToLower().Replace(" ", "-");
        return FishingConstants.RodBonuses.GetValueOrDefault(rodKey, (0, 1));
    }

    /// <summary>
    ///     Determines fishing results based on level and RNG.
    /// </summary>
    public async Task<(string item, string pokemon, bool isShiny)> DetermineFishingResults(ulong level, Dictionary<string, int> inventory)
    {
        // Get shop items by tier
        var cheapItems = await GetItemsByTier("cheap");
        var midItems = await GetItemsByTier("mid");
        var expensiveItems = await GetItemsByTier("expensive");
        var superItems = await GetItemsByTier("super");

        // Calculate chance with level bonus (cap at level 100)
        var levelBonus = Math.Min(Math.Max(level, 0), 300) * 20;
        var chance = _random.NextDouble() * (10000 - levelBonus) + levelBonus;

        // Determine rarity and get appropriate items/pokemon
        string item;
        string[] pokemonPool;

        if (chance < FishingConstants.RarityChances["common"])
        {
            item = cheapItems[_random.Next(cheapItems.Count)];
            pokemonPool = WaterPokemonLists.CommonWater;
        }
        else if (chance < FishingConstants.RarityChances["uncommon"])
        {
            item = cheapItems[_random.Next(cheapItems.Count)];
            pokemonPool = WaterPokemonLists.UncommonWater;
        }
        else if (chance < FishingConstants.RarityChances["rare"])
        {
            item = midItems[_random.Next(midItems.Count)];
            pokemonPool = WaterPokemonLists.RareWater;
        }
        else if (chance < FishingConstants.RarityChances["extremely_rare"])
        {
            item = expensiveItems[_random.Next(expensiveItems.Count)];
            pokemonPool = WaterPokemonLists.ExtremelyRareWater;
        }
        else
        {
            item = superItems[_random.Next(superItems.Count)];
            pokemonPool = WaterPokemonLists.UltraRareWater;
        }

        var pokemon = pokemonPool[_random.Next(pokemonPool.Length)];

        // Check for chest drops
        var chestChance = level > 150 ? FishingConstants.CommonChestChanceOver150 : FishingConstants.CommonChestChanceUnder150;
        var rareChestChance = level > 150 ? FishingConstants.RareChestChanceOver150 : FishingConstants.RareChestChanceUnder150;

        if (_random.Next(0, chestChance + 1) == 0)
        {
            item = "common-chest";
        }
        else if (_random.Next(0, rareChestChance + 1) == 0)
        {
            item = "rare-chest";
        }

        // Ultra rare item chance
        var ultraRareChance = FishingConstants.UltraRareBaseChance;
        var expBonus = Math.Min(level * 1000, FishingConstants.UltraRareExpBonus);
        ultraRareChance -= expBonus;

        if (_random.NextDouble() * 10000 < ultraRareChance)
        {
            item = FishingConstants.UltraRareItems[_random.Next(FishingConstants.UltraRareItems.Length)];
        }

        // Determine if shiny
        var shinyThreshold = FishingConstants.ShinyThreshold;
        var shinyMultiplier = inventory.GetValueOrDefault("shiny-multiplier", 0);
        shinyThreshold = (int)Math.Round(shinyThreshold - shinyThreshold * (shinyMultiplier / 100.0));
        
        var shinyRoll = _random.Next(0, shinyThreshold);
        var isShiny = shinyRoll == 0;

        return (item, pokemon.First().ToString().ToUpper() + pokemon[1..], isShiny);
    }

    /// <summary>
    ///     Gets items from shop by price tier and exclusions.
    /// </summary>
    public async Task<List<string>> GetItemsByTier(string tier)
    {
        var (minPrice, maxPrice) = FishingConstants.ShopTiers[tier];
        
        try
        {
            var shop = await _mongoService.Shop
                .Find(item => item.Price >= minPrice && 
                             item.Price < maxPrice && 
                             item.Item != null)
                .ToListAsync();

            return shop
                .Where(item => item.Item != null && 
                              !IsKeyItem(item.Item) && 
                              !FishingConstants.ExcludedItems.Contains(item.Item))
                .Select(item => item.Item!)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to get items by tier {Tier}, using fallback list", tier);
            
            // Return a fallback list of common items for this tier
            return tier switch
            {
                "cheap" => ["nugget", "pearl", "big-pearl"],
                "mid" => ["comet-shard", "protein", "leftovers"],
                "expensive" => ["choice-scarf", "focus-sash", "destiny-knot"],
                "super" => ["master-ball", "rare-candy", "bottle-cap"],
                _ => ["nugget", "pearl"]
            };
        }
    }

    /// <summary>
    ///     Calculates experience gain from fishing with a specific rod.
    /// </summary>
    public async Task<double> CalculateExpGain(string rod, ulong level)
    {
        var shop = await _mongoService.Shop
            .Find(item => item.Item == rod.ToLower().Replace(" ", "-"))
            .FirstOrDefaultAsync();

        if (shop == null) return 0;

        var baseExpGain = shop.Price / FishingConstants.ExpGainDivisor;
        return baseExpGain + (baseExpGain * level / 2.0);
    }

    /// <summary>
    ///     Determines if an item is a key item.
    /// </summary>
    public static bool IsKeyItem(string item)
    {
        return item.EndsWith("-orb") || item == "coin-case";
    }

    /// <summary>
    ///     Gets the possible name variations for a Pokemon for catching purposes.
    /// </summary>
    public List<string> GetCatchOptions(string pokemonName)
    {
        var options = new List<string> { pokemonName.ToLower() };

        switch (pokemonName.ToLower())
        {
            case "mr-mime":
                options.Add("mr.-mime");
                break;
            case "mime-jr":
                options.Add("mime-jr.");
                break;
            default:
                if (pokemonName.EndsWith("-alola"))
                {
                    var baseName = pokemonName[..^6];
                    options.AddRange([
                        $"alola-{baseName}",
                        $"{baseName}-alolan",
                        $"alolan-{baseName}"
                    ]);
                }
                else if (pokemonName.EndsWith("-galar"))
                {
                    var baseName = pokemonName[..^6];
                    options.AddRange([
                        $"galar-{baseName}",
                        $"{baseName}-galarian",
                        $"galarian-{baseName}"
                    ]);
                }
                break;
        }

        return options.Select(o => o.ToLower().Replace(" ", "-")).ToList();
    }

    /// <summary>
    ///     Gets active fishing data for a user from Redis.
    /// </summary>
    private async Task<FishingData?> GetUserActiveFishingData(ulong userId)
    {
        var key = $"fishing:active:{userId}";
        var data = await _cache.Redis.GetDatabase().StringGetAsync(key);
        return data.HasValue ? JsonSerializer.Deserialize<FishingData>(data!) : null;
    }

    /// <summary>
    ///     Stores active fishing data in Redis.
    /// </summary>
    private async Task SetUserActiveFishingData(ulong userId, FishingData data)
    {
        var key = $"fishing:active:{userId}";
        var json = JsonSerializer.Serialize(data);
        // Set expiration to 10 minutes as a fallback cleanup
        await _cache.Redis.GetDatabase().StringSetAsync(key, json, TimeSpan.FromMinutes(10));
    }

    /// <summary>
    ///     Stores fishing data for event handling.
    /// </summary>
    public async Task StoreFishingData(ulong channelId, ulong messageId, FishingResult result)
    {
        var fishingData = new FishingData
        {
            CasterId = result.CasterId,
            GuildId = result.GuildId,
            ChannelId = result.ChannelId,
            MessageId = messageId,
            PokemonName = result.Pokemon!,
            Item = result.Item!,
            IsShiny = result.IsShiny,
            ExpGain = result.ExpGain,
            LeveledUp = result.LeveledUp,
            NewLevel = result.NewLevel,
            RemainingEnergy = result.RemainingEnergy
        };

        await SetUserActiveFishingData(result.CasterId, fishingData);
        
        // Start timeout timer
        _ = HandleFishingTimeoutAsync(result.CasterId, messageId, result.TimeLimit);
    }

    /// <summary>
    ///     Removes active fishing data from Redis.
    /// </summary>
    private async Task RemoveUserActiveFishingData(ulong userId)
    {
        var key = $"fishing:active:{userId}";
        await _cache.Redis.GetDatabase().KeyDeleteAsync(key);
    }

    /// <summary>
    ///     Handles fishing timeout by cleaning up and updating the message.
    /// </summary>
    private async Task HandleFishingTimeoutAsync(ulong userId, ulong messageId, int timeLimit)
    {
        try
        {
            // Create and start timer
            var timer = new System.Timers.Timer(timeLimit * 1000);
            timer.Elapsed += async (sender, e) =>
            {
                timer.Dispose();
                _fishingTimers.TryRemove(userId, out _);

                // Check if fishing is still active (not already caught)
                var fishingData = await GetUserActiveFishingData(userId);
                if (fishingData == null || fishingData.MessageId != messageId)
                    return; // Already caught or cleaned up

                // Clean up the fishing data
                await RemoveUserActiveFishingData(userId);

                // Update the message to show timeout
                if (await _client.Rest.GetMessageAsync(fishingData.ChannelId, messageId) is { } message)
                {
                    var timeoutEmbed = new EmbedBuilder()
                        .WithTitle("The Pokemon got away!")
                        .WithDescription("The Pokemon slipped away before you could catch it!")
                        .WithColor(Color.Red)
                        .Build();

                    await message.ModifyAsync(embeds: [timeoutEmbed]);
                }
            };

            _fishingTimers[userId] = timer;
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling fishing timeout for user {UserId} message {MessageId}", userId, messageId);
        }
    }

    /// <summary>
    ///     Updates the fishing message after a successful catch.
    /// </summary>
    private async Task UpdateFishingMessage(ulong messageId, Channel channel, CatchResult result, FishingData fishingData)
    {
        try
        {
            if (await _client.Rest.GetMessageAsync(channel.Id, messageId) is not { } message)
                return;

            // Create success embed
            var successEmbed = new EmbedBuilder()
                .WithTitle("Here's what you got from fishing!")
                .WithColor(0xFFB6C1)
                .AddField("You caught a", $"**{fishingData.PokemonName}** `({result.IvPercent:F2}% iv)!`")
                .AddField("It was holding", $"{fishingData.Item.Replace("-", " ").ToTitleCase()} `x1`", false)
                .AddField($"+{fishingData.ExpGain:F1} Fishing EXP", "`Increase your fishing Exp gain by buying a Better Rod!`", false);

            if (fishingData.LeveledUp)
            {
                successEmbed.AddField("You have Leveled Up!", $"Your Fishing Level is now {fishingData.NewLevel}", false);
            }

            var energyPhrase = string.Format(_random.Choice(FishingConstants.EnergyPhrases), fishingData.RemainingEnergy);
            successEmbed.WithFooter(energyPhrase);

            await message.ModifyAsync(embeds: [successEmbed.Build()]);

            // Send additional response embed
            if (result.ResponseEmbed != null)
            {
                await channel.SendMessageAsync(embeds: [result.ResponseEmbed]);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error updating fishing message {MessageId} in channel {ChannelId}", messageId, channel.Id);
        }
    }

    #endregion
}

/// <summary>
///     Data structure for storing active fishing attempts.
/// </summary>
public class FishingData
{
    /// <summary>
    ///     The Discord ID of the user who cast the fishing line.
    /// </summary>
    public ulong CasterId { get; set; }

    /// <summary>
    ///     The Discord ID of the guild where fishing occurred.
    /// </summary>
    public ulong GuildId { get; set; }

    /// <summary>
    ///     The Discord ID of the channel where fishing occurred.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     The Discord ID of the fishing message.
    /// </summary>
    public ulong MessageId { get; set; }

    /// <summary>
    ///     The name of the Pokemon that can be caught.
    /// </summary>
    public string PokemonName { get; set; } = string.Empty;

    /// <summary>
    ///     The item that will be found.
    /// </summary>
    public string Item { get; set; } = string.Empty;

    /// <summary>
    ///     Whether the Pokemon is shiny.
    /// </summary>
    public bool IsShiny { get; set; }

    /// <summary>
    ///     Experience gained from fishing.
    /// </summary>
    public double ExpGain { get; set; }

    /// <summary>
    ///     Whether the user leveled up.
    /// </summary>
    public bool LeveledUp { get; set; }

    /// <summary>
    ///     The new level if leveled up.
    /// </summary>
    public ulong NewLevel { get; set; }

    /// <summary>
    ///     Remaining energy after fishing.
    /// </summary>
    public int RemainingEnergy { get; set; }
}

/// <summary>
///     Result of a fishing attempt.
/// </summary>
public class FishingResult
{
    /// <summary>
    ///     Gets or sets whether the fishing attempt was successful.
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    ///     Gets or sets the error message if unsuccessful.
    /// </summary>
    public string? Message { get; set; }
    
    /// <summary>
    ///     Gets or sets the embed to display.
    /// </summary>
    public EmbedRequest? ResponseEmbed { get; set; }
    
    /// <summary>
    ///     Gets or sets the name of the Pokemon caught.
    /// </summary>
    public string? Pokemon { get; set; }
    
    /// <summary>
    ///     Gets or sets the item found while fishing.
    /// </summary>
    public string? Item { get; set; }
    
    /// <summary>
    ///     Gets or sets whether the Pokemon caught was shiny.
    /// </summary>
    public bool IsShiny { get; set; }
    
    /// <summary>
    ///     Gets or sets the amount of experience gained.
    /// </summary>
    public double ExpGain { get; set; }
    
    /// <summary>
    ///     Gets or sets whether the user leveled up.
    /// </summary>
    public bool LeveledUp { get; set; }
    
    /// <summary>
    ///     Gets or sets the new fishing level if leveled up.
    /// </summary>
    public ulong NewLevel { get; set; }
    
    /// <summary>
    ///     Gets or sets the remaining energy after fishing.
    /// </summary>
    public int RemainingEnergy { get; set; }
    
    /// <summary>
    ///     Gets or sets the time limit for catching the Pokemon.
    /// </summary>
    public int TimeLimit { get; set; }
    
    /// <summary>
    ///     Gets or sets whether to show multi-box event.
    /// </summary>
    public bool ShowMultiBox { get; set; }

    /// <summary>
    ///     Gets or sets the ID of the user who cast the fishing line.
    /// </summary>
    public ulong CasterId { get; set; }

    /// <summary>
    ///     Gets or sets the guild ID where fishing occurred.
    /// </summary>
    public ulong GuildId { get; set; }

    /// <summary>
    ///     Gets or sets the channel ID where fishing occurred.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     Initializes a new instance of the FishingResult class.
    /// </summary>
    /// <param name="success">Whether the fishing attempt was successful.</param>
    /// <param name="message">The error message if unsuccessful.</param>
    /// <param name="responseEmbed">The embed to display.</param>
    public FishingResult(bool success, string? message, EmbedRequest? responseEmbed)
    {
        Success = success;
        Message = message;
        ResponseEmbed = responseEmbed;
    }
}

/// <summary>
///     Result of a Pokemon catch attempt.
/// </summary>
/// <param name="Success">Whether the catch was successful.</param>
/// <param name="Message">A message describing the result, if applicable.</param>
/// <param name="ResponseEmbed">The embed to display in response to the catch.</param>
/// <param name="IvPercent">The IV percentage of the caught Pokemon.</param>
public record CatchResult(
    bool Success,
    string? Message,
    EmbedRequest? ResponseEmbed,
    double IvPercent);

/// <summary>
///     Extension methods for collections.
/// </summary>
public static class Extensions
{
    /// <summary>
    ///     Gets a random choice from a collection.
    /// </summary>
    public static T Choice<T>(this Random random, IList<T> items)
    {
        return items[random.Next(items.Count)];
    }

    /// <summary>
    ///     Converts a string to title case.
    /// </summary>
    public static string ToTitleCase(this string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var words = input.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
            {
                words[i] = char.ToUpper(words[i][0]) + words[i][1..].ToLower();
            }
        }

        return string.Join(" ", words);
    }
}