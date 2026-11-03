using System.Linq.Expressions;
using System.Text;
using EeveeCore.Database.Linq.Models.Pokemon;
using LinqToDB;
using LinqToDB.Async;

namespace EeveeCore.Modules.Parties.Services;

/// <summary>
///     Provides functionality for managing Pokémon parties.
///     Handles operations for creating, viewing, and modifying parties,
///     with support for saving multiple party configurations.
/// </summary>
/// <param name="dbProvider">The LinqToDB connection provider for accessing party and user data.</param>
/// <param name="botLog">The bot event log.</param>
public class PartyService(LinqToDbConnectionProvider dbProvider, BotLog botLog) : INService
{
    /// <summary>
    ///     Dictionary storing paged results for users viewing party data.
    ///     Maps user IDs to their current page state.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, (List<EmbedBuilder> Pages, int CurrentPage)> _pagedResults = new();

    /// <summary>
    ///     Creates a formatted table string from rows and headers.
    ///     Used for displaying party information in a structured format.
    /// </summary>
    /// <param name="rows">List of rows, where each row is a list of string values.</param>
    /// <param name="headers">List of column headers for the table.</param>
    /// <returns>A formatted string representing the table with proper alignment.</returns>
    public string FormatTable(List<List<string?>> rows, List<string> headers)
    {
        // Calculate column widths
        var columnWidths = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            columnWidths[i] = headers[i].Length;
            foreach (var row in rows)
                if (i < row.Count && row[i] != null)
                    columnWidths[i] = Math.Max(columnWidths[i], row[i].Length);
        }

        // Format headers
        var sb = new StringBuilder();
        sb.Append('|');
        for (var i = 0; i < headers.Count; i++)
            sb.Append(' ').Append(headers[i].PadRight(columnWidths[i])).Append(" |");
        sb.AppendLine();
        sb.Append('|');

        // Add header separator
        for (var i = 0; i < headers.Count; i++) sb.Append(new string('-', columnWidths[i] + 2)).Append('|');
        sb.AppendLine();

