using LinqToDB;
using EeveeCore.Common.ModuleBehaviors;
using EeveeCore.Modules.Missions.Services;
using EeveeCore.Modules.Pokemon.Services;
using Serilog;
using System.Text.Json;
using LinqToDB.Async;
using EggHatchery = EeveeCore.Database.Linq.Models.Pokemon.EggHatchery;
using UserPokemonOwnership = EeveeCore.Database.Linq.Models.Pokemon.UserPokemonOwnership;
using User = EeveeCore.Database.Linq.Models.Bot.User;

namespace EeveeCore.Modules.Parties.Services;

/// <summary>
///     Provides functionality for managing Pokémon egg hatcheries.
///     Handles operations for adding, removing, and monitoring eggs in different hatchery groups
///     with varying incubation rates and slot limits based on user's Patreon tier.
///     Also handles egg hatching when counters reach zero.
/// </summary>
/// <param name="dbContext">The database context for accessing egg and user data.</param>
/// <param name="client">The Fluxer client, for messages and usernames.</param>
/// <param name="botLog">The bot event log.</param>
/// <param name="missionService">The mission service for tracking mission progress.</param>
/// <param name="pokemonService">The Pokemon service for creating hatched Pokemon.</param>
public class HatcheryService(
    LinqToDbConnectionProvider dbContext,
    FluxerClient client,
    BotLog botLog,
    MissionService missionService,
    PokemonService pokemonService) : INService, IReadyExecutor
{
    private const string PremiumX2Icon = Emojis.PremiumX2;
    private const string PremiumX3Icon = Emojis.PremiumX3;

    private readonly MissionService _missionService = missionService;
    private readonly PokemonService _pokemonService = pokemonService;
    private readonly Random _random = new();

    /// <summary>
    ///     Cache for user message cooldowns to prevent spam
    ///     Maps user IDs to the time of their last message processing
    /// </summary>
    private readonly ConcurrentDictionary<ulong, DateTime> _userCooldowns = new();

    /// <summary>
    ///     Dictionary storing paged results for users viewing hatchery data.
    ///     Maps user IDs to their current page state.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, (List<EmbedBuilder> Pages, int CurrentPage)> _pagedResults = new();

    /// <summary>
    ///     Stores a paged result for a user.
    ///     Used for maintaining pagination state when browsing hatchery information.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="pages">The list of embeds representing each page.</param>
    /// <param name="currentPage">The current page index being viewed.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task StorePagedResult(ulong userId, List<EmbedBuilder> pages, int currentPage)
    {
        _pagedResults[userId] = (pages, currentPage);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Retrieves a paged result for a user.
    ///     Used when a user navigates through hatchery pages.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns the user's
    ///     pages and current page index, or null if not found.
    /// </returns>
    public Task<(List<EmbedBuilder> Pages, int CurrentPage)> GetPagedResult(ulong userId)
    {
        if (_pagedResults.TryGetValue(userId, out var result)) return Task.FromResult(result);

        return Task.FromResult<(List<EmbedBuilder> Pages, int CurrentPage)>((null, 0));
    }

    /// <summary>
    ///     Updates a user's current page index in their paged result.
    ///     Called when a user navigates to a different page.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="newPage">The new page index to set.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task UpdatePagedResult(ulong userId, int newPage)
    {
        if (_pagedResults.TryGetValue(userId, out var result)) _pagedResults[userId] = (result.Pages, newPage);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Initializes the hatchery service and registers the message handler for egg hatching.
    /// </summary>
    public Task OnReadyAsync()
    {
        client.Gateway.MessageCreated += HandleMessageForEggHatching;
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Handles incoming messages to process egg hatching for users.
    ///     Decrements egg counters and converts eggs to Pokemon when they hatch.
    /// </summary>
    /// <param name="message">The received message.</param>
    private async Task HandleMessageForEggHatching(SocketMessage message)
    {
        try
        {
            if (message.Author is not { IsBot: false } author || message.Guild is not { } guild)
                return;

            var userId = author.Id;

            // Implement cooldown to prevent spam (similar to Python version)
            var now = DateTime.UtcNow;
            if (_userCooldowns.TryGetValue(userId, out var lastProcess) && now < lastProcess.AddSeconds(4))
                return;

            // Check if user exists
            await using var db = await dbContext.GetConnectionAsync();
            var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null) return;

            _userCooldowns[userId] = now;

            // Process egg hatching for all hatchery groups
            var hatchedEggs = new List<(string EggName, Database.Linq.Models.Pokemon.Pokemon Pokemon)>();

            // Process each hatchery group (1, 2, 3)
            for (short group = 1; group <= 3; group++)
            {
                var groupHatched = await ProcessHatcheryGroup(userId, group, guild.Id);
                hatchedEggs.AddRange(groupHatched);
            }

            // Send hatching notifications and fire mission events
            if (hatchedEggs.Count > 0)
            {
                await SendHatchingNotifications(message, hatchedEggs);
                
                // Fire mission events for each hatched Pokemon
                foreach (var (eggName, pokemon) in hatchedEggs)
                {
                    _ = Task.Run(async () => await _missionService.FirePokemonHatchedEvent(message, pokemon));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing egg hatching for user {UserId}", message.Author.Id);
        }
    }

    /// <summary>
    ///     Processes egg hatching for a specific hatchery group.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="group">The hatchery group (1-3).</param>
    /// <param name="guildId">The guild ID for premium speed calculations.</param>
    /// <returns>List of hatched eggs with their Pokemon.</returns>
    private async Task<List<(string EggName, Database.Linq.Models.Pokemon.Pokemon Pokemon)>> ProcessHatcheryGroup(ulong userId, short group, ulong guildId)
    {
        var hatchedEggs = new List<(string, Database.Linq.Models.Pokemon.Pokemon)>();

        await using var db = await dbContext.GetConnectionAsync();
        
        // Get hatchery for this user and group
        var hatchery = await db.EggHatcheries
            .Where(e => e.UserId == userId && e.Group == group)
            .FirstOrDefaultAsync();
            
        if (hatchery == null) return hatchedEggs;

        // Calculate speed modifier based on hatchery group and premium status
        var speedModifier = await GetHatcherySpeedModifier(userId, group, guildId);

        // Get all slot IDs from the hatchery
        var slotIds = new[] { hatchery.Slot1, hatchery.Slot2, hatchery.Slot3, hatchery.Slot4, hatchery.Slot5, 
                             hatchery.Slot6, hatchery.Slot7, hatchery.Slot8, hatchery.Slot9, hatchery.Slot10 }
            .Where(id => id.HasValue)
            .Select(id => id.Value)
            .ToList();

        if (slotIds.Count == 0) return hatchedEggs;

        // Get all eggs in these slots with Pokemon data
        var eggs = await db.UserPokemonOwnerships
            .Where(p => slotIds.Contains(p.PokemonId))
            .Join(db.UserPokemon, o => o.PokemonId, p => p.Id, (o, p) => new { Ownership = o, Pokemon = p })
            .ToListAsync();

        foreach (var egg in eggs)
        {
            // Decrement counter based on speed modifier
            var newCounter = Math.Max(0, egg.Pokemon.Counter.GetValueOrDefault() - speedModifier);
            
            if (newCounter == 0)
            {
                // Egg is ready to hatch!
                var pokemon = await HatchEggFromOwnership(egg.Pokemon);
                if (pokemon != null)
                {
                    hatchedEggs.Add((egg.Pokemon.PokemonName, pokemon));
                    
                    // Remove the egg from hatchery slot
                    await RemoveEggFromHatcherySlot(hatchery, egg.Pokemon.Id);
                    
                    // Chance for common chest drop (1 in 200, same as Python)
                    if (_random.Next(200) == 0)
                    {
                        await AddCommonChestToUser(userId);
                    }
                }
            }
            else
            {
                // Update the counter
                await db.UserPokemon
                    .Where(p => p.Id == egg.Pokemon.Id)
                    .Set(p => p.Counter, newCounter)
                    .UpdateAsync();
            }
        }

        return hatchedEggs;
    }

    /// <summary>
    ///     Converts an egg to a Pokemon when it hatches.
    /// </summary>
    /// <param name="pokemon">The pokemon record to hatch.</param>
    /// <returns>The hatched Pokemon.</returns>
    private async Task<Database.Linq.Models.Pokemon.Pokemon?> HatchEggFromOwnership(Database.Linq.Models.Pokemon.Pokemon pokemon)
    {
        try
        {
            // The egg is already a Pokemon record, just update its Counter to 0
            // and mark it as hatched by removing the Counter
            await using var db = await dbContext.GetConnectionAsync();
            await db.UserPokemon
                .Where(p => p.Id == pokemon.Id)
                .Set(p => p.Counter, 0)
                .UpdateAsync();

            // Return the Pokemon (already have the data)
            return pokemon;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error hatching egg {PokemonId} for user {UserId}", pokemon.Id, pokemon.Owner);
            return null;
        }
    }

    /// <summary>
    ///     Removes an egg from the hatchery slot when it hatches.
    /// </summary>
    private async Task RemoveEggFromHatcherySlot(EggHatchery hatchery, ulong eggId)
    {
        try
        {
            await using var db = await dbContext.GetConnectionAsync();
            
            // Find which slot contains this egg and clear it
            if (hatchery.Slot1 == eggId)
                await db.EggHatcheries.Where(e => e.Id == hatchery.Id).Set(e => e.Slot1, (ulong?)null).UpdateAsync();
            else if (hatchery.Slot2 == eggId)
                await db.EggHatcheries.Where(e => e.Id == hatchery.Id).Set(e => e.Slot2, (ulong?)null).UpdateAsync();
            else if (hatchery.Slot3 == eggId)
                await db.EggHatcheries.Where(e => e.Id == hatchery.Id).Set(e => e.Slot3, (ulong?)null).UpdateAsync();
            else if (hatchery.Slot4 == eggId)
                await db.EggHatcheries.Where(e => e.Id == hatchery.Id).Set(e => e.Slot4, (ulong?)null).UpdateAsync();
            else if (hatchery.Slot5 == eggId)
                await db.EggHatcheries.Where(e => e.Id == hatchery.Id).Set(e => e.Slot5, (ulong?)null).UpdateAsync();
            else if (hatchery.Slot6 == eggId)
                await db.EggHatcheries.Where(e => e.Id == hatchery.Id).Set(e => e.Slot6, (ulong?)null).UpdateAsync();
            else if (hatchery.Slot7 == eggId)
                await db.EggHatcheries.Where(e => e.Id == hatchery.Id).Set(e => e.Slot7, (ulong?)null).UpdateAsync();
            else if (hatchery.Slot8 == eggId)
                await db.EggHatcheries.Where(e => e.Id == hatchery.Id).Set(e => e.Slot8, (ulong?)null).UpdateAsync();
            else if (hatchery.Slot9 == eggId)
                await db.EggHatcheries.Where(e => e.Id == hatchery.Id).Set(e => e.Slot9, (ulong?)null).UpdateAsync();
            else if (hatchery.Slot10 == eggId)
                await db.EggHatcheries.Where(e => e.Id == hatchery.Id).Set(e => e.Slot10, (ulong?)null).UpdateAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error removing egg {EggId} from hatchery slot for user {UserId}", eggId, hatchery.UserId);
        }
    }

    /// <summary>
    ///     Gets the speed modifier for a hatchery group based on premium status.
    /// </summary>
    private async Task<int> GetHatcherySpeedModifier(ulong userId, short group, ulong guildId)
    {
        // Base speed: 1 per message
        const int baseSpeed = 1;

        return group switch
        {
            1 => baseSpeed,
            2 => (int)(baseSpeed * 2.5),
            3 => baseSpeed * 3,
            _ => baseSpeed
        };
    }

    /// <summary>
    ///     Adds a common chest to the user's inventory.
    /// </summary>
    private async Task AddCommonChestToUser(ulong userId)
    {
        try
        {
            await using var db = await dbContext.GetConnectionAsync();
            var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null) return;

            var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Inventory ?? "{}") ?? new Dictionary<string, int>();
            inventory["common chest"] = inventory.GetValueOrDefault("common chest", 0) + 1;

            await db.Users
                .Where(u => u.UserId == userId)
                .Set(u => u.Inventory, JsonSerializer.Serialize(inventory))
                .UpdateAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding common chest to user {UserId}", userId);
        }
    }

    /// <summary>
    ///     Sends hatching notifications to the channel.
    /// </summary>
    private async Task SendHatchingNotifications(SocketMessage message, List<(string EggName, Database.Linq.Models.Pokemon.Pokemon Pokemon)> hatchedEggs)
    {
        try
        {
            var notifications = new List<string>();
            
            foreach (var (eggName, pokemon) in hatchedEggs)
            {
                notifications.Add($"Congratulations!\nYour {eggName} Egg has hatched!");
                
                // Check for common chest drops
                if (_random.Next(200) == 0)
                {
                    notifications.Add("How...\nIt was holding a common chest within its egg!");
                }
            }

            if (notifications.Count > 0)
            {
                var embed = new EmbedBuilder()
                    .WithDescription(string.Join("\n", notifications))
                    .WithColor(new Color(0xFF, 0x49, 0xE6))
                    .Build();

                await message.Channel.SendMessageAsync(embeds: [embed]);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error sending hatching notifications for user {UserId}", message.Author.Id);
        }
    }

    /// <summary>
    ///     Generates an embed displaying a user's hatchery information for a specific group.
    ///     Shows eggs in each slot along with their incubation progress.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="group">The hatchery group number (1-3).</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns an EmbedBuilder
    ///     with the hatchery information, or null if the group is invalid.
    /// </returns>
    public async Task<EmbedBuilder> GetHatcheryViewEmbed(ulong userId, short group)
    {
        // Validate group number
        if (group < 1 || group > 3) return null;

        // Get user's Patreon tier to determine available slots
        var patreonTier = await GetPatreonTier(userId);
        string maxMsg;

        // Initialize embed
        var embed = new EmbedBuilder()
            .WithTitle($"{await GetUserNameAsync(userId)}'s Hatchery (Group #{group})")
            .WithColor(new Color(0xff, 0x00, 0x60));

        // Get egg data from the hatchery group
        var eggData = await GetHatcheryData(userId, group, patreonTier);
        if (eggData.Item3.Count == 0) return null;

        var (slots, maxSlots, slotData) = eggData;

        // Format the slot information into the embed description
        var description = string.Empty;
        foreach (var (slotNumber, eggName, pokemonNumber, stepCounter) in slotData)
            if (!string.IsNullOrEmpty(eggName))
                description +=
                    $"**__Slot {slotNumber}__** - **Steps:** `{stepCounter}`\n`{eggName} [{pokemonNumber}]`\n\n";

        embed.WithDescription(description);

        // Add the maximum egg slots information
        switch (patreonTier)
        {
            case PatreonTier.None:
            case PatreonTier.Silver:
                maxMsg = "6";
                break;
            case PatreonTier.Gold:
                maxMsg = $"7 {PremiumX2Icon}";
                break;
            case PatreonTier.Crystal:
                maxMsg = $"10 {PremiumX3Icon}";
                break;
            default:
                maxMsg = "6";
                break;
        }

        embed.AddField("Max Egg Slots", maxMsg);

        // Add footer based on group
        switch (group)
        {
            case 1:
                embed.WithFooter("Group 1 | Full Steps Counted");
                break;
            case 2:
                embed.WithFooter("Group 2 | 1/3 Steps Counted");
                break;
            case 3:
                embed.WithFooter("Group 3 | 1/6 Steps Counted");
                break;
            default:
                embed.WithFooter("Something went wrong | No Steps Counted");
                break;
        }

        return embed;
    }

    /// <summary>
    ///     Adds an egg to a specific slot in a hatchery group.
    ///     Performs validation checks for slot access based on Patreon tier.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="eggIndex">The index of the egg in the user's Pokémon collection.</param>
    /// <param name="group">The hatchery group number (1-3).</param>
    /// <param name="slot">The slot number within the group (1-10).</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a tuple with
    ///     a success indicator and a result message.
    /// </returns>
    public async Task<(bool Success, string Message)> AddEggToHatchery(ulong userId, ulong eggIndex, short group,
        int slot)
    {
        try
        {
            await using var db = await dbContext.GetConnectionAsync();
            // Validate parameters
            if (group < 1 || group > 3 || slot < 1 || slot > 10) return (false, "Invalid group or slot number.");

            // Get user's Patreon tier to check slot access
            var patreonTier = await GetPatreonTier(userId);

            switch (slot)
            {
                // Check if the user has access to the specified slot based on their Patreon tier
                case 7 when patreonTier < PatreonTier.Gold:
                    return (false, "You must be a Gold or Crystal Patreon to add eggs to slot 7.");
                case >= 8 when patreonTier < PatreonTier.Crystal:
                    return (false, "You must be a Crystal Patreon to add eggs to slots 8-10.");
            }

            // Convert from 1-based to 0-based indexing
            var position = eggIndex - 1;

            // Get the egg from the ownership table by position
            var ownership = await db.GetTable<UserPokemonOwnership>()
                .FirstOrDefaultAsync(o => o.UserId == userId && o.Position == position);

            if (ownership == null)
                return (false, "Invalid egg index.");

            // Get the egg Pokémon
            var eggId = ownership.PokemonId;
            var egg = await db.GetTable<Database.Linq.Models.Pokemon.Pokemon>()
                .FirstOrDefaultAsync(p => p.Id == eggId && p.PokemonName == "Egg");

            if (egg == null) return (false, "The provided ID is not a valid Egg or doesn't exist.");

            // Check if this egg is already in any hatchery
            var hatcheriesWithEgg = await db.GetTable<EggHatchery>()
                .Where(h => h.UserId == userId &&
                            (h.Slot1 == eggId || h.Slot2 == eggId || h.Slot3 == eggId ||
                             h.Slot4 == eggId || h.Slot5 == eggId || h.Slot6 == eggId ||
                             h.Slot7 == eggId || h.Slot8 == eggId || h.Slot9 == eggId || h.Slot10 == eggId))
                .AnyAsync();

            if (hatcheriesWithEgg) return (false, "This Egg is already in a hatchery slot.");

            // Get or create the hatchery record for this user and group
            var hatchery = await db.GetTable<EggHatchery>()
                .FirstOrDefaultAsync(h => h.UserId == userId && h.Group == group);

            if (hatchery == null)
            {
                // Create new hatchery record
                hatchery = new EggHatchery
                {
                    UserId = userId,
                    Group = group
                };
                hatchery.Id = (ulong)await db.InsertWithInt64IdentityAsync(hatchery);
            }

            // Check if the selected slot is already occupied
            var isSlotOccupied = false;
            switch (slot)
            {
                case 1: isSlotOccupied = hatchery.Slot1 != null; break;
                case 2: isSlotOccupied = hatchery.Slot2 != null; break;
                case 3: isSlotOccupied = hatchery.Slot3 != null; break;
                case 4: isSlotOccupied = hatchery.Slot4 != null; break;
                case 5: isSlotOccupied = hatchery.Slot5 != null; break;
                case 6: isSlotOccupied = hatchery.Slot6 != null; break;
                case 7: isSlotOccupied = hatchery.Slot7 != null; break;
                case 8: isSlotOccupied = hatchery.Slot8 != null; break;
                case 9: isSlotOccupied = hatchery.Slot9 != null; break;
                case 10: isSlotOccupied = hatchery.Slot10 != null; break;
            }

            if (isSlotOccupied) return (false, $"Slot {slot} in group {group} is already occupied.");

            // Update the hatchery record using LinqToDB Set pattern
            switch (slot)
            {
                case 1:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot1, eggId)
                        .UpdateAsync();
                    break;
                case 2:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot2, eggId)
                        .UpdateAsync();
                    break;
                case 3:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot3, eggId)
                        .UpdateAsync();
                    break;
                case 4:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot4, eggId)
                        .UpdateAsync();
                    break;
                case 5:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot5, eggId)
                        .UpdateAsync();
                    break;
                case 6:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot6, eggId)
                        .UpdateAsync();
                    break;
                case 7:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot7, eggId)
                        .UpdateAsync();
                    break;
                case 8:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot8, eggId)
                        .UpdateAsync();
                    break;
                case 9:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot9, eggId)
                        .UpdateAsync();
                    break;
                case 10:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot10, eggId)
                        .UpdateAsync();
                    break;
            }

            return (true, $"Egg has been added to slot {slot} in group {group}.");
        }
        catch (Exception ex)
        {
            await botLog.SendAsync($"**__ERROR OCCURRED__**: ```{ex.Message}\n{ex.StackTrace}```");

            return (false, "Something went wrong\nPlease try running this command again.");
        }
    }

    /// <summary>
    ///     Removes an egg from a specific slot in a hatchery group.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="group">The hatchery group number (1-3).</param>
    /// <param name="slot">The slot number within the group (1-10).</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a tuple with
    ///     a success indicator and a result message.
    /// </returns>
    public async Task<(bool Success, string Message)> RemoveEggFromHatchery(ulong userId, int group, int slot)
    {
        try
        {
            await using var db = await dbContext.GetConnectionAsync();

            // Validate parameters
            if (group < 1 || group > 3 || slot < 1 || slot > 10) return (false, "Invalid group or slot number.");

            // Get the hatchery for this user and group
            var hatchery = await db.EggHatcheries
                .FirstOrDefaultAsync(h => h.UserId == userId && h.Group == group);

            if (hatchery == null) return (false, $"You don't have a hatchery in group {group}.");

            // Check if there's an egg in the selected slot
            ulong? eggId = null;
            switch (slot)
            {
                case 1: eggId = hatchery.Slot1; break;
                case 2: eggId = hatchery.Slot2; break;
                case 3: eggId = hatchery.Slot3; break;
                case 4: eggId = hatchery.Slot4; break;
                case 5: eggId = hatchery.Slot5; break;
                case 6: eggId = hatchery.Slot6; break;
                case 7: eggId = hatchery.Slot7; break;
                case 8: eggId = hatchery.Slot8; break;
                case 9: eggId = hatchery.Slot9; break;
                case 10: eggId = hatchery.Slot10; break;
            }

            if (eggId == null) return (false, $"Slot {slot} in group {group} is empty.");

            // Update the hatchery record using LinqToDB
            switch (slot)
            {
                case 1:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot1, (ulong?)null)
                        .UpdateAsync();
                    break;
                case 2:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot2, (ulong?)null)
                        .UpdateAsync();
                    break;
                case 3:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot3, (ulong?)null)
                        .UpdateAsync();
                    break;
                case 4:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot4, (ulong?)null)
                        .UpdateAsync();
                    break;
                case 5:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot5, (ulong?)null)
                        .UpdateAsync();
                    break;
                case 6:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot6, (ulong?)null)
                        .UpdateAsync();
                    break;
                case 7:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot7, (ulong?)null)
                        .UpdateAsync();
                    break;
                case 8:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot8, (ulong?)null)
                        .UpdateAsync();
                    break;
                case 9:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot9, (ulong?)null)
                        .UpdateAsync();
                    break;
                case 10:
                    await db.GetTable<EggHatchery>()
                        .Where(h => h.Id == hatchery.Id)
                        .Set(h => h.Slot10, (ulong?)null)
                        .UpdateAsync();
                    break;
            }

            return (true, $"Removed egg from slot {slot} in group {group}.");
        }
        catch (Exception ex)
        {
            await botLog.SendAsync($"**__ERROR OCCURRED__**: ```{ex.Message}\n{ex.StackTrace}```");

            return (false, "Something went wrong\nPlease try running this command again.");
        }
    }

    /// <summary>
    ///     Sets multiple eggs in a hatchery group simultaneously.
    ///     Used for batch processing of egg placement.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="group">The hatchery group number (1-3).</param>
    /// <param name="slot1">The egg index for slot 1, or null if not changed.</param>
    /// <param name="slot2">The egg index for slot 2, or null if not changed.</param>
    /// <param name="slot3">The egg index for slot 3, or null if not changed.</param>
    /// <param name="slot4">The egg index for slot 4, or null if not changed.</param>
    /// <param name="slot5">The egg index for slot 5, or null if not changed.</param>
    /// <param name="slot6">The egg index for slot 6, or null if not changed.</param>
    /// <param name="slot7">The egg index for slot 7, or null if not changed.</param>
    /// <param name="slot8">The egg index for slot 8, or null if not changed.</param>
    /// <param name="slot9">The egg index for slot 9, or null if not changed.</param>
    /// <param name="slot10">The egg index for slot 10, or null if not changed.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a dictionary
    ///     mapping group numbers to lists of successfully added slot numbers.
    /// </returns>
    public async Task<Dictionary<int, List<int>>> SetMultipleEggs(ulong userId, short group, int? slot1, int? slot2,
        int? slot3,
        int? slot4, int? slot5, int? slot6, int? slot7, int? slot8, int? slot9, int? slot10)
    {
        var slotArray = new[] { slot1, slot2, slot3, slot4, slot5, slot6, slot7, slot8, slot9, slot10 };
        var addedEggs = new Dictionary<int, List<int>>();

        // Process each provided egg index
        for (var slotIndex = 0; slotIndex < slotArray.Length; slotIndex++)
        {
            var eggIndex = slotArray[slotIndex];
            if (eggIndex.HasValue)
            {
                var slot = slotIndex + 1;
                var result = await AddEggToHatchery(userId, (ulong)eggIndex.Value, group, slot);

                if (result.Success)
                {
                    if (!addedEggs.ContainsKey(group)) addedEggs[group] = new List<int>();
                    addedEggs[group].Add(slot);
                }
            }
        }

        return addedEggs;
    }

    /// <summary>
    ///     Swaps two eggs between slots in the hatchery system.
    ///     Can swap eggs between different groups.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="egg1Id">The ID of the first egg to swap.</param>
    /// <param name="egg2Id">The ID of the second egg to swap.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a tuple with
    ///     a success indicator and a result message.
    /// </returns>
    public async Task<(bool Success, string Message)> SwapEggs(ulong userId, ulong egg1Id, ulong egg2Id)
    {
        try
        {
            await using var db = await dbContext.GetConnectionAsync();

            // Find which slots these eggs are in
            var hatcheries = await db.EggHatcheries
                .Where(h => h.UserId == userId)
                .ToListAsync();

            EggHatchery hatchery1 = null;
            EggHatchery hatchery2 = null;
            var egg1Slot = 0;
            var egg2Slot = 0;
            short? egg1Group = 0;
            short? egg2Group = 0;

            // Find the first egg
            foreach (var hatchery in hatcheries)
            {
                if (hatchery.Slot1 == egg1Id)
                {
                    hatchery1 = hatchery;
                    egg1Slot = 1;
                    egg1Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot2 == egg1Id)
                {
                    hatchery1 = hatchery;
                    egg1Slot = 2;
                    egg1Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot3 == egg1Id)
                {
                    hatchery1 = hatchery;
                    egg1Slot = 3;
                    egg1Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot4 == egg1Id)
                {
                    hatchery1 = hatchery;
                    egg1Slot = 4;
                    egg1Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot5 == egg1Id)
                {
                    hatchery1 = hatchery;
                    egg1Slot = 5;
                    egg1Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot6 == egg1Id)
                {
                    hatchery1 = hatchery;
                    egg1Slot = 6;
                    egg1Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot7 == egg1Id)
                {
                    hatchery1 = hatchery;
                    egg1Slot = 7;
                    egg1Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot8 == egg1Id)
                {
                    hatchery1 = hatchery;
                    egg1Slot = 8;
                    egg1Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot9 == egg1Id)
                {
                    hatchery1 = hatchery;
                    egg1Slot = 9;
                    egg1Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot10 != egg1Id) continue;
                hatchery1 = hatchery;
                egg1Slot = 10;
                egg1Group = hatchery.Group;
                break;
            }

            // Find the second egg
            foreach (var hatchery in hatcheries)
            {
                if (hatchery.Slot1 == egg2Id)
                {
                    hatchery2 = hatchery;
                    egg2Slot = 1;
                    egg2Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot2 == egg2Id)
                {
                    hatchery2 = hatchery;
                    egg2Slot = 2;
                    egg2Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot3 == egg2Id)
                {
                    hatchery2 = hatchery;
                    egg2Slot = 3;
                    egg2Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot4 == egg2Id)
                {
                    hatchery2 = hatchery;
                    egg2Slot = 4;
                    egg2Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot5 == egg2Id)
                {
                    hatchery2 = hatchery;
                    egg2Slot = 5;
                    egg2Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot6 == egg2Id)
                {
                    hatchery2 = hatchery;
                    egg2Slot = 6;
                    egg2Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot7 == egg2Id)
                {
                    hatchery2 = hatchery;
                    egg2Slot = 7;
                    egg2Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot8 == egg2Id)
                {
                    hatchery2 = hatchery;
                    egg2Slot = 8;
                    egg2Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot9 == egg2Id)
                {
                    hatchery2 = hatchery;
                    egg2Slot = 9;
                    egg2Group = hatchery.Group;
                    break;
                }

                if (hatchery.Slot10 == egg2Id)
                {
                    hatchery2 = hatchery;
                    egg2Slot = 10;
                    egg2Group = hatchery.Group;
                    break;
                }
            }

            if (hatchery1 == null || hatchery2 == null)
                return (false, "One or both of the provided egg IDs are not in the hatchery.");

            // Swap the eggs
            ulong? temp = null;
            switch (egg1Slot)
            {
                case 1:
                    temp = hatchery1.Slot1;
                    hatchery1.Slot1 = null;
                    break;
                case 2:
                    temp = hatchery1.Slot2;
                    hatchery1.Slot2 = null;
                    break;
                case 3:
                    temp = hatchery1.Slot3;
                    hatchery1.Slot3 = null;
                    break;
                case 4:
                    temp = hatchery1.Slot4;
                    hatchery1.Slot4 = null;
                    break;
                case 5:
                    temp = hatchery1.Slot5;
                    hatchery1.Slot5 = null;
                    break;
                case 6:
                    temp = hatchery1.Slot6;
                    hatchery1.Slot6 = null;
                    break;
                case 7:
                    temp = hatchery1.Slot7;
                    hatchery1.Slot7 = null;
                    break;
                case 8:
                    temp = hatchery1.Slot8;
                    hatchery1.Slot8 = null;
                    break;
                case 9:
                    temp = hatchery1.Slot9;
                    hatchery1.Slot9 = null;
                    break;
                case 10:
                    temp = hatchery1.Slot10;
                    hatchery1.Slot10 = null;
                    break;
            }

            ulong? temp2 = null;
            switch (egg2Slot)
            {
                case 1:
                    temp2 = hatchery2.Slot1;
                    hatchery2.Slot1 = temp;
                    break;
                case 2:
                    temp2 = hatchery2.Slot2;
                    hatchery2.Slot2 = temp;
                    break;
                case 3:
                    temp2 = hatchery2.Slot3;
                    hatchery2.Slot3 = temp;
                    break;
                case 4:
                    temp2 = hatchery2.Slot4;
                    hatchery2.Slot4 = temp;
                    break;
                case 5:
                    temp2 = hatchery2.Slot5;
                    hatchery2.Slot5 = temp;
                    break;
                case 6:
                    temp2 = hatchery2.Slot6;
                    hatchery2.Slot6 = temp;
                    break;
                case 7:
                    temp2 = hatchery2.Slot7;
                    hatchery2.Slot7 = temp;
                    break;
                case 8:
                    temp2 = hatchery2.Slot8;
                    hatchery2.Slot8 = temp;
                    break;
                case 9:
                    temp2 = hatchery2.Slot9;
                    hatchery2.Slot9 = temp;
                    break;
                case 10:
                    temp2 = hatchery2.Slot10;
                    hatchery2.Slot10 = temp;
                    break;
            }

            switch (egg1Slot)
            {
                case 1: hatchery1.Slot1 = temp2; break;
                case 2: hatchery1.Slot2 = temp2; break;
                case 3: hatchery1.Slot3 = temp2; break;
                case 4: hatchery1.Slot4 = temp2; break;
                case 5: hatchery1.Slot5 = temp2; break;
                case 6: hatchery1.Slot6 = temp2; break;
                case 7: hatchery1.Slot7 = temp2; break;
                case 8: hatchery1.Slot8 = temp2; break;
                case 9: hatchery1.Slot9 = temp2; break;
                case 10: hatchery1.Slot10 = temp2; break;
            }


            return (true, $"Eggs {egg1Id} and {egg2Id} have been swapped.");
        }
        catch (Exception ex)
        {
            await botLog.SendAsync($"**__ERROR OCCURRED__**: ```{ex.Message}\n{ex.StackTrace}```");

            return (false, "Something went wrong\nPlease try running this command again.");
        }
    }

    /// <summary>
    ///     Retrieves hatchery data for a specific user and group.
    ///     Collects information about available slots, max slots, and egg data for display.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="group">The hatchery group number (1-3).</param>
    /// <param name="patreonTier">The user's Patreon tier for slot access validation.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a tuple with:
    ///     - Number of available slots
    ///     - Maximum number of slots based on Patreon tier
    ///     - List of slot data (slot number, egg name, index in party, step counter)
    /// </returns>
    private async
        Task<(int AvailableSlots, int MaxSlots,
            List<(int SlotNumber, string EggName, int? PokemonNumber, int StepCounter)>
            )> GetHatcheryData(ulong userId, short group, PatreonTier patreonTier)
    {
        await using var db = await dbContext.GetConnectionAsync();

        // Get max slots based on Patreon tier
        var maxSlots = patreonTier switch
        {
            PatreonTier.Crystal => 10,
            PatreonTier.Gold => 7,
            _ => 6
        };

        // Get the hatchery data for this user and group
        var hatchery = await db.EggHatcheries
            .FirstOrDefaultAsync(h => h.UserId == userId && h.Group == group);

        if (hatchery == null)
        {
            // Create a new hatchery for this user and group
            hatchery = new EggHatchery
            {
                UserId = userId,
                Group = group
            };
            hatchery.Id = (ulong)await db.InsertWithInt64IdentityAsync(hatchery);
        }

        // Get all slots data
        var slotData = new List<(int SlotNumber, string EggName, int? PokemonNumber, int StepCounter)>();
        var availableSlots = 0;

        // Helper function to process each slot
        async Task ProcessSlot(int slotNumber, ulong? eggId)
        {
            if (eggId is > 0)
            {
                var pokemon = await db.UserPokemon
                    .FirstOrDefaultAsync(p => p.Id == eggId.Value);

                if (pokemon != null)
                {
                    // Find the Pokemon's index by querying the ownership table
                    var ownership = await db.GetTable<UserPokemonOwnership>()
                        .Where(o => o.UserId == userId && o.PokemonId == eggId.Value)
                        .FirstOrDefaultAsync();

                    var pokemonIndex = ownership != null ? (int)(ownership.Position + 1) : -1; // Convert to 1-based if found
                    slotData.Add((slotNumber, pokemon.Name, pokemonIndex, pokemon.Counter ?? 0));
                }
                else
                {
                    slotData.Add((slotNumber, "Empty", null, 0));
                    availableSlots++;
                }
            }
            else
            {
                slotData.Add((slotNumber, "Empty", null, 0));
                availableSlots++;
            }
        }

        // Process all slots
        await ProcessSlot(1, hatchery.Slot1);
        await ProcessSlot(2, hatchery.Slot2);
        await ProcessSlot(3, hatchery.Slot3);
        await ProcessSlot(4, hatchery.Slot4);
        await ProcessSlot(5, hatchery.Slot5);
        await ProcessSlot(6, hatchery.Slot6);

        // Process premium slots based on Patreon tier
        if (patreonTier >= PatreonTier.Gold) await ProcessSlot(7, hatchery.Slot7);

        if (patreonTier >= PatreonTier.Crystal)
        {
            await ProcessSlot(8, hatchery.Slot8);
            await ProcessSlot(9, hatchery.Slot9);
            await ProcessSlot(10, hatchery.Slot10);
        }

        return (availableSlots, maxSlots, slotData);
    }

    /// <summary>
    ///     Determines a user's Patreon tier from their database record.
    ///     Used for feature access validation like premium hatchery slots.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns the user's PatreonTier.
    ///     Returns PatreonTier.None if the user is not found or has no Patreon status.
    /// </returns>
    private async Task<PatreonTier> GetPatreonTier(ulong userId)
    {
        await using var db = await dbContext.GetConnectionAsync();

        var user = await db.GetTable<User>()
            .Where(u => u.UserId == userId)
            .Select(u => new { u.Patreon, u.PatreonOverride })
            .FirstOrDefaultAsync();

        if (user == null)
            return PatreonTier.None;

        var patreonStatus = !string.IsNullOrEmpty(user.PatreonOverride) ? user.PatreonOverride : user.Patreon;

        return patreonStatus switch
        {
            "Crystal Patreon" => PatreonTier.Crystal,
            "Gold Patreon" => PatreonTier.Gold,
            "Silver Patreon" => PatreonTier.Silver,
            _ => PatreonTier.None
        };
    }

    /// <summary>
    ///     Retrieves a user's username from their ID.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns the user's username,
    ///     or "Unknown User" if the user cannot be found.
    /// </returns>
    private async Task<string> GetUserNameAsync(ulong userId)
    {
        try
        {
            return await client.GetUsernameAsync(userId) ?? "Unknown User";
        }
        catch
        {
            return "Unknown User";
        }
    }
}

/// <summary>
///     Defines Patreon subscription tiers that determine feature access levels.
///     Higher tiers provide access to more slots in the egg hatchery.
/// </summary>
public enum PatreonTier
{
    /// <summary>
    ///     No Patreon subscription, access to standard features only.
    ///     Allows access to 6 hatchery slots per group.
    /// </summary>
    None = 0,

    /// <summary>
    ///     Silver tier Patreon subscription.
    ///     Allows access to 6 hatchery slots per group (same as None).
    /// </summary>
    Silver = 1,

    /// <summary>
    ///     Gold tier Patreon subscription.
    ///     Allows access to 7 hatchery slots per group.
    /// </summary>
    Gold = 2,

    /// <summary>
    ///     Crystal tier Patreon subscription.
    ///     Allows access to 10 hatchery slots per group.
    /// </summary>
    Crystal = 3
}