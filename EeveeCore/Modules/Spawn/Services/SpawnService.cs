using System.Text;
using System.Text.Json;
using EeveeCore.Common.Constants;
using EeveeCore.Common.ModuleBehaviors;
using EeveeCore.Database.Linq.Models.Pokemon;
using EeveeCore.Database.Models.Mongo.Discord;
using EeveeCore.Modules.Pokemon.Services;
using EeveeCore.Modules.Spawn.Constants;
using EeveeCore.Modules.Achievements.Services;
using EeveeCore.Modules.Missions.Services;
using EeveeCore.Services.Impl;
using LinqToDB;
using LinqToDB.Async;
using MongoDB.Driver;
using Serilog;
using StackExchange.Redis;
using User = EeveeCore.Database.Linq.Models.Bot.User;
using ActiveSpawn = EeveeCore.Database.Linq.Models.Game.ActiveSpawn;

namespace EeveeCore.Modules.Spawn.Services;

/// <summary>
///     Provides functionality for spawning Pokemon in Discord channels.
///     Handles the spawn mechanics, user catches, rewards, and guild configuration for the Pokemon spawn system.
/// </summary> 
public class SpawnService : INService, IReadyExecutor
{
    // Constants from Python code
    /// <summary>
    ///     Base chance for a Pokemon to spawn when a message is sent.
    /// </summary>
    private const double BASE_SPAWN_CHANCE = 0.03;

    /// <summary>
    ///     Base cooldown time in seconds between Pokemon spawns in a guild.
    /// </summary>
    private const int BASE_COOLDOWN = 20;

    /// <summary>
    ///     List of guild IDs that are excluded from spawns.
    /// </summary>
    private static readonly ulong[] EXCLUDED_GUILDS = [264445053596991498, 446425626988249089];

    /// <summary>
    ///     Set of channel IDs where vault events are currently active.
    /// </summary>
    private readonly HashSet<ulong> _activeVaults = [];

    /// <summary>
    ///     Cache service for temporary data storage.
    /// </summary>
    private readonly IDataCache _cache;

    /// <summary>
    ///     The Discord client for interaction with Discord.
    /// </summary>
    private readonly DiscordShardedClient _client;

    /// <summary>
    ///     Provider for database contexts.
    /// </summary>
    private readonly LinqToDbConnectionProvider _dbContextProvider;

    /// <summary>
    ///     MongoDB service for data storage.
    /// </summary>
    private readonly IMongoService _mongoDb;

    /// <summary>
    ///     Service for Pokemon-related functionality.
    /// </summary>
    private readonly PokemonService _pokemonService;

    /// <summary>
    ///     Service for tracking achievements and milestones.
    /// </summary>
    private readonly AchievementService _achievementService;

    /// <summary>
    ///     Service for handling mission-related operations.
    /// </summary>
    private readonly MissionService _missionService;

    /// <summary>
    ///     Random number generator for spawn mechanics.
    /// </summary>
    private readonly Random _random;

    /// <summary>
    ///     Cache of spawn cooldowns for guilds.
    ///     Maps guild IDs to the time of their last spawn.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, DateTime> _spawnCache = new(Environment.ProcessorCount, 1000);

    /// <summary>
    ///     Flag to enable spawns regardless of spawn chance.
    ///     Used for testing or special events.
    /// </summary>
    private bool _alwaysSpawn;


    /// <summary>
    ///     Initializes a new instance of the SpawnService class.
    ///     Sets up event handlers and required services.
    /// </summary>
    /// <param name="client">The Discord client for interactions.</param>
    /// <param name="mongoDb">MongoDB service for data access.</param>
    /// <param name="handler">Event handler for Discord events.</param>
    /// <param name="pokemonService">Pokemon service for Pokemon-related operations.</param>
    /// <param name="dbContextProvider">Provider for database access.</param>
    /// <param name="cache">Cache service for temporary data.</param>
    /// <param name="achievementService">Service for tracking achievements and milestones.</param>
    /// <param name="missionService">Service for handling mission-related operations.</param>
    public SpawnService(
        DiscordShardedClient client,
        IMongoService mongoDb,
        EventHandler handler,
        PokemonService pokemonService,
        LinqToDbConnectionProvider dbContextProvider,
        IDataCache cache,
        AchievementService achievementService,
        MissionService missionService)
    {
        _client = client;
        _mongoDb = mongoDb;
        _pokemonService = pokemonService;
        _dbContextProvider = dbContextProvider;
        _cache = cache;
        _achievementService = achievementService;
        _missionService = missionService;
        _random = new Random();

        handler.MessageReceived += HandleMessageAsync;
    }

    /// <summary>
    ///     Checks if a Pokémon spawn message has already been caught using Redis.
    /// </summary>
    /// <param name="messageId">The ID of the spawn message.</param>
    /// <returns>True if the Pokémon has already been caught, false otherwise.</returns>
    public async Task<bool> IsPokemonAlreadyCaught(ulong messageId)
    {
        var key = $"pokemon:caught:{messageId}";
        return await _cache.Redis.GetDatabase().KeyExistsAsync(key);
    }

    /// <summary>
    ///     Tries to mark a Pokémon spawn message as caught using Redis.
    ///     Uses NX (Not Exists) flag to ensure atomic operation.
    /// </summary>
    /// <param name="messageId">The ID of the spawn message.</param>
    /// <param name="userId">The ID of the user who caught the Pokémon.</param>
    /// <returns>True if successfully marked (was not previously caught), false otherwise.</returns>
    public async Task<bool> TryMarkPokemonAsCaught(ulong messageId, ulong userId)
    {
        var key = $"pokemon:caught:{messageId}";

        // Set key only if it doesn't exist, with 15 minute expiry
        // This ensures that even if the code never cleans up, the key will expire
        var result = await _cache.Redis.GetDatabase().StringSetAsync(
            key,
            userId.ToString(),
            TimeSpan.FromMinutes(15),
            When.NotExists);

        return result;
    }

    /// <summary>
    ///     Handles the catching of a Pokémon by a user, with Redis-based message ID validation.
    ///     Processes reward distribution and creates the Pokémon in the user's collection.
    /// </summary>
    /// <param name="userId">The Discord ID of the user catching the Pokémon.</param>
    /// <param name="guildId">The Discord ID of the guild where the catch occurred.</param>
    /// <param name="pokemonName">The name of the caught Pokémon.</param>
    /// <param name="isShiny">Whether the Pokémon is shiny.</param>
    /// <param name="legendChance">The legendary spawn chance value.</param>
    /// <param name="ubChance">The Ultra Beast spawn chance value.</param>
    /// <param name="messageId">The ID of the spawn message.</param>
    /// <returns>A CatchResult with information about the catch outcome.</returns>
    public async Task<CatchResult> HandleCatchWithMessageCheck(
        ulong userId,
        ulong guildId,
        string? pokemonName,
        bool isShiny,
        int legendChance,
        int ubChance,
        ulong messageId)
    {
        // First check if this Pokémon message has already been caught
        if (await IsPokemonAlreadyCaught(messageId))
            return new CatchResult(false, "This Pokémon has already been caught by someone else!", null, false, false);

        // Try to mark the Pokémon as caught - if this fails, someone else caught it since our check
        if (!await TryMarkPokemonAsCaught(messageId, userId))
            return new CatchResult(false, "This Pokémon has already been caught by someone else!", null, false, false);

        // Now proceed with the normal catch logic
        return await HandleCatch(userId, guildId, pokemonName, isShiny, legendChance, ubChance);
    }

    /// <summary>
    ///     Handles incoming Discord messages to determine if a Pokemon should spawn.
    ///     Checks against cooldowns, spawn chances, and guild configurations.
    /// </summary>
    /// <param name="msg">The received message.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task HandleMessageAsync(SocketMessage msg)
    {
        if (msg.Author.IsBot || msg.Channel is not ITextChannel channel)
            return;
        
        if (msg is not IUserMessage message)
            return;

        var guildId = channel.Guild.Id;
        if (EXCLUDED_GUILDS.Contains(guildId))
            return;

        try
        {
            // Check for mention-based catching first
            if (await HandleMentionCatch(message, channel))
                return;
            var guildConfig = await GetGuildConfig(guildId);
            if (guildConfig == null) return;

            var now = DateTime.UtcNow;
            var cooldown = Math.Max(1, BASE_COOLDOWN - guildConfig.Speed);

            if (_spawnCache.TryGetValue(guildId, out var lastSpawn) && now < lastSpawn.AddSeconds(cooldown))
                return;

            var spawnChance = BASE_SPAWN_CHANCE * (guildConfig.Speed / 10.0);
            if (_random.NextDouble() >= spawnChance && !_alwaysSpawn)
                return;

            _spawnCache.AddOrUpdate(guildId, now, (_, _) => now);

            if (message.Channel is IVoiceChannel)
                return;

            if (!await ValidateChannel(channel, guildConfig))
                return;

            var spawnChannel = await GetSpawnChannel(channel, guildConfig);
            if (spawnChannel == null) return;

            await using var db = await _dbContextProvider.GetConnectionAsync();
            var (shiny, honey) = await GetSpawnModifiers(message.Author.Id, channel.Id, db);
            var (overrideWithGhost, overrideWithIce) = ProcessHoneyEffect(honey);

            var spawnChances = CalculateSpawnChances(honey);
            var pokemon = await SelectPokemon(spawnChances, overrideWithGhost, overrideWithIce, guildId);

            // Handle Christmas Event (if active)
            if (IsChristmasEvent() && _random.NextDouble() < 0.09 && !_activeVaults.Contains(channel.Id))
            {
                await HandleVaultSpawn(spawnChannel, pokemon);
                return;
            }

            await CreateAndSendSpawnMessage(spawnChannel, pokemon, shiny, guildConfig);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing spawn in guild {GuildId}", guildId);
        }
    }

    /// <summary>
    ///     Retrieves or creates a guild configuration.
    /// </summary>
    /// <param name="guildId">The Discord ID of the guild.</param>
    /// <returns>The guild configuration document.</returns>
    private async Task<Guild> GetGuildConfig(ulong guildId)
    {
        var guildConfig = await _mongoDb.Guilds
            .Find(g => g.GuildId == guildId)
            .FirstOrDefaultAsync();

        if (guildConfig != null) return guildConfig;

        guildConfig = new Guild
        {
            GuildId = guildId,
            Speed = 10,
            EnableSpawnsAll = false
        };
        await _mongoDb.Guilds.InsertOneAsync(guildConfig);
        return guildConfig;
    }