        // Format rows
        foreach (var row in rows)
        {
            sb.Append('|');
            for (var i = 0; i < headers.Count; i++)
            {
                var value = i < row.Count && row[i] != null ? row[i] : "";
                sb.Append(' ').Append(value.PadRight(columnWidths[i])).Append(" |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Retrieves party details for the setup menu.
    ///     Collects and formats information about the party's Pokémon for display.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="partyName">The name of the party to retrieve.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a tuple with:
    ///     - Description text for the embed
    ///     - Array of party Pokémon IDs
    ///     - Array of party Pokémon IDs formatted for display
    ///     - Array of Pokémon indices in the user's collection
    ///     - Array of Pokémon names
    ///     Returns null values if the party doesn't exist and can't be created.
    /// </returns>
    public async
        Task<(string Description, ulong[] Party, ulong[] PartyPokeIds, int[] PokemonIndices, string[] PokemonNames)>
        GetPartySetupData(ulong userId, string partyName)
    {
        await using var db = await dbProvider.GetConnectionAsync();

        // Initialize arrays for pokemon data
        var pokemonNames = new string?[6] { "None", "None", "None", "None", "None", "None" };
        var pokemonIndices = new int[6] { 0, 0, 0, 0, 0, 0 };
        var partyPokeIds = new ulong[6] { 0, 0, 0, 0, 0, 0 };

        // Get party data from database
        var party = await db.Parties
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Name == partyName);

        // If party doesn't exist, try to register it from user's current party
        if (party == null)
        {
            // Get current party from Parties table
            var currentParty = await db.Parties
                .FirstOrDefaultAsync(p => p.UserId == userId && p.IsCurrentParty);

            if (currentParty == null) return (null, null, null, null, null);

            // Create a new party save from current party
            party = new Party
            {
                UserId = userId,
                Name = partyName,
                Slot1 = currentParty.Slot1,
                Slot2 = currentParty.Slot2,
                Slot3 = currentParty.Slot3,
                Slot4 = currentParty.Slot4,
                Slot5 = currentParty.Slot5,
                Slot6 = currentParty.Slot6,
                IsCurrentParty = false,
                Quick = false
            };

            await db.InsertAsync(party);
        }

        // Extract party Pokemon IDs into array
        var partyPokemonIds = new ulong[6]
        {
            party.Slot1 ?? 0,
            party.Slot2 ?? 0,
            party.Slot3 ?? 0,
            party.Slot4 ?? 0,
            party.Slot5 ?? 0,
            party.Slot6 ?? 0
        };

        // Process each Pokemon in the party
        for (var i = 0; i < 6; i++)
        {
            if (partyPokemonIds[i] <= 0)
                continue;

            // Get the Pokemon's name
            var pokemon = await db.UserPokemon
                .FirstOrDefaultAsync(p => p.Id == partyPokemonIds[i]);

            if (pokemon != null)
            {
                pokemonNames[i] = pokemon.PokemonName;
                partyPokeIds[i] = pokemon.Id;

                // Find the Pokemon's index in the user's collection using the ownership table
                var ownership = await db.UserPokemonOwnerships
                    .FirstOrDefaultAsync(o => o.UserId == userId && o.PokemonId == pokemon.Id);

                if (ownership != null)
                    pokemonIndices[i] = (int)(ownership.Position + 1); // Add 1 for user-friendly indexing
            }
        }

        // Build the table for display
        var table = new List<List<string?>>
        {
            new() { "1", pokemonIndices[0] > 0 ? pokemonIndices[0].ToString() : "None", pokemonNames[0] },
            new() { "2", pokemonIndices[1] > 0 ? pokemonIndices[1].ToString() : "None", pokemonNames[1] },
            new() { "3", pokemonIndices[2] > 0 ? pokemonIndices[2].ToString() : "None", pokemonNames[2] },
            new() { "4", pokemonIndices[3] > 0 ? pokemonIndices[3].ToString() : "None", pokemonNames[3] },
            new() { "5", pokemonIndices[4] > 0 ? pokemonIndices[4].ToString() : "None", pokemonNames[4] },
            new() { "6", pokemonIndices[5] > 0 ? pokemonIndices[5].ToString() : "None", pokemonNames[5] }
        };

        var description = "Use the buttons below to set pokemon to a corresponding slot number in your party\n";
        description += $"**```\n{FormatTable(table, ["Slot #", "PokeID", "Name"])}\n```**";
        description += $"\nMenu Timeout: ~<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 122}:R>";

        return (description, partyPokemonIds, partyPokeIds, pokemonIndices, pokemonNames);
    }

    /// <summary>
    ///     Generates an embed displaying party information.
    ///     Shows Pokémon in each slot along with their collection indices.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="prefix">The server's command prefix.</param>
    /// <param name="partyName">The name of the party to view, or null to view the active party.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns an EmbedBuilder
    ///     with the party information, or null if the party doesn't exist.
    /// </returns>
    public async Task<EmbedBuilder> GetPartyViewEmbed(ulong userId, string prefix, string? partyName = null)
    {
        await using var db = await dbProvider.GetConnectionAsync();

        var embed = new EmbedBuilder()
            .WithTitle($"Party Info: {partyName}")
            .WithColor(new Color(0xEE, 0xE6, 0x47));

        // If no party name is provided, display the current active party
        if (string.IsNullOrEmpty(partyName))
        {
            // Get current party from Parties table
            var currentParty = await db.Parties
                .FirstOrDefaultAsync(p => p.UserId == userId && p.IsCurrentParty);

            if (currentParty == null) return null;

            var partySlots = new[]
            {
                currentParty.Slot1,
                currentParty.Slot2,
                currentParty.Slot3,
                currentParty.Slot4,
                currentParty.Slot5,
                currentParty.Slot6
            };

            for (var i = 0; i < partySlots.Length; i++)
            {
                var pokemonId = partySlots[i];
                var pokemonName = "None";
                int? pokemonIndex = null;

                if (pokemonId.HasValue && pokemonId.Value > 0)
                {
                    var pokemon = await db.UserPokemon
                        .FirstOrDefaultAsync(p => p.Id == pokemonId.Value);

                    if (pokemon != null)
                    {
                        pokemonName = pokemon.PokemonName;

                        // Find the Pokemon's index by querying the ownership table
                        var ownership = await db.UserPokemonOwnerships
                            .FirstOrDefaultAsync(o => o.UserId == userId && o.PokemonId == pokemonId.Value);

                        if (ownership != null)
                            pokemonIndex = (int)(ownership.Position + 1); // Convert to 1-based indexing
                    }
                }

                embed.AddField(
                    $"Slot {i + 1} Pokemon",
                    pokemonIndex.HasValue ? $"{pokemonName} [{pokemonIndex}]" : pokemonName
                );
            }
        }
        else
        {
            // Display the named saved party
            var party = await db.Parties
                .FirstOrDefaultAsync(p => p.UserId == userId && p.Name == partyName);

            if (party == null) return null;

            var partySlots = new[]
            {
                party.Slot1,
                party.Slot2,
                party.Slot3,
                party.Slot4,
                party.Slot5,
                party.Slot6
            };

            for (var i = 0; i < partySlots.Length; i++)
            {
                var pokemonId = partySlots[i];
                var pokemonName = "None";
                int? pokemonIndex = null;

                if (pokemonId is > 0)
                {
                    var pokemon = await db.UserPokemon
                        .FirstOrDefaultAsync(p => p.Id == pokemonId.Value);

                    if (pokemon != null)
                    {
                        pokemonName = pokemon.PokemonName;

                        // Find the Pokemon's index by querying the ownership table
                        var ownership = await db.UserPokemonOwnerships
                            .FirstOrDefaultAsync(o => o.UserId == userId && o.PokemonId == pokemonId.Value);

                        if (ownership != null)
                            pokemonIndex = (int)(ownership.Position + 1); // Convert to 1-based indexing
                    }
                }

                embed.AddField(
                    $"Slot {i + 1} Pokemon",
                    pokemonIndex.HasValue ? $"{pokemonName} [{pokemonIndex}]" : pokemonName
                );
            }
        }

        embed.WithFooter($"Use '{prefix}party load {partyName}' and then '{prefix}party add <slot_number>' to add a Pokemon");
        return embed;
    }

    /// <summary>
    ///     Adds a Pokémon to the active party.
    ///     Places the specified or currently selected Pokémon in the given slot.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="slot">The slot number to place the Pokémon in (1-6).</param>
    /// <param name="pokeIndex">
    ///     The index of the Pokémon in the user's collection, or 0 to use the currently selected Pokémon.
    /// </param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a tuple with
    ///     a success indicator and a result message.
    /// </returns>
    public async Task<(bool Success, string Message)> AddPokemonToParty(ulong userId, int slot, int pokeIndex = 0)
    {
        try
        {
            // Adjust slot for zero-based indexing
            var slotIndex = slot - 1;

            await using var db = await dbProvider.GetConnectionAsync();

            // Get or create current party
            var currentParty = await db.Parties
                .Where(p => p.UserId == userId && p.IsCurrentParty)
                .FirstOrDefaultAsync();

            if (currentParty == null)
            {
                // Create current party if it doesn't exist
                currentParty = new Party
                {
                    UserId = userId,
                    Name = "Current Party",
                    IsCurrentParty = true,
                    Quick = false
                };
                await db.InsertAsync(currentParty);
            }

            // Determine which Pokemon to add
            ulong pokemonId;
            if (pokeIndex > 0)
            {
                // Use the specified Pokemon index - convert from 1-based to 0-based
                var position = pokeIndex - 1;

                // Find the Pokemon using the ownership table
                var ownership = await db.UserPokemonOwnerships
                    .FirstOrDefaultAsync(o => o.UserId == userId && o.Position == (ulong)position);

                if (ownership == null)
                    return (false, "Invalid Pokemon ID. You don't have that many Pokemon.");

                pokemonId = ownership.PokemonId;
            }
            else
            {
                // Use the currently selected Pokemon
                var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
                if (user?.Selected == null || user.Selected.Value <= 0)
                    return (false, "You don't have a Pokemon selected. Select one first or specify an ID.");
                pokemonId = user.Selected.Value;
            }

            // Check if the Pokemon exists and isn't an egg
            var pokemon = await db.UserPokemon
                .FirstOrDefaultAsync(p => p.Id == pokemonId);

            if (pokemon == null) return (false, "You do not have that Pokemon!");

            if (pokemon.PokemonName.Equals("Egg", StringComparison.OrdinalIgnoreCase))
                return (false, "You cannot add an Egg to your party! Use `/hatchery` commands instead!");

            // Check if this Pokemon is already in the current party
            var slots = new[] { currentParty.Slot1, currentParty.Slot2, currentParty.Slot3, 
                               currentParty.Slot4, currentParty.Slot5, currentParty.Slot6 };
            if (slots.Any(s => s.HasValue && s.Value == pokemonId))
                return (false, "That Pokemon already occupies a Team Slot!");

            // Update the specific slot in the current party
            switch (slot)
            {
                case 1:
                    await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot1, pokemonId).UpdateAsync();
                    break;
                case 2:
                    await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot2, pokemonId).UpdateAsync();
                    break;
                case 3:
                    await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot3, pokemonId).UpdateAsync();
                    break;
                case 4:
                    await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot4, pokemonId).UpdateAsync();
                    break;
                case 5:
                    await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot5, pokemonId).UpdateAsync();
                    break;
                case 6:
                    await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot6, pokemonId).UpdateAsync();
                    break;
                default:
                    return (false, "Invalid slot number. Must be between 1 and 6.");
            }

            return (true, $"Your {pokemon.PokemonName} is now on your party, Slot number {slot}");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    /// <summary>
    ///     Swaps two slots of the active party in one update. Either slot may be empty.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="slotA">The first slot, from 1 to 6.</param>
    /// <param name="slotB">The second slot, from 1 to 6.</param>
    public async Task<(bool Success, string Message)> SwapPartySlots(ulong userId, int slotA, int slotB)
    {
        if (slotA is < 1 or > 6 || slotB is < 1 or > 6)
            return (false, "Slot must be between 1 and 6!");
        if (slotA == slotB)
            return (false, "Pick two different slots.");

        await using var db = await dbProvider.GetConnectionAsync();
        var party = await db.Parties.FirstOrDefaultAsync(p => p.UserId == userId && p.IsCurrentParty);
        if (party == null)
            return (false, "You do not have a party yet.");

        var slots = new[] { party.Slot1, party.Slot2, party.Slot3, party.Slot4, party.Slot5, party.Slot6 };
        var a = slots[slotA - 1] is > 0 ? slots[slotA - 1] : null;
        var b = slots[slotB - 1] is > 0 ? slots[slotB - 1] : null;
        if (a is null && b is null)
            return (false, "Both slots are empty.");

        // The update only applies while both slots still hold what was read
        var updated = await db.Parties
            .Where(p => p.PartyId == party.PartyId)
            .Where(SlotHolds(slotA, slots[slotA - 1]))
            .Where(SlotHolds(slotB, slots[slotB - 1]))
            .Set(SlotColumn(slotA), b)
            .Set(SlotColumn(slotB), a)
            .UpdateAsync();

        return updated == 0
            ? (false, "Your party changed while swapping. Please try again.")
            : (true, $"Swapped party slots {slotA} and {slotB}.");
    }

    private static Expression<Func<Party, ulong?>> SlotColumn(int slot) => slot switch
    {
        1 => p => p.Slot1,
        2 => p => p.Slot2,
        3 => p => p.Slot3,
        4 => p => p.Slot4,
        5 => p => p.Slot5,
        _ => p => p.Slot6
    };

    private static Expression<Func<Party, bool>> SlotHolds(int slot, ulong? value)
    {
        var column = SlotColumn(slot);
        return Expression.Lambda<Func<Party, bool>>(
            Expression.Equal(column.Body, Expression.Constant(value, typeof(ulong?))), column.Parameters);
    }

    /// <summary>
    ///     Removes a Pokémon from the active party.
    ///     Clears the specified slot in the user's party.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="slot">The slot number to clear (1-6).</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a tuple with
    ///     a success indicator and a result message.
    /// </returns>
    public async Task<(bool Success, string Message)> RemovePokemonFromParty(ulong userId, int slot)
    {
        await using var db = await dbProvider.GetConnectionAsync();

        // Get or create current party
        var currentParty = await db.Parties
            .Where(p => p.UserId == userId && p.IsCurrentParty)
            .FirstOrDefaultAsync();

        if (currentParty == null) return (false, "You have not started!\nStart with `/start` first!");

        // Get the Pokemon to remove from the specified slot
        var slots = new[] { currentParty.Slot1, currentParty.Slot2, currentParty.Slot3, 
                           currentParty.Slot4, currentParty.Slot5, currentParty.Slot6 };
        var pokemonId = slots[slot - 1];

        if (!pokemonId.HasValue || pokemonId.Value == 0)
            return (false, "No Pokemon in that slot.");

        var pokemon = await db.UserPokemon
            .FirstOrDefaultAsync(p => p.Id == pokemonId.Value);

        // Clear the specific slot in the current party
        switch (slot)
        {
            case 1:
                await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot1, (ulong?)null).UpdateAsync();
                break;
            case 2:
                await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot2, (ulong?)null).UpdateAsync();
                break;
            case 3:
                await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot3, (ulong?)null).UpdateAsync();
                break;
            case 4:
                await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot4, (ulong?)null).UpdateAsync();
                break;
            case 5:
                await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot5, (ulong?)null).UpdateAsync();
                break;
            case 6:
                await db.Parties.Where(p => p.PartyId == currentParty.PartyId).Set(p => p.Slot6, (ulong?)null).UpdateAsync();
                break;
            default:
                return (false, "Invalid slot number. Must be between 1 and 6.");
        }

        return (true, $"You have successfully removed {pokemon?.PokemonName ?? "Pokemon"} from Pokemon Number {slot} In your Party!");
    }

    /// <summary>
    ///     Registers a party with a name.
    ///     Creates a new saved party or updates an existing one using the current active party.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="partyName">The name to assign to the party.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a tuple with
    ///     a success indicator and a result message.
    /// </returns>
    public async Task<(bool Success, string Message)> RegisterParty(ulong userId, string partyName)
    {
        await using var db = await dbProvider.GetConnectionAsync();

        // Get current party from Parties table
        var currentParty = await db.Parties
            .FirstOrDefaultAsync(p => p.UserId == userId && p.IsCurrentParty);

        if (currentParty == null) return (false, "You have not started!\nStart with `/start` first.");

        // Check if a party with this name already exists
        var existingParty = await db.Parties
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Name == partyName);

        if (existingParty != null)
        {
            // Update existing party with current party data
            await db.Parties.Where(p => p.UserId == userId && p.Name == partyName)
                .Set(p => p.Slot1, currentParty.Slot1)
                .Set(p => p.Slot2, currentParty.Slot2)
                .Set(p => p.Slot3, currentParty.Slot3)
                .Set(p => p.Slot4, currentParty.Slot4)
                .Set(p => p.Slot5, currentParty.Slot5)
                .Set(p => p.Slot6, currentParty.Slot6)
                .UpdateAsync();
            return (true, $"Successfully updated party save {partyName}");
        }

        // Create new party from current party
        var newParty = new Party
        {
            UserId = userId,
            Name = partyName,
            Slot1 = currentParty.Slot1,
            Slot2 = currentParty.Slot2,
            Slot3 = currentParty.Slot3,
            Slot4 = currentParty.Slot4,
            Slot5 = currentParty.Slot5,
            Slot6 = currentParty.Slot6,
            IsCurrentParty = false,
            Quick = false
        };

        await db.InsertAsync(newParty);
        return (true, "Successfully created a new party save.");
    }

    /// <summary>
    ///     Checks if a party with the specified name exists for the user.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="partyName">The name of the party to check for.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns true if the party exists,
    ///     or false if it doesn't.
    /// </returns>
    public async Task<bool> DoesPartyExist(ulong userId, string partyName)
    {
        await using var db = await dbProvider.GetConnectionAsync();

        return await db.Parties
            .AnyAsync(p => p.UserId == userId && p.Name == partyName);
    }

    /// <summary>
    ///     Deregisters a party.
    ///     Removes a saved party from the database.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="partyName">The name of the party to deregister.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a tuple with
    ///     a success indicator and a result message.
    /// </returns>
    public async Task<(bool Success, string Message)> DeregisterParty(ulong userId, string partyName)
    {
        await using var db = await dbProvider.GetConnectionAsync();

        // Find the party
        var party = await db.Parties
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Name == partyName);

        if (party == null) return (false, $"You do not have a party with the name `{partyName}`.");

        // Remove the party
        await db.DeleteAsync(party);

        return (true, $"Successfully deregistered party `{partyName}`.");
    }

    /// <summary>
    ///     Loads a saved party into the user's active party.
    ///     Replaces the current active party with the saved configuration.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="partyName">The name of the party to load.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a tuple with
    ///     a success indicator and a result message.
    /// </returns>
    public async Task<(bool Success, string Message)> LoadParty(ulong userId, string partyName)
    {
        try
        {
            await using var db = await dbProvider.GetConnectionAsync();

            // Find the saved party to load
            var savedParty = await db.Parties
                .FirstOrDefaultAsync(p => p.UserId == userId && p.Name == partyName);

            if (savedParty == null) return (false, "You don't have a party registered with that name.");

            // Get or create current party
            var currentParty = await db.Parties
                .FirstOrDefaultAsync(p => p.UserId == userId && p.IsCurrentParty);

            if (currentParty == null)
            {
                // Create current party if it doesn't exist
                currentParty = new Party
                {
                    UserId = userId,
                    Name = "Current Party",
                    IsCurrentParty = true,
                    Quick = false
                };
                await db.InsertAsync(currentParty);
            }

            // Load saved party data into current party
            await db.Parties
                .Where(p => p.PartyId == currentParty.PartyId)
                .Set(p => p.Slot1, savedParty.Slot1)
                .Set(p => p.Slot2, savedParty.Slot2)
                .Set(p => p.Slot3, savedParty.Slot3)
                .Set(p => p.Slot4, savedParty.Slot4)
                .Set(p => p.Slot5, savedParty.Slot5)
                .Set(p => p.Slot6, savedParty.Slot6)
                .UpdateAsync();

            return (true, "Successfully, updated current party from saved data.");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    /// <summary>
    ///     Retrieves a list of saved parties for a user.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a list of party names.
    ///     Returns an empty list if the user has no saved parties.
    /// </returns>
    public async Task<List<string>> GetUserParties(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();

        var parties = await db.Parties
            .Where(p => p.UserId == userId && !p.IsCurrentParty)
            .Select(p => p.Name)
            .ToListAsync();

        return parties;
    }

    /// <summary>
    ///     Adds a Pokémon to a specific slot in a saved party.
    ///     Creates the party if it doesn't exist.
    /// </summary>
    /// <param name="userId">The Discord ID of the user.</param>
    /// <param name="slot">The slot number to place the Pokémon in (1-6).</param>
    /// <param name="pokeIndex">The index of the Pokémon in the user's collection.</param>
    /// <param name="partyName">The name of the party to modify.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a tuple with
    ///     a success indicator and a result message.
    /// </returns>
    public async Task<(bool Success, string Message)> AddPokemonToPartySlot(ulong userId, int slot, int pokeIndex,
        string partyName)
    {
        try
        {
            await using var db = await dbProvider.GetConnectionAsync();

            // Check if the user exists
            var user = await db.Users
                .FirstOrDefaultAsync(u => u.UserId == userId);

            if (user == null)
                return (false, "You have not started!\nStart with `/start` first!");

            // Find the Pokemon using the ownership table instead of array indexing
            var position = pokeIndex - 1; // Convert from 1-based to 0-based

            var ownership = await db.UserPokemonOwnerships
                .FirstOrDefaultAsync(o => o.UserId == userId && o.Position == (ulong)position);

            if (ownership == null)
                return (false, "Invalid ID Provided\nPlease try again.");

            var pokemonId = ownership.PokemonId;

            // Check if the Pokemon exists in the user's collection
            var pokemon = await db.UserPokemon
                .FirstOrDefaultAsync(p => p.Id == pokemonId);

            if (pokemon == null) return (false, "Invalid ID Provided\nPlease try again.");

            // Find the party
            var party = await db.Parties
                .FirstOrDefaultAsync(p => p.UserId == userId && p.Name == partyName);

            if (party == null)
            {
                // If the party doesn't exist, create it from the current user party
                var result = await RegisterParty(userId, partyName);
                if (!result.Success) return result;

                party = await db.Parties
                    .FirstOrDefaultAsync(p => p.UserId == userId && p.Name == partyName);
            }

            // Check if the Pokemon is already in the party
            var partySlots = new[]
            {
                party.Slot1,
                party.Slot2,
                party.Slot3,
                party.Slot4,
                party.Slot5,
                party.Slot6
            };

            if (partySlots.Any(s => s.HasValue && s.Value == pokemonId))
                return (false, "This pokemon already occupies a slot in your party!");

            // Update the slot using LinqToDB
            var updateQuery = db.Parties.Where(p => p.UserId == userId && p.Name == partyName);
            
            switch (slot)
            {
                case 1:
                    await updateQuery.Set(p => p.Slot1, pokemonId).UpdateAsync();
                    break;
                case 2:
                    await updateQuery.Set(p => p.Slot2, pokemonId).UpdateAsync();
                    break;
                case 3:
                    await updateQuery.Set(p => p.Slot3, pokemonId).UpdateAsync();
                    break;
                case 4:
                    await updateQuery.Set(p => p.Slot4, pokemonId).UpdateAsync();
                    break;
                case 5:
                    await updateQuery.Set(p => p.Slot5, pokemonId).UpdateAsync();
                    break;
                case 6:
                    await updateQuery.Set(p => p.Slot6, pokemonId).UpdateAsync();
                    break;
                default:
                    return (false, "Invalid slot number.");
            }
            return (true, $"Successfully added {pokemon.PokemonName} to slot {slot} in party {partyName}");
        }
        catch (Exception ex)
        {
            await botLog.SendAsync($"**__ERROR OCCURRED__**: ```{ex.Message}\n{ex.StackTrace}```");

            return (false, "Something went wrong\nPlease try running this command again.");
        }
    }

    /// <summary>
    ///     Empties a slot in a saved party.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="slot">The slot number to clear (1-6).</param>
    /// <param name="partyName">The name of the saved party.</param>
    /// <returns>A tuple with a success indicator and a result message.</returns>
    public async Task<(bool Success, string Message)> ClearSavedPartySlot(ulong userId, int slot, string partyName)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var query = db.Parties.Where(p => p.UserId == userId && p.Name == partyName && !p.IsCurrentParty);
        if (!await query.AnyAsync())
            return (false, $"You do not have a party with the name `{partyName}`.");

        var updated = slot switch
        {
            1 => query.Set(p => p.Slot1, (ulong?)null),
            2 => query.Set(p => p.Slot2, (ulong?)null),
            3 => query.Set(p => p.Slot3, (ulong?)null),
            4 => query.Set(p => p.Slot4, (ulong?)null),
            5 => query.Set(p => p.Slot5, (ulong?)null),
            6 => query.Set(p => p.Slot6, (ulong?)null),
            _ => null
        };
        if (updated is null)
            return (false, "Invalid slot number.");

        await updated.UpdateAsync();
        return (true, $"Cleared slot {slot} in party {partyName}");
    }

    /// <summary>
    ///     Stores a paged result for a user.
    ///     Used for maintaining pagination state when browsing party information.
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
    ///     Used when a user navigates through party pages.
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
}

/// <summary>
///     Result class for service operations.
///     Provides a standardized way to return success/failure status and messages.
/// </summary>
public class ServiceResult
{
    /// <summary>
    ///     Indicates whether the operation was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    ///     Contains a descriptive message about the result of the operation.
    /// </summary>
    public string Message { get; set; }

    /// <summary>
    ///     Creates a success result with the specified message.
    /// </summary>
    /// <param name="message">The success message.</param>
    /// <returns>A ServiceResult indicating success.</returns>
    public static ServiceResult FromSuccess(string message)
    {
        return new ServiceResult { Success = true, Message = message };
    }

    /// <summary>
    ///     Creates an error result with the specified message.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>A ServiceResult indicating failure.</returns>
    public static ServiceResult FromError(string message)
    {
        return new ServiceResult { Success = false, Message = message };
    }
}