    /// <summary>
    ///     Gets spawn modifiers based on user and channel.
    ///     Determines shiny chance and applies honey effects.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="channelId">The Discord ID of the channel.</param>
    /// <param name="db">The database connection for queries.</param>
    /// <returns>A tuple containing shiny status and honey effect.</returns>
    private async Task<(bool IsShiny, Honey? Honey)> GetSpawnModifiers(ulong userId, ulong channelId,
        DittoDataConnection db)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null) return (false, null);

        var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Inventory ?? "{}");
        var threshold = 4000;
        if (inventory != null)
            threshold = (int)(threshold - threshold * (inventory.GetValueOrDefault("shiny-multiplier", 0) / 100.0));

        var isShiny = _random.Next(threshold) == 0;
        var honey = await db.Honey.FirstOrDefaultAsync(h => h.ChannelId == channelId);

        return (isShiny, honey);
    }

    /// <summary>
    ///     Processes honey effects to determine spawn overrides.
    ///     Different honey types can influence the type of Pokemon that spawns.
    /// </summary>
    /// <param name="honey">The honey object with type information.</param>
    /// <returns>A tuple indicating ghost and ice type overrides.</returns>
    private (bool Ghost, bool Ice) ProcessHoneyEffect(Honey? honey)
    {
        if (honey == null) return (false, false);

        return honey.Type switch
        {
            "ghost" => (_random.Next(4) == 0, false),
            "cheer" => (false, true),
            _ => (false, false)
        };
    }

    /// <summary>
    ///     Calculates spawn chances for different Pokemon rarities.
    ///     Honey effects can influence the chance of rare spawns.
    /// </summary>
    /// <param name="honey">The honey effect, if any.</param>
    /// <returns>A tuple containing spawn chances for different Pokemon types.</returns>
    private (int Legend, int Ub, int Pseudo, int Starter) CalculateSpawnChances(Honey? honey)
    {
        var honeyValue = honey?.Type is "ghost" or "cheer" ? 0.0 : honey?.Type == null ? 0.0 : 50.0;

        var legendBase = 4000 - 7600 * honeyValue / 100.0;
        var ubBase = 3000 - 5700 * honeyValue / 100.0;
        var pseudoBase = 1000 - 1900 * honeyValue / 100.0;
        var starterBase = 750 - 950 * honeyValue / 100.0;

        return (
            (int)(_random.NextDouble() * Math.Round(legendBase)),
            (int)(_random.NextDouble() * Math.Round(ubBase)),
            (int)(_random.NextDouble() * Math.Round(pseudoBase)),
            (int)(_random.NextDouble() * Math.Round(starterBase))
        );
    }

    /// <summary>
    ///     Selects a Pokemon to spawn based on calculated spawn chances.
    ///     Takes into account special overrides for ghost, ice, or specific guilds.
    /// </summary>
    /// <param name="chances">The spawn chances for different Pokemon rarities.</param>
    /// <param name="ghost">Whether to override with a ghost-type Pokemon.</param>
    /// <param name="ice">Whether to override with an ice-type Pokemon.</param>
    /// <param name="guildId">The Discord ID of the guild where the spawn occurs.</param>
    /// <returns>The name of the selected Pokemon.</returns>
    private async Task<string?> SelectPokemon((int Legend, int Ub, int Pseudo, int Starter) chances, bool ghost,
        bool ice, ulong guildId)
    {
        if (ghost)
            return await GetRandomPokemonOfType(8);
        if (ice)
            return await GetRandomPokemonOfType(15);
        if (guildId == 999953429751414784)
            return "EeveeCore";

        // Low numbers = rare spawns in Python
        var pokemon = chances.Legend < 2 ? GetRandomFromList(PokemonList.LegendList) : // This matches Python
            chances.Ub < 2 ? GetRandomFromList(PokemonList.ubList) : // This matches Python
            chances.Pseudo < 2 ? GetRandomFromList(PokemonList.pseudoList) : // This matches Python
            chances.Starter < 2 ? GetRandomFromList(PokemonList.starterList) : // This matches Python
            GetRandomFromList(PokemonList.pList); // Fallback to normal list like Python

        return pokemon.ToLower();
    }

    /// <summary>
    ///     Handles mention-based Pokemon catching.
    ///     Checks if a user mentioned the bot with a Pokemon name to catch a spawned Pokemon.
    /// </summary>
    /// <param name="message">The Discord message to check.</param>
    /// <param name="channel">The channel where the message was sent.</param>
    /// <returns>True if the message was a catch attempt, false otherwise.</returns>
    private async Task<bool> HandleMentionCatch(IUserMessage message, ITextChannel channel)
    {
        // Check if the message mentions the bot
        if (message.MentionedUserIds.All(u => u != _client.CurrentUser.Id))
            return false;

        // Extract Pokemon name from the message (everything after the mention)
        var content = message.Content;
        var mentionPattern = $"<@{_client.CurrentUser.Id}>";
        var mentionIndex = content.IndexOf(mentionPattern, StringComparison.OrdinalIgnoreCase);
        
        if (mentionIndex == -1)
        {
            // Try alternative mention format
            mentionPattern = $"<@!{_client.CurrentUser.Id}>";
            mentionIndex = content.IndexOf(mentionPattern, StringComparison.OrdinalIgnoreCase);
        }

        if (mentionIndex == -1)
            return false;

        // Extract the potential Pokemon name after the mention
        var afterMention = content[(mentionIndex + mentionPattern.Length)..].Trim();
        if (string.IsNullOrEmpty(afterMention))
            return false;

        // Get active spawns in this channel from the new tracking system
        var activeSpawnIds = await GetActiveSpawnsInChannel(channel.Id);
        
        foreach (var spawnMessageId in activeSpawnIds)
        {
            // Get spawn info from tracking system
            var spawnInfo = await GetActiveSpawn(spawnMessageId);
            if (spawnInfo == null)
                continue;

            var pokemonName = spawnInfo.Value.PokemonName;
            var isShiny = spawnInfo.Value.IsShiny;
            var legChance = spawnInfo.Value.LegendaryChance;
            var ubChance = spawnInfo.Value.UltraBeastChance;

            // Check if the mentioned Pokemon name matches
            var validNames = GetCatchOptions(pokemonName);
            var guessedName = afterMention.ToLower().Replace(" ", "-");
            
            if (!validNames.Contains(guessedName))
                continue;

            // Try to get the actual message to modify it later
            IUserMessage? spawnMessage = null;
            try
            {
                spawnMessage = await channel.GetMessageAsync(spawnMessageId) as IUserMessage;
            }
            catch
            {
                // Message might have been deleted, continue with the catch anyway
            }

            // Attempt to catch the Pokemon using the same logic as modal catching
            var result = await HandleCatchWithMessageCheck(
                message.Author.Id,
                channel.Guild.Id,
                pokemonName,
                isShiny,
                legChance,
                ubChance,
                spawnMessageId);

            // If successful, also mark in the new spawn tracking system
            if (result.Success)
            {
                await MarkSpawnAsCaught(spawnMessageId, message.Author.Id);
            }

            // Send the catch result
            if (result.Success)
            {
                if (result.ResponseEmbed != null)
                {
                    await message.Channel.SendMessageAsync(embed: result.ResponseEmbed);
                }
                else if (!string.IsNullOrEmpty(result.Message))
                {
                    await message.Channel.SendMessageAsync(result.Message);
                }
                
                // Add reaction to the original catch message
                try
                {
                    await message.AddReactionAsync(new Emoji("✅"));
                }
                catch
                {
                    // Ignore reaction errors
                }

                // Handle the spawn message if we found it
                if (spawnMessage != null)
                {
                    if (result.ShouldDeleteSpawn)
                    {
                        await spawnMessage.DeleteAsync();
                    }
                    else
                    {
                        var originalEmbed = spawnMessage.Embeds.First().ToEmbedBuilder();
                        originalEmbed.Title = "Caught!";
                        await spawnMessage.ModifyAsync(m =>
                        {
                            m.Embed = originalEmbed.Build();
                            m.Components = new ComponentBuilder().Build();
                            m.Attachments = null;
                        });

                        if (result.ShouldPinSpawn)
                        {
                            var curUser = await channel.Guild.GetCurrentUserAsync();
                            var perms = curUser.GetPermissions(channel);
                            if (perms.ManageMessages)
                                await spawnMessage.PinAsync();
                        }
                    }
                }
            }
            else
            {
                // Send error message as reply
                if (result.ResponseEmbed != null)
                {
                    await message.ReplyAsync(embed: result.ResponseEmbed);
                }
                else if (!string.IsNullOrEmpty(result.Message))
                {
                    await message.ReplyAsync(result.Message);
                }
                else
                {
                    await message.ReplyAsync("Something went wrong with the catch attempt.");
                }
            }

            return true; // We processed a catch attempt
        }

        return false; // No matching spawn found
    }

    /// <summary>
    ///     Handles the spawning of a vault event.
    ///     Vaults are special events that contain rewards and use an encoded message.
    /// </summary>
    /// <param name="channel">The text channel where the vault spawns.</param>
    /// <param name="pokemonName">The name of the Pokemon to encode in the vault message.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task HandleVaultSpawn(ITextChannel channel, string? pokemonName)
    {
        if (_activeVaults.Contains(channel.Id))
            return;

        var shiftAmount = new[] { -6, -5, -4, -3, -2, -1, 1, 2, 3, 4, 5, 6 };
        var letterShift = shiftAmount[_random.Next(shiftAmount.Length)];
        var shiftInverted = -letterShift;
        var giftWord = EncodePhrase(pokemonName, letterShift);

        var embed = new EmbedBuilder()
            .WithTitle("🔒 A locked EeveeCore vault has been spotted!")
            .WithDescription(
                $"`Decoding key:` ||{shiftInverted}||!\n# {giftWord}\n**Reply to the bots message with the pokemon name.**\nShift each letter up or down by the decoding key!\nBe the first decode and say the name to unlock the vault and see what its hiding!\n\n||`A B C D E F G H I J K L M N O P Q R S T U V W X Y Z`||")
            .WithColor(Color.Red);

        // Check if vault image exists locally
        var vaultImagePath = Path.Combine("data", "images", "EeveeCore_vault.png");
        IUserMessage message;
        FileStream vaultStream = null;
        
        try
        {
            if (File.Exists(vaultImagePath))
            {
                embed.WithImageUrl("attachment://vault.png");
                vaultStream = new FileStream(vaultImagePath, FileMode.Open, FileAccess.Read);
                var fileAttachment = new FileAttachment(vaultStream, "vault.png");
                message = await channel.SendFileAsync(fileAttachment, embed: embed.Build());
            }
            else
            {
                message = await channel.SendMessageAsync(embed: embed.Build());
            }
        }
        finally
        {
            // Dispose the stream after sending
            vaultStream?.Dispose();
        }
        _activeVaults.Add(channel.Id);

        try
        {
            var response = await channel.GetMessageAsync(message.Id);
            if (response != null)
            {
                var vaultReward = await HandleVaultReward(response, channel);
                if (vaultReward != null)
                {
                    var rewardEmbed = new EmbedBuilder()
                        .WithDescription(vaultReward)
                        .Build();
                    await message.ModifyAsync(x => x.Embed = rewardEmbed);
                }
            }
        }
        finally
        {
            _activeVaults.Remove(channel.Id);
        }
    }

    /// <summary>
    ///     Handles the reward process for a vault event.
    ///     Determines the type of reward and processes it for the user.
    /// </summary>
    /// <param name="message">The message containing the user's response.</param>
    /// <param name="channel">The channel where the vault event occurred.</param>
    /// <returns>A string describing the reward.</returns>
    private async Task<string> HandleVaultReward(IMessage message, ITextChannel channel)
    {
        await using var db = await _dbContextProvider.GetConnectionAsync();
        var rewardType = _random.NextDouble() switch
        {
            < 0.2 => "shadow",
            < 0.4 => "chest",
            < 0.75 => "gift",
            _ => "redeem"
        };

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == message.Author.Id);
        if (user == null) return null;

        switch (rewardType)
        {
            case "shadow":
                return await HandleShadowReward(user, db);
            case "chest":
                return await HandleChestReward(user, db);
            case "gift":
                return await HandleGiftReward(user, db);
            case "redeem":
                return await HandleRedeemReward(user, db);
            default:
                return "An error occurred with the vault reward.";
        }
    }

    /// <summary>
    ///     Encodes a phrase by shifting letters by a specified amount.
    ///     Used for the vault event to create a puzzle for users to solve.
    /// </summary>
    /// <param name="phrase">The phrase to encode.</param>
    /// <param name="shift">The amount to shift each letter.</param>
    /// <returns>The encoded phrase.</returns>
    private string EncodePhrase(string? phrase, int shift)
    {
        var encoded = new StringBuilder();
        foreach (var c in phrase)
        {
            if (!char.IsLetter(c))
            {
                encoded.Append(c);
                continue;
            }

            var shifted = (char)(c + shift);
            if (char.IsLower(c))
                switch (shifted)
                {
                    case > 'z':
                        shifted -= (char)26;
                        break;
                    case < 'a':
                        shifted += (char)26;
                        break;
                }
            else
                switch (shifted)
                {
                    case > 'Z':
                        shifted -= (char)26;
                        break;
                    case < 'A':
                        shifted += (char)26;
                        break;
                }

            encoded.Append(shifted);
        }

        return encoded.ToString();
    }

    /// <summary>
    ///     Handles the shadow reward type from a vault.
    ///     Either increases the user's shadow hunt chain or rewards a shadow Pokemon.
    /// </summary>
    /// <param name="user">The user receiving the reward.</param>
    /// <param name="db">The database connection.</param>
    /// <returns>A string describing the reward.</returns>
    private async Task<string> HandleShadowReward(User user, DittoDataConnection db)
    {
        if (string.IsNullOrEmpty(user.Hunt))
            return "You don't have a shadow hunt set! Here's some credits instead.";

        var shadowChainUp = _random.Next(20) + 1;
        var isShadow = await ShadowHuntCheck(user.UserId.GetValueOrDefault(), user.Hunt);

        if (isShadow)
        {
            await CreatePokemon(user.UserId.GetValueOrDefault(), user.Hunt, skin: "shadow", boosted: true,
                level: 100);
            return $"Shadows pour from the vault and circle around you! You got a shadow {user.Hunt}!";
        }

        await db.Users
            .Where(u => u.UserId == user.UserId)
            .Set(u => u.Chain, u => u.Chain + shadowChainUp)
            .UpdateAsync();
        return $"Shadows pour from the vault and circle around you! Your chain has increased by {shadowChainUp}!";
    }

    /// <summary>
    ///     Handles the chest reward type from a vault.
    ///     Adds a chest item to the user's inventory.
    /// </summary>
    /// <param name="user">The user receiving the reward.</param>
    /// <param name="db">The database connection.</param>
    /// <returns>A string describing the reward.</returns>
    private async Task<string> HandleChestReward(User user, DittoDataConnection db)
    {
        var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Inventory ?? "{}") ??
                        new Dictionary<string, int>();
        var chance = _random.Next(60);

        string chestType;
        if (chance <= 6)
        {
            chestType = "rare chest";
            inventory["rare chest"] = inventory.GetValueOrDefault("rare chest", 0) + 1;
        }
        else if (chance == 0)
        {
            chestType = "mythic chest";
            inventory["mythic chest"] = inventory.GetValueOrDefault("mythic chest", 0) + 1;
        }
        else
        {
            chestType = "common chest";
            inventory["common chest"] = inventory.GetValueOrDefault("common chest", 0) + 1;
        }

        await db.Users
            .Where(u => u.UserId == user.UserId)
            .Set(u => u.Inventory, JsonSerializer.Serialize(inventory))
            .UpdateAsync();
        return $"> The vault contained a **{chestType}**!";
    }

    /// <summary>
    ///     Handles the gift reward type from a vault.
    ///     Creates and returns a gift item.
    /// </summary>
    /// <param name="user">The user receiving the reward.</param>
    /// <param name="db">The database connection.</param>
    /// <returns>A string describing the reward.</returns>
    private async Task<string> HandleGiftReward(User user, DittoDataConnection db)
    {
        // Implement gift creation logic
        return
            "Its a winter wrapped gift! Oh, it even has a EeveeCore on it... Imagine that!\n\nGifts can be kept and opened or gifted to others as a `Mystery Gift`\nSee `/explain event`";
    }

    /// <summary>
    ///     Handles the redeem reward type from a vault.
    ///     Adds redeems to the user's account.
    /// </summary>
    /// <param name="user">The user receiving the reward.</param>
    /// <param name="db">The database connection.</param>
    /// <returns>A string describing the reward.</returns>
    private async Task<string> HandleRedeemReward(User user, DittoDataConnection db)
    {
        var redeems = _random.Next(1, 3) + 1;
        await db.Users
            .Where(u => u.UserId == user.UserId)
            .Set(u => u.Redeems, u => u.Redeems + (ulong?)redeems)
            .UpdateAsync();
        return $"The vault contained {redeems} Redeems! Yay!";
    }

    /// <summary>
    ///     Checks if the Christmas event is currently active.
    ///     The event affects spawn rates and enables vault spawns.
    /// </summary>
    /// <returns>True if the Christmas event is active, false otherwise.</returns>
    private bool IsChristmasEvent()
    {
        var now = DateTime.UtcNow;
        var start = new DateTime(2023, 12, 25);
        var end = new DateTime(2024, 1, 10);
        return now >= start && now <= end;
    }

    /// <summary>
    ///     Gets the appropriate channel for spawning a Pokemon.
    ///     Handles redirect channels if configured in the guild settings.
    /// </summary>
    /// <param name="channel">The original channel where the spawn was triggered.</param>
    /// <param name="config">The guild configuration.</param>
    /// <returns>The text channel where the Pokemon should spawn.</returns>
    private async Task<ITextChannel?> GetSpawnChannel(ITextChannel channel, Guild config)
    {
        if (config.Redirects?.Any() != true) return channel as ITextChannel;
        var redirectId = config.Redirects[_random.Next(config.Redirects.Count)];
        if (await channel.Guild.GetChannelAsync(redirectId) is not ITextChannel redirectChannel)
            return null;

        var user = await channel.Guild.GetUserAsync(_client.CurrentUser.Id);
        var perms = user.GetPermissions(redirectChannel);
        if (!perms.SendMessages || !perms.EmbedLinks)
            return null;

        return redirectChannel;
    }

    /// <summary>
    ///     Validates whether Pokemon can spawn in the specified channel.
    ///     Checks enabled/disabled channel configurations in the guild settings.
    /// </summary>
    /// <param name="channel">The channel to check.</param>
    /// <param name="config">The guild configuration.</param>
    /// <returns>True if spawns are allowed in the channel, false otherwise.</returns>
    private static async Task<bool> ValidateChannel(ITextChannel channel, Guild config)
    {
        if (!config.EnableSpawnsAll && !config.EnabledChannels.Contains(channel.Id))
            return false;

        return !config.DisabledSpawnChannels.Contains(channel.Id);
    }

    /// <summary>
    ///     Rounds a double value to the nearest integer.
    /// </summary>
    /// <param name="value">The value to round.</param>
    /// <returns>The rounded integer value.</returns>
    private int Round(double value)
    {
        return (int)Math.Round(value);
    }

    /// <summary>
    ///     Gets a random item from a list.
    /// </summary>
    /// <param name="list">The list to select from.</param>
    /// <returns>A randomly selected item from the list.</returns>
    private string? GetRandomFromList(IReadOnlyList<string?> list)
    {
        return list[_random.Next(list.Count)];
    }

    /// <summary>
    ///     Checks if a shadow Pokemon should be spawned for a user's hunt.
    ///     Calculates the chance based on the user's current chain value.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="pokemon">The Pokemon the user is hunting.</param>
    /// <returns>True if a shadow Pokemon should spawn, false otherwise.</returns>
    private async Task<bool> ShadowHuntCheck(ulong userId, string? pokemon)
    {
        await using var dbContext = await _dbContextProvider.GetConnectionAsync();
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.UserId == userId);

        if (user?.Hunt != pokemon.Capitalize())
            return false;

        var makeShadow = _random.NextDouble() < 1.0 / 6000 * Math.Pow(4, user.Chain / 1000.0);

        if (makeShadow)
            await dbContext.Users
                .Where(u => u.UserId == userId)
                .Set(u => u.Chain, 0)
                .UpdateAsync();
        else
            await dbContext.Users
                .Where(u => u.UserId == userId)
                .Set(u => u.Chain, u => u.Chain + 1)
                .UpdateAsync();
        return makeShadow;
    }

    /// <summary>
    ///     Creates and sends a spawn message for a Pokemon.
    ///     Handles different display modes and interaction methods based on guild configuration.
    /// </summary>
    /// <param name="channel">The channel to send the spawn message to.</param>
    /// <param name="pokemonName">The name of the spawned Pokemon.</param>
    /// <param name="isShiny">Whether the Pokemon is shiny.</param>
    /// <param name="config">The guild configuration.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task CreateAndSendSpawnMessage(ITextChannel channel, string? pokemonName, bool isShiny, Guild config)
    {
        // Get form info from MongoDB for validation
        var formInfo = await _mongoDb.Forms
            .Find(f => f.Identifier == pokemonName.ToLower())
            .FirstOrDefaultAsync();

        if (formInfo == null)
        {
            Log.Error("Invalid pokemon name {Pokemon} in spawn", pokemonName);
            return;
        }

        // Get the image path
        var (_, imagePath) = await _pokemonService.GetPokemonFormInfo(pokemonName, isShiny);
        if (string.IsNullOrEmpty(imagePath)) return;

        var shinyEmote = isShiny ? "<a:shiny:1057764628349853786>" : "";
        var spawnMessage = SpawnMessages.Messages[_random.Next(SpawnMessages.Messages.Count)];

        // Calculate spawn chances ONCE
        var legChance = _random.Next(4000);
        var ubChance = _random.Next(3000);

        var embed = new EmbedBuilder()
            .WithTitle(spawnMessage)
            .WithDescription($"{shinyEmote}This Pokémon's name starts with {pokemonName[0]}{shinyEmote}")
            .WithColor(new Color(_random.Next(256), _random.Next(256), _random.Next(256)))
            .WithFooter("/explain spawns for basic info");

        // Check if the image file exists
        if (!File.Exists(imagePath))
        {
            Log.Warning("Pokemon image not found at path: {ImagePath}", imagePath);
            embed.WithDescription($"{embed.Description}\n\n*[Image not available]*");
        }
        else
        {
            // Set image attachment filename - use generic name to avoid revealing Pokemon identity
            var imageFileName = "spawn.png";
            if (config.SmallImages)
                embed.WithThumbnailUrl($"attachment://{imageFileName}");
            else
                embed.WithImageUrl($"attachment://{imageFileName}");
        }

        // Create spawn message with or without button
        IUserMessage spawnMsg;
        FileStream fileStream = null;
        
        try
        {
            if (File.Exists(imagePath))
            {
                fileStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
                // Use generic filename to not reveal Pokemon identity
                var imageFileName = "spawn.png";
                var fileAttachment = new FileAttachment(fileStream, imageFileName);

                if (config.ModalView)
                {
                    var button = new ButtonBuilder()
                        .WithLabel("Catch This Pokemon!")
                        .WithCustomId($"catch:{pokemonName},{isShiny},{legChance},{ubChance}")
                        .WithStyle(ButtonStyle.Primary);

                    var component = new ComponentBuilder().WithButton(button);
                    spawnMsg = await channel.SendFileAsync(fileAttachment, embed: embed.Build(), components: component.Build());
                }
                else
                {
                    spawnMsg = await channel.SendFileAsync(fileAttachment, embed: embed.Build());
                    _ = HandleMessageCollector(channel, spawnMsg, pokemonName, isShiny, config, legChance,
                        ubChance); // Pass the chances here too
                }
            }
            else
            {
                if (config.ModalView)
                {
                    var button = new ButtonBuilder()
                        .WithLabel("Catch This Pokemon!")
                        .WithCustomId($"catch:{pokemonName},{isShiny},{legChance},{ubChance}")
                        .WithStyle(ButtonStyle.Primary);

                    var component = new ComponentBuilder().WithButton(button);
                    spawnMsg = await channel.SendMessageAsync(embed: embed.Build(), components: component.Build());
                }
                else
                {
                    spawnMsg = await channel.SendMessageAsync(embed: embed.Build());
                    _ = HandleMessageCollector(channel, spawnMsg, pokemonName, isShiny, config, legChance,
                        ubChance); // Pass the chances here too
                }
            }
            
            // Register the active spawn for tracking
            _ = RegisterActiveSpawn(spawnMsg.Id, channel.Id, channel.Guild.Id, pokemonName, isShiny, legChance, ubChance);
        }
        finally
        {
            // Dispose the stream after sending
            fileStream?.Dispose();
        }
    }

    /// <summary>
    ///     Handles message collection for Pokemon catching.
    ///     Monitors chat for messages containing the Pokemon's name.
    ///     Uses Redis to prevent multiple catches of the same Pokemon.
    /// </summary>
    /// <param name="channel">The channel to monitor.</param>
    /// <param name="spawnMsg">The spawn message.</param>
    /// <param name="pokemonName">The name of the spawned Pokemon.</param>
    /// <param name="isShiny">Whether the Pokemon is shiny.</param>
    /// <param name="config">The guild configuration.</param>
    /// <param name="legChance">The legendary spawn chance value.</param>
    /// <param name="ubChance">The Ultra Beast spawn chance value.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task HandleMessageCollector(ITextChannel channel, IUserMessage spawnMsg, string? pokemonName,
        bool isShiny, Guild config, int legChance, int ubChance)
    {
        var catchOptions = GetCatchOptions(pokemonName);
        var hasCaught = false;

        for (var i = 0; i < 12; i++) // 12 * 50 = 600 seconds (10 minutes)
            try
            {
                var collected = await channel.GetMessagesAsync(50).FlattenAsync();
                foreach (var message in collected)
                {
                    if (hasCaught) break;
                    if (message.Author.IsBot) continue;

                    var content = message.Content.ToLower().Replace(" ", "-");
                    if (!catchOptions.Contains(content)) continue;

                    // Get spawn info from new tracking system
                    var spawnInfo = await GetActiveSpawn(spawnMsg.Id);
                    if (spawnInfo == null)
                        // Spawn no longer active (already caught or expired)
                        break;

                    // Try to mark as caught - this is atomic using the old Redis system for backward compatibility
                    if (!await TryMarkPokemonAsCaught(spawnMsg.Id, message.Author.Id))
                        // Another thread marked it as caught before we could
                        break;

                    // Mark spawn as caught in the new system
                    await MarkSpawnAsCaught(spawnMsg.Id, message.Author.Id);

                    // Process catch using info from spawn tracking
                    var result = await HandleCatch(
                        message.Author.Id,
                        channel.Guild.Id,
                        spawnInfo.Value.PokemonName,
                        spawnInfo.Value.IsShiny,
                        spawnInfo.Value.LegendaryChance,
                        spawnInfo.Value.UltraBeastChance);

                    if (!result.Success) continue;

                    hasCaught = true;

                    try
                    {
                        await message.AddReactionAsync(new Emoji("✅"));
                    }
                    catch
                    {
                        /* Ignore reaction errors */
                    }

                    if (result.ShouldDeleteSpawn)
                    {
                        await spawnMsg.DeleteAsync();
                    }
                    else
                    {
                        var originalEmbed = spawnMsg.Embeds.First().ToEmbedBuilder();
                        originalEmbed.Title = "Caught!";
                        await spawnMsg.ModifyAsync(m =>
                        {
                            m.Embed = originalEmbed.Build();
                            m.Components = new ComponentBuilder().Build();
                            m.Attachments = null;
                        });

                        if (result.ShouldPinSpawn)
                        {
                            var curUser = await channel.Guild.GetCurrentUserAsync();
                            var perms = curUser.GetPermissions(channel);
                            if (perms.ManageMessages)
                                await spawnMsg.PinAsync();
                        }
                    }

                    if (result.ResponseEmbed != null)
                    {
                        await channel.SendMessageAsync(embed: result.ResponseEmbed);
                    }
                    else if (!string.IsNullOrEmpty(result.Message))
                    {
                        await channel.SendMessageAsync(result.Message);
                    }
                }

                if (hasCaught) break;
                await Task.Delay(50000); // Wait 50 seconds before next collection
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in message collector for spawn");
            }

        // Timeout if no one caught it
        if (!hasCaught)
            try
            {
                // Make sure no one caught it while we were deciding to despawn
                if (!await IsPokemonAlreadyCaught(spawnMsg.Id))
                {
                    var timeoutEmbed = spawnMsg.Embeds.First().ToEmbedBuilder();
                    timeoutEmbed.Title = "Despawned!";
                    await spawnMsg.ModifyAsync(m =>
                    {
                        m.Embed = timeoutEmbed.Build();
                        m.Components = new ComponentBuilder().Build();
                    });
                }
            }
            catch
            {
                /* Ignore timeout message errors */
            }
    }

    /// <summary>
    ///     Gets the possible name variations for a Pokemon for catching purposes.
    ///     Includes regional form variations and formatting alternatives.
    /// </summary>
    /// <param name="pokemonName">The name of the Pokemon.</param>
    /// <returns>A list of valid name variations for catching the Pokemon.</returns>
    public List<string> GetCatchOptions(string? pokemonName)
    {
        var options = new List<string?> { pokemonName };

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
                else if (pokemonName.EndsWith("-hisui"))
                {
                    var baseName = pokemonName[..^6];
                    options.AddRange([
                        $"hisui-{baseName}",
                        $"{baseName}-hisuian",
                        $"hisuian-{baseName}"
                    ]);
                }
                else if (pokemonName.EndsWith("-paldea"))
                {
                    var baseName = pokemonName[..^7];
                    options.AddRange([
                        $"paldea-{baseName}",
                        $"{baseName}-paldean",
                        $"paldean-{baseName}"
                    ]);
                }

                break;
        }

        return options.Select(o => o.ToLower()).ToList();
    }

    /// <summary>
    ///     Toggles the always spawn flag.
    ///     Used for testing or special events to force Pokemon spawns.
    /// </summary>
    /// <returns>The new state of the always spawn flag.</returns>
    public bool ToggleAlwaysSpawn()
    {
        _alwaysSpawn = !_alwaysSpawn;
        return _alwaysSpawn;
    }

    /// <summary>
    ///     Gets a random Pokemon of a specific type.
    ///     Used for spawning Pokemon affected by honey or special events.
    /// </summary>
    /// <param name="typeId">The type ID to filter Pokemon by.</param>
    /// <returns>The name of a randomly selected Pokemon of the specified type.</returns>
    private async Task<string?> GetRandomPokemonOfType(int typeId)
    {
        var pokemonOfType = await _mongoDb.PokemonTypes
            .Find(p => p.Types.Contains(typeId))
            .ToListAsync();

        var pokemonIds = pokemonOfType.Select(p => p.PokemonId).ToList();

        var forms = await _mongoDb.Forms
            .Find(f => pokemonIds.Contains(f.PokemonId))
            .ToListAsync();

        var validForms = forms
            .Select(f => f.Identifier.ToTitleCase())
            .Where(name => PokemonConstants.TotalList.Contains(name))
            .ToList();

        return validForms.Any() ? validForms[_random.Next(validForms.Count)] : null;
    }

    /// <summary>
    ///     Handles the catching of a Pokemon by a user.
    ///     Processes reward distribution and creates the Pokemon in the user's collection.
    /// </summary>
    /// <param name="userId">The Discord ID of the user catching the Pokemon.</param>
    /// <param name="guildId">The Discord ID of the guild where the catch occurred.</param>
    /// <param name="pokemonName">The name of the caught Pokemon.</param>
    /// <param name="isShiny">Whether the Pokemon is shiny.</param>
    /// <param name="legendChance">The legendary spawn chance value.</param>
    /// <param name="ubChance">The Ultra Beast spawn chance value.</param>
    /// <returns>A CatchResult with information about the catch outcome.</returns>
    public async Task<CatchResult> HandleCatch(
        ulong userId,
        ulong guildId,
        string? pokemonName,
        bool isShiny,
        int legendChance,
        int ubChance)
    {
        await using var dbContext = await _dbContextProvider.GetConnectionAsync();
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        var conf = await _mongoDb.Guilds.Find(x => x.GuildId == guildId).FirstOrDefaultAsync();

        if (user == null)
            return new CatchResult(false, "You have not started!\nStart with `/start` first!", null,
                conf?.DeleteSpawns ?? false,
                conf?.PinSpawns ?? false);

        var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Inventory ?? "{}");
        var ivMulti = inventory?.GetValueOrDefault("iv-multiplier", 0) ?? 0;
        var boosted = _random.Next(500) < ivMulti;
        var level = _random.Next(1, 61);

        var pokemon = await CreatePokemon(userId, pokemonName, isShiny, boosted, level: level);
        if (pokemon == null)
            return new CatchResult(false, "Failed to create Pokemon", null, conf?.DeleteSpawns ?? false,
                conf?.PinSpawns ?? false);

        var ivPercent = Math.Round((pokemon.HpIv + pokemon.AttackIv + pokemon.DefenseIv +
                                    pokemon.SpecialAttackIv + pokemon.SpecialDefenseIv + pokemon.SpeedIv) / 186.0 * 100,
            2);

        var rewardMessages = new List<string>();
        var shinyEmote = isShiny ? "<a:shiny:1057764628349853786>" : "";

        rewardMessages.Add(
            $"{shinyEmote}Congratulations, you have caught a **{pokemonName}** `({ivPercent}% iv)`!{shinyEmote}");

        if (boosted)
            rewardMessages.Add("It was boosted by your IV multiplier!");

        // Handle berry drops
        var berryResult = await HandleBerryDrop(user);
        if (berryResult.Message != null)
        {
            rewardMessages.Add(berryResult.Message);
            await dbContext.Users
                .Where(u => u.UserId == userId)
                .Set(u => u.Items, JsonSerializer.Serialize(berryResult.Items))
                .UpdateAsync();
        }

        // Handle chest drops
        if (_random.Next(150) == 0)
        {
            inventory ??= new Dictionary<string, int>();
            inventory["common chest"] = inventory.GetValueOrDefault("common chest", 0) + 1;
            await dbContext.Users
                .Where(u => u.UserId == userId)
                .Set(u => u.Inventory, JsonSerializer.Serialize(inventory))
                .UpdateAsync();
            rewardMessages.Add("It also dropped a Common Chest!");
        }

        // Handle premium server credits
        var isPremium = await _cache.Redis.GetDatabase().KeyExistsAsync($"premium_guild:{guildId}");
        if (isPremium)
        {
            var credits = (ulong)_random.NextInt64(100, 251);
            await dbContext.Users
                .Where(u => u.UserId == userId)
                .Set(u => u.MewCoins, u => u.MewCoins + credits)
                .UpdateAsync();
            rewardMessages.Add($"[BONUS] You also found {credits} credits!");
        }

        rewardMessages.Add($"`about` (<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>)");

        var embed = new EmbedBuilder()
            .WithDescription(string.Join("\n", rewardMessages))
            .WithColor(new Color(_random.Next(256), _random.Next(256), _random.Next(256)))
            .Build();

        return new CatchResult(
            true,
            null,
            embed,
            conf?.DeleteSpawns ?? false,
            (conf?.PinSpawns ?? false) && (legendChance < 2 || ubChance < 2));
    }

    /// <summary>
    ///     Handles berry drop rewards when catching a Pokemon.
    ///     Determines if a berry should drop and which type.
    /// </summary>
    /// <param name="user">The user who caught the Pokemon.</param>
    /// <returns>A tuple containing the message about the berry drop and the updated items dictionary.</returns>
    private async Task<(string Message, Dictionary<string, int> Items)> HandleBerryDrop(User user)
    {
        var berryChance = _random.Next(1, 101);
        var expensiveChance = _random.Next(1, 26);

        if (berryChance >= 8)
            return (null, null);

        Dictionary<string?, int> items = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Items ?? "{}") ??
                                         new Dictionary<string?, int>();

        string? berry;
        if (berryChance == 1)
        {
            var cheapItems = await GetCachedCheapShopItemsAsync();
            berry = cheapItems.Count > 0 ? cheapItems[_random.Next(cheapItems.Count)] : null;
        }
        else if (berryChance == expensiveChance)
        {
            var expensiveItems = await GetCachedExpensiveShopItemsAsync();
            berry = expensiveItems.Count > 0 ? expensiveItems[_random.Next(expensiveItems.Count)] : null;
        }
        else
        {
            var berryList = await GetCachedBerryItemsAsync();
            berry = berryList.Count > 0 ? berryList[_random.Next(berryList.Count)] : null;
        }

        if (berry == null)
            return (null, null);

        items[berry] = items.GetValueOrDefault(berry, 0) + 1;
        return ($"It also dropped a {berry}!", items);
    }

    /// <summary>
    ///     Gets debug information about spawns for a guild.
    ///     Used for admin commands to troubleshoot spawn issues.
    /// </summary>
    /// <param name="guild">The guild to get spawn information for.</param>
    /// <returns>A string containing spawn debug information.</returns>
    public async Task<string> GetSpawnDebugInfo(IGuild guild)
    {
        var debugMessages = new List<string>();

        if (_spawnCache.TryGetValue(guild.Id, out var lastSpawn))
            debugMessages.Add($"Spawn cooldown active until: {lastSpawn}");

        var config = await _mongoDb.Guilds
            .Find(g => g.GuildId == guild.Id).FirstOrDefaultAsync();

        if (config == null)
        {
            debugMessages.Add("No guild configuration found.");
            return string.Join("\n", debugMessages);
        }

        debugMessages.AddRange([
            $"Speed: {config.Speed}",
            $"Delete spawns: {config.DeleteSpawns}",
            $"Pin spawns: {config.PinSpawns}",
            $"Small images: {config.SmallImages}",
            $"Modal view: {config.ModalView}",
            $"Enable spawns all: {config.EnableSpawnsAll}",
            $"Enabled channels: {string.Join(", ", config.EnabledChannels?.Select(c => c) ?? [])}",
            $"Disabled spawn channels: {string.Join(", ", config.DisabledSpawnChannels?.Select(c => c) ?? [])}",
            $"Redirects: {string.Join(", ", config.Redirects?.Select(c => c) ?? [])}"
        ]);

        return string.Join("\n", debugMessages);
    }

    /// <summary>
    ///     Creates a new Pokemon and adds it to a user's collection.
    ///     Handles generation of IVs, nature, ability, and other attributes.
    /// </summary>
    /// <param name="userId">The Discord ID of the user who will own the Pokemon.</param>
    /// <param name="pokemonName">The name of the Pokemon to create.</param>
    /// <param name="shiny">Whether the Pokemon should be shiny.</param>
    /// <param name="boosted">Whether the Pokemon's IVs should be boosted.</param>
    /// <param name="radiant">Whether the Pokemon should be radiant.</param>
    /// <param name="skin">The skin to apply to the Pokemon, if any.</param>
    /// <param name="gender">The gender of the Pokemon, or null for random.</param>
    /// <param name="level">The level of the Pokemon.</param>
    /// <returns>The created Pokemon object.</returns>
    public async Task<Database.Linq.Models.Pokemon.Pokemon?> CreatePokemon(
        ulong userId,
        string? pokemonName,
        bool shiny = false,
        bool boosted = false,
        bool radiant = false,
        string skin = null,
        string gender = null,
        int level = 1)
    {
        var pokemonNameLower = pokemonName.ToLower();
        
        // Get form info using cached lookup
        var formInfo = await GetCachedFormInfoAsync(pokemonNameLower);
        if (formInfo == null) return null;

        // Get pokemon info and abilities using cached data in parallel
        var pokemonInfoTask = GetCachedPokemonInfoAsync(formInfo.Value.PokemonId, pokemonNameLower);
        var abilityTask = GetCachedAbilityIdsAsync(formInfo.Value.PokemonId);
        var natureTask = GetRandomNatureAsync();

        await Task.WhenAll(pokemonInfoTask, abilityTask, natureTask);
        var pokemonInfo = await pokemonInfoTask;
        var abilityIds = await abilityTask;
        var selectedNature = await natureTask;

        if (pokemonInfo == null) return null;

        // Determine base stats
        var minIv = boosted ? 12 : 1;
        var maxIv = boosted || _random.Next(2) == 0 ? 31 : 29;

        // Generate IVs
        var hpIv = _random.Next(minIv, maxIv + 1);
        var atkIv = _random.Next(minIv, maxIv + 1);
        var defIv = _random.Next(minIv, maxIv + 1);
        var spaIv = _random.Next(minIv, maxIv + 1);
        var spdIv = _random.Next(minIv, maxIv + 1);
        var speIv = _random.Next(minIv, maxIv + 1);

        // Determine gender if not provided (optimized)
        if (string.IsNullOrEmpty(gender))
        {
            if (pokemonNameLower.Contains("nidoran-"))
                gender = pokemonNameLower.EndsWith("f") ? "-f" : "-m";
            else
                gender = pokemonNameLower switch
                {
                    "illumise" => "-f",
                    "volbeat" => "-m", 
                    _ => pokemonInfo.Value.GenderRate == -1 ? "-x" 
                        : _random.Next(8) < pokemonInfo.Value.GenderRate ? "-f" : "-m"
                };
        }

        // Check for shadow override if no skin is specified
        if (string.IsNullOrEmpty(skin) && !radiant && !shiny)
        {
            var makeShadow = await ShadowHuntCheck(userId, pokemonName);
            if (makeShadow)
            {
                skin = "shadow";
                // Log shadow creation
                if (_client.GetChannel(1005737655025291334) is IMessageChannel channel)
                    await channel.SendMessageAsync($"`{userId} - {pokemonName}`");
            }
        }

        // Create the Pokemon
        var pokemon = new Database.Linq.Models.Pokemon.Pokemon{
            PokemonName = pokemonName.Capitalize(),
            Nickname = "None",
            Gender = gender,
            HpIv = hpIv,
            AttackIv = atkIv,
            DefenseIv = defIv,
            SpecialAttackIv = spaIv,
            SpecialDefenseIv = spdIv,
            SpeedIv = speIv,
            HpEv = 0,
            AttackEv = 0,
            DefenseEv = 0,
            SpecialAttackEv = 0,
            SpecialDefenseEv = 0,
            SpeedEv = 0,
            Level = level,
            Moves = ["tackle", "tackle", "tackle", "tackle"],
            HeldItem = "None",
            Experience = 1,
            Nature = selectedNature,
            ExperienceCap = level * level,
            Price = 0,
            MarketEnlist = false,
            Favorite = false,
            AbilityIndex = abilityIds.Any() ? _random.Next(abilityIds.Count) : 0,
            CaughtBy = userId,
            Radiant = radiant,
            Shiny = shiny,
            Skin = skin,
            Owner = userId,
            Tags = [],
            Tradable = true,
            Breedable = true,
            Temporary = false,
            Happiness = pokemonInfo.Value.BaseHappiness,
            Timestamp = DateTime.UtcNow
        };

        await using var dbContext = await _dbContextProvider.GetConnectionAsync();

        try
        {
            // Insert Pokemon first and get its ID
            pokemon.Id = (ulong)await dbContext.InsertWithInt64IdentityAsync(pokemon);

            // Retry logic for handling concurrent position conflicts
            UserPokemonOwnership? ownership = null;
            var maxRetries = 5;
            
            for (var attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var highestPosition = await dbContext.UserPokemonOwnerships
                        .Where(o => o.UserId == userId)
                        .OrderByDescending(o => o.Position)
                        .Select(o => (int?)o.Position)
                        .FirstOrDefaultAsync() ?? -1;

                    ownership = new UserPokemonOwnership
                    {
                        UserId = userId,
                        PokemonId = pokemon.Id,
                        Position = (ulong)(highestPosition + 1)
                    };

                    await dbContext.InsertAsync(ownership);
                    break; // Success
                }
                catch (Exception ex) when (ex.Message.Contains("duplicate key") && attempt < maxRetries - 1)
                {
                    // Small random delay to reduce collision probability
                    await Task.Delay(_random.Next(10, 50));
                }
            }

            // Update achievements (keep this separate to avoid connection conflicts)
            if (shiny)
                await dbContext.Achievements
                    .Where(a => a.UserId == userId)
                    .Set(a => a.ShinyCaught, a => a.ShinyCaught + 1)
                    .UpdateAsync();
            else
                await dbContext.Achievements
                    .Where(a => a.UserId == userId)
                    .Set(a => a.PokemonCaught, a => a.PokemonCaught + 1)
                    .UpdateAsync();

            // Comprehensive achievement tracking with type-specific milestones
            try
            {
                // Get Pokemon type information for type-specific achievements
                var pokemonTypes = await _mongoDb.PokemonTypes
                    .Find(pt => pt.PokemonId == Convert.ToInt32(pokemon.Id))
                    .FirstOrDefaultAsync();

                var typeIds = pokemonTypes?.Types ?? [];

                // Track comprehensive achievements including milestones and rewards
                await _achievementService.TrackPokemonCaughtAsync(userId, typeIds, shiny, false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error tracking comprehensive achievements for Pokemon catch");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating Pokemon {Name} for user {UserId}", pokemonName, userId);
            return null;
        }

        return pokemon;
    }

    /// <summary>
    ///     Updates guild spawn settings.
    ///     Changes configuration options like delete spawns, pin spawns, small images, modal view, or enable all.
    /// </summary>
    /// <param name="guildId">The Discord ID of the guild.</param>
    /// <param name="setting">The setting to update.</param>
    /// <param name="value">The new value for the setting.</param>
    /// <returns>An embed with information about the update.</returns>
    public async Task<Embed> UpdateGuildSetting(ulong guildId, string setting, string? value)
    {
        var update = setting.ToLower() switch
        {
            "delete" or "deletespawns" => Builders<Guild>.Update
                .Set(g => g.DeleteSpawns, value?.ToLower() == "true"),

            "pin" or "pinspawns" => Builders<Guild>.Update
                .Set(g => g.PinSpawns, value?.ToLower() == "true"),

            "small" or "smallimages" => Builders<Guild>.Update
                .Set(g => g.SmallImages, value?.ToLower() == "true"),

            "modal" or "modalview" => Builders<Guild>.Update
                .Set(g => g.ModalView, value?.ToLower() == "true"),

            "enableall" or "enable_all" => Builders<Guild>.Update
                .Set(g => g.EnableSpawnsAll, value?.ToLower() == "true"),

            _ => null
        };

        if (update == null)
            return new EmbedBuilder()
                .WithColor(Color.Red)
                .WithDescription("Invalid setting. Available settings:\n" +
                                 "- delete (true/false)\n" +
                                 "- pin (true/false)\n" +
                                 "- small (true/false)\n" +
                                 "- modal (true/false)\n" +
                                 "- enableall (true/false)")
                .Build();

        await _mongoDb.Guilds.UpdateOneAsync(
            g => g.GuildId == guildId,
            update,
            new UpdateOptions { IsUpsert = true });

        return new EmbedBuilder()
            .WithColor(Color.Green)
            .WithDescription($"Successfully updated {setting} to {value}")
            .Build();
    }

    /// <summary>
    ///     Updates channel spawn settings for a guild.
    ///     Enables or disables spawns in specific channels.
    /// </summary>
    /// <param name="guildId">The Discord ID of the guild.</param>
    /// <param name="channelId">The Discord ID of the channel to update.</param>
    /// <param name="enable">Whether to enable or disable spawns in the channel.</param>
    /// <returns>An embed with information about the update.</returns>
    public async Task<Embed> UpdateChannelSetting(ulong guildId, ulong channelId, bool enable)
    {
        // First check if the document exists
        var guild = await _mongoDb.Guilds.Find(g => g.GuildId == guildId).FirstOrDefaultAsync();

        if (guild == null)
        {
            // Create new document with initialized arrays
            guild = new Guild
            {
                GuildId = guildId,
                EnabledChannels = [],
                DisabledSpawnChannels = []
            };

            // Add the channel to the appropriate list
            if (enable)
                guild.EnabledChannels.Add(channelId);
            else
                guild.DisabledSpawnChannels.Add(channelId);

            await _mongoDb.Guilds.InsertOneAsync(guild);
        }
        else
        {
            // Initialize arrays if they are null
            if (guild.EnabledChannels == null)
                guild.EnabledChannels = [];

            if (guild.DisabledSpawnChannels == null)
                guild.DisabledSpawnChannels = [];

            // Add the channel to the appropriate list if not already present
            if (enable && !guild.EnabledChannels.Contains(channelId))
            {
                guild.EnabledChannels.Add(channelId);
                await _mongoDb.Guilds.ReplaceOneAsync(g => g.GuildId == guildId, guild);
            }
            else if (!enable && !guild.DisabledSpawnChannels.Contains(channelId))
            {
                guild.DisabledSpawnChannels.Add(channelId);
                await _mongoDb.Guilds.ReplaceOneAsync(g => g.GuildId == guildId, guild);
            }
        }

        return new EmbedBuilder()
            .WithColor(Color.Green)
            .WithDescription(enable
                ? $"Enabled spawns in <#{channelId}>"
                : $"Disabled spawns in <#{channelId}>")
            .Build();
    }

    /// <summary>
    ///     Updates redirect channels for a guild.
    ///     Adds or removes channels where Pokemon spawns are redirected to.
    /// </summary>
    /// <param name="guildId">The Discord ID of the guild.</param>
    /// <param name="channelId">The Discord ID of the channel to update.</param>
    /// <param name="add">Whether to add or remove the channel as a redirect.</param>
    /// <returns>An embed with information about the update.</returns>
    public async Task<Embed> UpdateRedirectChannel(ulong guildId, ulong channelId, bool add)
    {
        // Ensure redirects field is initialized as an array if null
        await _mongoDb.Guilds.UpdateOneAsync(
            g => g.GuildId == guildId && g.Redirects == null,
            Builders<Guild>.Update.Set(g => g.Redirects, new List<ulong>()),
            new UpdateOptions { IsUpsert = false });

        var update = add
            ? Builders<Guild>.Update.AddToSet(g => g.Redirects, channelId)
            : Builders<Guild>.Update.Pull(g => g.Redirects, channelId);

        await _mongoDb.Guilds.UpdateOneAsync(
            g => g.GuildId == guildId,
            update,
            new UpdateOptions { IsUpsert = true });

        return new EmbedBuilder()
            .WithColor(Color.Green)
            .WithDescription(add
                ? $"Successfully added <#{channelId}> to redirect channels."
                : $"Successfully removed <#{channelId}> from redirect channels.")
            .Build();
    }

    /// <summary>
    ///     Updates the spawn speed for a guild.
    ///     Higher values increase spawn frequency.
    /// </summary>
    /// <param name="guildId">The Discord ID of the guild.</param>
    /// <param name="speed">The new spawn speed (1-20).</param>
    /// <returns>An embed with information about the update.</returns>
    public async Task<Embed> UpdateSpawnSpeed(ulong guildId, int speed)
    {
        speed = Math.Max(1, Math.Min(20, speed));

        await _mongoDb.Guilds.UpdateOneAsync(
            g => g.GuildId == guildId,
            Builders<Guild>.Update.Set(g => g.Speed, speed),
            new UpdateOptions { IsUpsert = true });

        var description = speed switch
        {
            >= 15 => "⚡ Very fast spawns activated!",
            >= 10 => "🏃 Normal spawn speed set.",
            _ => "🐌 Slower spawns activated."
        };

        return new EmbedBuilder()
            .WithColor(Color.Green)
            .WithTitle("Spawn Speed Updated")
            .WithDescription($"{description}\nSpeed set to: {speed}")
            .Build();
    }

    /// <summary>
    ///     Gets the current spawn settings for a guild.
    ///     Displays all configured options and channel settings.
    /// </summary>
    /// <param name="guildId">The Discord ID of the guild.</param>
    /// <returns>An embed containing the guild's spawn settings.</returns>
    public async Task<Embed> GetGuildSettings(ulong guildId)
    {
        var guild = await _mongoDb.Guilds
            .Find(g => g.GuildId == guildId)
            .FirstOrDefaultAsync();

        if (guild == null)
        {
            guild = new Guild
            {
                GuildId = guildId,
                Speed = 10,
                EnableSpawnsAll = false
            };
            await _mongoDb.Guilds.InsertOneAsync(guild);
        }

        var embed = new EmbedBuilder()
            .WithTitle("Spawn Settings")
            .WithColor(Color.Blue)
            .AddField("Speed", guild.Speed, true)
            .AddField("Delete Spawns", guild.DeleteSpawns ? "Yes" : "No", true)
            .AddField("Pin Spawns", guild.PinSpawns ? "Yes" : "No", true)
            .AddField("Small Images", guild.SmallImages ? "Yes" : "No", true)
            .AddField("Modal View", guild.ModalView ? "Yes" : "No", true)
            .AddField("Enable All Channels", guild.EnableSpawnsAll ? "Yes" : "No", true);

        if (guild.EnabledChannels?.Any() == true)
        {
            var channelList = string.Join("\n", guild.EnabledChannels.Select(c => $"<#{c}>"));
            embed.AddField("Enabled Channels", channelList.Length > 1024 ? "Too many to display" : channelList);
        }

        if (guild.DisabledSpawnChannels?.Any() == true)
        {
            var channelList = string.Join("\n", guild.DisabledSpawnChannels.Select(c => $"<#{c}>"));
            embed.AddField("Disabled Channels", channelList.Length > 1024 ? "Too many to display" : channelList);
        }

        if (guild.Redirects?.Any() == true)
        {
            var redirectList = string.Join("\n", guild.Redirects.Select(c => $"<#{c}>"));
            embed.AddField("Redirect Channels", redirectList.Length > 1024 ? "Too many to display" : redirectList);
        }

        return embed.Build();
    }

    #region Spawn Tracking

    /// <summary>
    ///     Initializes the spawn tracking system by loading active spawns from database to cache.
    ///     Called on bot startup to restore spawn state.
    /// </summary>
    public async Task InitializeSpawnTracking()
    {
        try
        {
            await using var db = await _dbContextProvider.GetConnectionAsync();
            
            // Get all active spawns from database (not caught and created within last 24 hours)
            var cutoffTime = DateTime.UtcNow.AddHours(-24);
            var activeSpawns = await db.ActiveSpawns
                .Where(s => !s.IsCaught && s.CreatedAt > cutoffTime)
                .ToListAsync();

            var redis = _cache.Redis.GetDatabase();
            
            foreach (var spawn in activeSpawns)
            {
                // Check if spawn still exists in cache
                var cacheKey = $"spawn:active:{spawn.MessageId}";
                var exists = await redis.KeyExistsAsync(cacheKey);
                
                if (!exists)
                {
                    // Add to cache with remaining TTL (24 hours from creation)
                    var remainingTime = spawn.CreatedAt.AddHours(24) - DateTime.UtcNow;
                    if (remainingTime.TotalMinutes > 0)
                    {
                        var spawnData = new
                        {
                            spawn.MessageId,
                            spawn.ChannelId,
                            spawn.GuildId,
                            spawn.PokemonName,
                            spawn.IsShiny,
                            spawn.LegendaryChance,
                            spawn.UltraBeastChance,
                            spawn.CreatedAt
                        };
                        
                        await redis.StringSetAsync(cacheKey, 
                            JsonSerializer.Serialize(spawnData),
                            remainingTime);
                    }
                    else
                    {
                        // Spawn is too old, mark as expired
                        spawn.IsCaught = true;
                        spawn.CaughtAt = DateTime.UtcNow;
                    }
                }
            }
            
            Log.Information("Initialized spawn tracking with {Count} active spawns", activeSpawns.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error initializing spawn tracking");
        }
    }

    /// <summary>
    ///     Registers a new active spawn in both cache and database.
    /// </summary>
    /// <param name="messageId">The Discord message ID of the spawn.</param>
    /// <param name="channelId">The Discord channel ID.</param>
    /// <param name="guildId">The Discord guild ID.</param>
    /// <param name="pokemonName">The name of the spawned Pokemon.</param>
    /// <param name="isShiny">Whether the Pokemon is shiny.</param>
    /// <param name="legendaryChance">The legendary spawn chance value.</param>
    /// <param name="ultraBeastChance">The Ultra Beast spawn chance value.</param>
    public async Task RegisterActiveSpawn(ulong messageId, ulong channelId, ulong guildId, 
        string pokemonName, bool isShiny, int legendaryChance, int ultraBeastChance)
    {
        try
        {
            var redis = _cache.Redis.GetDatabase();
            var cacheKey = $"spawn:active:{messageId}";
            
            var spawnData = new
            {
                MessageId = messageId,
                ChannelId = channelId,
                GuildId = guildId,
                PokemonName = pokemonName,
                IsShiny = isShiny,
                LegendaryChance = legendaryChance,
                UltraBeastChance = ultraBeastChance,
                CreatedAt = DateTime.UtcNow
            };
            
            // Cache for 24 hours
            await redis.StringSetAsync(cacheKey, 
                JsonSerializer.Serialize(spawnData),
                TimeSpan.FromHours(24));
            
            await using var db = await _dbContextProvider.GetConnectionAsync();
            var activeSpawn = new ActiveSpawn
            {
                MessageId = messageId,
                ChannelId = channelId,
                GuildId = guildId,
                PokemonName = pokemonName,
                IsShiny = isShiny,
                LegendaryChance = legendaryChance,
                UltraBeastChance = ultraBeastChance,
                CreatedAt = DateTime.UtcNow
            };
            
            await db.InsertAsync(activeSpawn);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error registering active spawn {MessageId}", messageId);
        }
    }

    /// <summary>
    ///     Gets an active spawn by message ID from cache, falling back to database.
    /// </summary>
    /// <param name="messageId">The Discord message ID of the spawn.</param>
    /// <returns>The spawn data if found, null otherwise.</returns>
    public async Task<(string PokemonName, bool IsShiny, int LegendaryChance, int UltraBeastChance)?> GetActiveSpawn(ulong messageId)
    {
        try
        {
            var redis = _cache.Redis.GetDatabase();
            var cacheKey = $"spawn:active:{messageId}";
            
            // Try cache first
            var cachedData = await redis.StringGetAsync(cacheKey);
            if (cachedData.HasValue)
            {
                var spawnData = JsonSerializer.Deserialize<dynamic>(cachedData);
                return (
                    spawnData.GetProperty("PokemonName").GetString(),
                    spawnData.GetProperty("IsShiny").GetBoolean(),
                    spawnData.GetProperty("LegendaryChance").GetInt32(),
                    spawnData.GetProperty("UltraBeastChance").GetInt32()
                );
            }
            
            await using var db = await _dbContextProvider.GetConnectionAsync();
            var spawn = await db.GetTable<ActiveSpawn>()
                .FirstOrDefaultAsync(s => s.MessageId == messageId && !s.IsCaught);
                
            if (spawn != null)
            {
                // Re-cache if found in database
                var spawnData = new
                {
                    spawn.MessageId,
                    spawn.ChannelId,
                    spawn.GuildId,
                    spawn.PokemonName,
                    spawn.IsShiny,
                    spawn.LegendaryChance,
                    spawn.UltraBeastChance,
                    spawn.CreatedAt
                };
                
                var remainingTime = spawn.CreatedAt.AddHours(24) - DateTime.UtcNow;
                if (remainingTime.TotalMinutes > 0)
                {
                    await redis.StringSetAsync(cacheKey, 
                        JsonSerializer.Serialize(spawnData),
                        remainingTime);
                }
                
                return (spawn.PokemonName, spawn.IsShiny, spawn.LegendaryChance, spawn.UltraBeastChance);
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting active spawn {MessageId}", messageId);
            return null;
        }
    }

    /// <summary>
    ///     Marks a spawn as caught in both cache and database.
    /// </summary>
    /// <param name="messageId">The Discord message ID of the spawn.</param>
    /// <param name="userId">The user ID who caught the Pokemon.</param>
    public async Task MarkSpawnAsCaught(ulong messageId, ulong userId)
    {
        try
        {
            var redis = _cache.Redis.GetDatabase();
            var cacheKey = $"spawn:active:{messageId}";
            
            // Remove from cache
            await redis.KeyDeleteAsync(cacheKey);
            
            await using var db = await _dbContextProvider.GetConnectionAsync();
            await db.GetTable<ActiveSpawn>()
                .Where(s => s.MessageId == messageId)
                .Set(s => s.IsCaught, true)
                .Set(s => s.CaughtAt, DateTime.UtcNow)
                .Set(s => s.CaughtByUserId, userId)
                .UpdateAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error marking spawn as caught {MessageId}", messageId);
        }
    }

    /// <summary>
    ///     Gets all active spawns for a channel.
    /// </summary>
    /// <param name="channelId">The Discord channel ID.</param>
    /// <returns>List of active spawn message IDs in the channel.</returns>
    public async Task<List<ulong>> GetActiveSpawnsInChannel(ulong channelId)
    {
        try
        {
            await using var db = await _dbContextProvider.GetConnectionAsync();
            var cutoffTime = DateTime.UtcNow.AddHours(-24);
            
            return await db.GetTable<ActiveSpawn>()
                .Where(s => s.ChannelId == channelId && !s.IsCaught && s.CreatedAt > cutoffTime)
                .Select(s => s.MessageId)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting active spawns for channel {ChannelId}", channelId);
            return [];
        }
    }

    #endregion

    /// <summary>
    ///     Called when the bot is ready. Initializes spawn tracking system.
    /// </summary>
    public async Task OnReadyAsync()
    {
        await InitializeSpawnTracking();
    }

    #region Cached Data Methods

    private static List<string>? _cachedNatures;
    private static DateTime _naturesLastCached = DateTime.MinValue;
    private static Dictionary<int, List<string>>? _cachedFormsByPokemonId;
    private static DateTime _formsLastCached = DateTime.MinValue;
    private static Dictionary<string, (int PokemonId, int GenderRate, int BaseHappiness)>? _cachedPokemonInfo;
    private static DateTime _pokemonInfoLastCached = DateTime.MinValue;
    private static Dictionary<int, List<int>>? _cachedAbilitiesByPokemonId;
    private static DateTime _abilitiesLastCached = DateTime.MinValue;
    private static Dictionary<int, List<string>>? _cachedPokemonByType;
    private static DateTime _pokemonByTypeLastCached = DateTime.MinValue;
    private static List<string>? _cachedCheapShopItems;
    private static DateTime _cheapShopItemsLastCached = DateTime.MinValue;
    private static List<string>? _cachedExpensiveShopItems;
    private static DateTime _expensiveShopItemsLastCached = DateTime.MinValue;
    private static List<string>? _cachedBerryItems;
    private static DateTime _berryItemsLastCached = DateTime.MinValue;
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(1);

    /// <summary>
    ///     Gets a random nature using cached data for performance.
    /// </summary>
    private async Task<string> GetRandomNatureAsync()
    {
        if (_cachedNatures == null || DateTime.UtcNow - _naturesLastCached > CacheExpiry)
        {
            var natures = await _mongoDb.Natures
                .Find(_ => true)
                .Project(n => n.Identifier)
                .ToListAsync();
            
            _cachedNatures = natures;
            _naturesLastCached = DateTime.UtcNow;
        }

        return _cachedNatures[_random.Next(_cachedNatures.Count)];
    }

    /// <summary>
    ///     Gets cached Pokemon form information for fast lookups.
    /// </summary>
    private async Task<(int PokemonId, string Identifier)?> GetCachedFormInfoAsync(string pokemonNameLower)
    {
        if (_cachedFormsByPokemonId == null || DateTime.UtcNow - _formsLastCached > CacheExpiry)
        {
            var forms = await _mongoDb.Forms
                .Find(_ => true)
                .Project(f => new { f.PokemonId, f.Identifier })
                .ToListAsync();
            
            _cachedFormsByPokemonId = forms
                .GroupBy(f => f.PokemonId)
                .ToDictionary(g => g.Key, g => g.Select(f => f.Identifier).ToList());
            
            _formsLastCached = DateTime.UtcNow;
        }

        var form = await _mongoDb.Forms
            .Find(f => f.Identifier.Equals(pokemonNameLower))
            .Project(f => new { f.PokemonId, f.Identifier })
            .FirstOrDefaultAsync();

        return form != null ? (form.PokemonId, form.Identifier) : null;
    }

    /// <summary>
    ///     Gets cached Pokemon info for fast lookups.
    /// </summary>
    private async Task<(int GenderRate, int BaseHappiness)?> GetCachedPokemonInfoAsync(int pokemonId, string pokemonNameLower)
    {
        if (_cachedPokemonInfo == null || DateTime.UtcNow - _pokemonInfoLastCached > CacheExpiry)
        {
            var pokemonInfos = await _mongoDb.PFile
                .Find(_ => true)
                .Project(p => new { p.PokemonId, p.Identifier, p.GenderRate, p.BaseHappiness })
                .ToListAsync();
            
            _cachedPokemonInfo = pokemonInfos.ToDictionary(
                p => p.Identifier, 
                p => (p.PokemonId ?? 0, p.GenderRate ?? 0, p.BaseHappiness ?? 70)
            );
            
            _pokemonInfoLastCached = DateTime.UtcNow;
        }

        // Try direct lookup first
        if (_cachedPokemonInfo.TryGetValue(pokemonNameLower, out var info))
            return (info.GenderRate, info.BaseHappiness);

        // Handle Alola variant fallback
        if (pokemonNameLower.Contains("alola"))
        {
            var pokemonNameWithoutSuffix = pokemonNameLower.Split("-")[0];
            if (_cachedPokemonInfo.TryGetValue(pokemonNameWithoutSuffix, out var alolaInfo))
                return (alolaInfo.GenderRate, alolaInfo.BaseHappiness);
        }

        return null;
    }

    /// <summary>
    ///     Gets cached ability IDs for a Pokemon.
    /// </summary>
    private async Task<List<int>> GetCachedAbilityIdsAsync(int pokemonId)
    {
        if (_cachedAbilitiesByPokemonId == null || DateTime.UtcNow - _abilitiesLastCached > CacheExpiry)
        {
            var abilities = await _mongoDb.PokeAbilities
                .Find(_ => true)
                .Project(a => new { a.PokemonId, a.AbilityId })
                .ToListAsync();
            
            _cachedAbilitiesByPokemonId = abilities
                .GroupBy(a => a.PokemonId)
                .ToDictionary(g => g.Key, g => g.Select(a => a.AbilityId).ToList());
            
            _abilitiesLastCached = DateTime.UtcNow;
        }

        return _cachedAbilitiesByPokemonId.GetValueOrDefault(pokemonId, []);
    }

    /// <summary>
    ///     Gets cached Pokemon names by type for fast lookups.
    /// </summary>
    private async Task<List<string>> GetCachedPokemonByTypeAsync(int typeId)
    {
        if (_cachedPokemonByType == null || DateTime.UtcNow - _pokemonByTypeLastCached > CacheExpiry)
        {
            // Load all Pokemon types and forms in parallel
            var pokemonTypesTask = _mongoDb.PokemonTypes
                .Find(_ => true)
                .Project(p => new { p.PokemonId, p.Types })
                .ToListAsync();

            var formsTask = _mongoDb.Forms
                .Find(_ => true)
                .Project(f => new { f.PokemonId, f.Identifier })
                .ToListAsync();

            await Task.WhenAll(pokemonTypesTask, formsTask);
            var pokemonTypes = await pokemonTypesTask;
            var forms = await formsTask;

            // Create Pokemon ID to form name mapping
            var pokemonIdToForms = forms
                .GroupBy(f => f.PokemonId)
                .ToDictionary(g => g.Key, g => g.Select(f => f.Identifier.ToTitleCase()).ToList());

            // Build type to Pokemon mapping
            _cachedPokemonByType = new Dictionary<int, List<string>>();
            
            foreach (var pokemonType in pokemonTypes)
            {
                foreach (var type in pokemonType.Types)
                {
                    if (!_cachedPokemonByType.ContainsKey(type))
                        _cachedPokemonByType[type] = [];

                    if (pokemonIdToForms.TryGetValue(pokemonType.PokemonId, out var formNames))
                    {
                        var validForms = formNames.Where(name => PokemonConstants.TotalList.Contains(name));
                        _cachedPokemonByType[type].AddRange(validForms);
                    }
                }
            }

            _pokemonByTypeLastCached = DateTime.UtcNow;
        }

        return _cachedPokemonByType.GetValueOrDefault(typeId, []);
    }

    /// <summary>
    ///     Gets cached cheap shop items for berry drop rewards.
    /// </summary>
    private async Task<List<string>> GetCachedCheapShopItemsAsync()
    {
        if (_cachedCheapShopItems == null || DateTime.UtcNow - _cheapShopItemsLastCached > CacheExpiry)
        {
            var cheapItems = await _mongoDb.Shop
                .Find(i => i.Price <= 8000 && !i.Item.EndsWith("key"))
                .Project(i => i.Item)
                .ToListAsync();
            
            _cachedCheapShopItems = cheapItems;
            _cheapShopItemsLastCached = DateTime.UtcNow;
        }

        return _cachedCheapShopItems;
    }

    /// <summary>
    ///     Gets cached expensive shop items for berry drop rewards.
    /// </summary>
    private async Task<List<string>> GetCachedExpensiveShopItemsAsync()
    {
        if (_cachedExpensiveShopItems == null || DateTime.UtcNow - _expensiveShopItemsLastCached > CacheExpiry)
        {
            var expensiveItems = await _mongoDb.Shop
                .Find(i => i.Price >= 8000 && i.Price <= 20000 && !i.Item.EndsWith("key"))
                .Project(i => i.Item)
                .ToListAsync();
            
            _cachedExpensiveShopItems = expensiveItems;
            _expensiveShopItemsLastCached = DateTime.UtcNow;
        }

        return _cachedExpensiveShopItems;
    }

    /// <summary>
    ///     Gets cached berry items for berry drop rewards.
    /// </summary>
    private async Task<List<string>> GetCachedBerryItemsAsync()
    {
        if (_cachedBerryItems == null || DateTime.UtcNow - _berryItemsLastCached > CacheExpiry)
        {
            var berryItems = await _mongoDb.Items
                .Find(i => i.Identifier.EndsWith("-berry"))
                .Project(i => i.Identifier)
                .ToListAsync();
            
            _cachedBerryItems = berryItems;
            _berryItemsLastCached = DateTime.UtcNow;
        }

        return _cachedBerryItems;
    }

    #endregion

    /// <summary>
    ///     Represents the result of a Pokemon catch attempt.
    ///     Contains information about success, messages, and configured behaviors.
    /// </summary>
    /// <param name="Success">Whether the catch was successful.</param>
    /// <param name="Message">A message describing the result, if applicable.</param>
    /// <param name="ResponseEmbed">The embed to display in response to the catch.</param>
    /// <param name="ShouldDeleteSpawn">Whether the spawn message should be deleted.</param>
    /// <param name="ShouldPinSpawn">Whether the spawn message should be pinned.</param>
    public record CatchResult(
        bool Success,
        string Message,
        Embed ResponseEmbed,
        bool ShouldDeleteSpawn,
        bool ShouldPinSpawn);
}