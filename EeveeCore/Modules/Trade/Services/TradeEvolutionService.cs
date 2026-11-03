using EeveeCore.Services.Impl;
using LinqToDB;
using LinqToDB.Async;
using MongoDB.Driver;
using Serilog;

namespace EeveeCore.Modules.Trade.Services;

/// <summary>
///     Service for handling Pokemon evolution triggered by trading.
///     Checks for trade evolution conditions and processes evolution after trades.
/// </summary>
public class TradeEvolutionService : INService
{
    private readonly LinqToDbConnectionProvider _context;
    private readonly IMongoService _mongoService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TradeEvolutionService" /> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="mongoService">The MongoDB service for Pokemon data.</param>
    public TradeEvolutionService(LinqToDbConnectionProvider context, IMongoService mongoService)
    {
        _context = context;
        _mongoService = mongoService;
    }

    /// <summary>
    ///     Checks if a Pokemon can evolve via trade and returns the evolution name.
    /// </summary>
    /// <param name="pokemon">The Pokemon to check for trade evolution.</param>
    /// <returns>The name of the evolved Pokemon if evolution occurs, null otherwise.</returns>
    public async Task<string?> CheckTradeEvolution(Database.Linq.Models.Pokemon.Pokemon pokemon)
    {
        try
        {
            var pokemonName = pokemon.PokemonName.ToLower();
            
            // Get Pokemon species data from MongoDB
            var pokemonSpecies = await _mongoService.PFile
                .Find(p => p.Identifier == pokemonName)
                .FirstOrDefaultAsync();

            if (pokemonSpecies == null)
            {
                return null;
            }

            // Get all possible evolutions for this Pokemon (Pokemon that evolve FROM this one)
            var evolutions = await _mongoService.PFile
                .Find(p => p.EvolvesFromSpeciesId == pokemonSpecies.PokemonId)
                .ToListAsync();

            foreach (var evolution in evolutions)
            {
                // Check if this evolution is triggered by trade
                var evolutionTrigger = await _mongoService.Evolution
                    .Find(e => e.EvolvedSpeciesId == evolution.PokemonId)
                    .FirstOrDefaultAsync();

                if (evolutionTrigger == null)
                    continue;

                // Check for held item requirement
                if (evolutionTrigger.HeldItemId != null)
                {
                    // Get the held item name
                    var requiredItem = await _mongoService.Items
                        .Find(i => i.ItemId == evolutionTrigger.HeldItemId)
                        .FirstOrDefaultAsync();

                    if (requiredItem != null && !string.IsNullOrEmpty(pokemon.HeldItem))
                    {
                        if (pokemon.HeldItem.Equals(requiredItem.Identifier, StringComparison.OrdinalIgnoreCase))
                        {
                            // Evolution with held item
                            await using var db = await _context.GetConnectionAsync();
                            await db.UserPokemon
                                .Where(p => p.Id == pokemon.Id)
                                .Set(p => p.PokemonName, evolution.Identifier)
                                .UpdateAsync();
                            return evolution.Identifier;
                        }
                    }
                }
                else if (evolutionTrigger.EvolutionTriggerId == 2) // Trade trigger ID is 2
                {
                    // Simple trade evolution (no item required)
                    await using var db = await _context.GetConnectionAsync();
                    await db.UserPokemon
                        .Where(p => p.Id == pokemon.Id)
                        .Set(p => p.PokemonName, evolution.Identifier)
                        .UpdateAsync();
                    return evolution.Identifier;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Checks and processes trade evolution for a Pokemon that was just traded.
    /// </summary>
    /// <param name="pokemonId">The ID of the Pokemon to check for evolution.</param>
    /// <param name="newOwnerId">The new owner of the Pokemon.</param>
    /// <param name="channel">The channel to send evolution messages to.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CheckAndProcessTradeEvolutionAsync(ulong pokemonId, ulong newOwnerId, Channel channel)
    {
        try
        {
            await using var db = await _context.GetConnectionAsync();
            var pokemon = await db.UserPokemon.FirstOrDefaultAsync(p => p.Id == pokemonId);
            if (pokemon == null)
            {
                return;
            }

            var pokemonName = pokemon.PokemonName.ToLower();
            
            // Get Pokemon species data from MongoDB
            var pokemonSpecies = await _mongoService.PFile
                .Find(p => p.Identifier == pokemonName)
                .FirstOrDefaultAsync();

            if (pokemonSpecies == null)
            {
                return;
            }

            // Get evolution data
            var evolutionData = await _mongoService.Evolution
                .Find(e => e.EvolvedSpeciesId == pokemonSpecies.EvolvesFromSpeciesId)
                .ToListAsync();

            // Find trade evolutions
            foreach (var evolution in evolutionData)
            {
                // Check for trade evolution trigger (evolution_trigger_id == 2)
                if (evolution.EvolutionTriggerId == 2)
                {
                    await ProcessTradeEvolution(pokemon, evolution, newOwnerId, channel);
                }
                // Check for held item trade evolution
                else if (evolution.HeldItemId.HasValue)
                {
                    await ProcessHeldItemTradeEvolution(pokemon, evolution, newOwnerId, channel);
                }
            }
        }
        catch (Exception ex)
        {
            // Evolution errors should not break the trade, just log and continue
            Log.Information($"Error processing trade evolution for Pokemon {pokemonId}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Processes a simple trade evolution (no held item required).
    /// </summary>
    /// <param name="pokemon">The Pokemon to evolve.</param>
    /// <param name="evolution">The evolution data.</param>
    /// <param name="newOwnerId">The new owner of the Pokemon.</param>
    /// <param name="channel">The channel to send messages to.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ProcessTradeEvolution(Database.Linq.Models.Pokemon.Pokemon pokemon,
        Database.Models.Mongo.Pokemon.Evolution evolution,
        ulong newOwnerId,
        Channel channel)
    {
        // Get the evolved species name
        var evolvedSpecies = await _mongoService.PFile
            .Find(p => p.PokemonId == evolution.EvolvedSpeciesId)
            .FirstOrDefaultAsync();

        if (evolvedSpecies == null)
        {
            return;
        }

        var oldName = pokemon.PokemonName;
        var newName = CapitalizeName(evolvedSpecies.Identifier);

        // Update Pokemon name
        await using var db = await _context.GetConnectionAsync();
        await db.UserPokemon
            .Where(p => p.Id == pokemon.Id)
            .Set(p => p.PokemonName, newName)
            .UpdateAsync();

        // Send evolution message
        var embed = new EmbedBuilder()
            .WithTitle("🎉 Congratulations!!!")
            .WithDescription($"<@{newOwnerId}>, your {oldName} has evolved into {newName}!")
            .WithColor(Color.Gold)
            .Build();

        await channel.SendMessageAsync(embeds: [embed]);
    }

    /// <summary>
    ///     Processes a held item trade evolution.
    /// </summary>
    /// <param name="pokemon">The Pokemon to evolve.</param>
    /// <param name="evolution">The evolution data.</param>
    /// <param name="newOwnerId">The new owner of the Pokemon.</param>
    /// <param name="channel">The channel to send messages to.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ProcessHeldItemTradeEvolution(Database.Linq.Models.Pokemon.Pokemon pokemon,
        Database.Models.Mongo.Pokemon.Evolution evolution,
        ulong newOwnerId,
        Channel channel)
    {
        if (!evolution.HeldItemId.HasValue)
        {
            return;
        }

        // Get the required held item
        var requiredItem = await _mongoService.Items
            .Find(i => i.ItemId == evolution.HeldItemId.Value)
            .FirstOrDefaultAsync();

        if (requiredItem == null)
        {
            return;
        }

        // Check if Pokemon is holding the required item
        var currentHeldItem = pokemon.HeldItem?.ToLower();
        var requiredItemName = requiredItem.Identifier.ToLower();

        if (currentHeldItem != requiredItemName)
        {
            return;
        }

        // Get the evolved species name
        var evolvedSpecies = await _mongoService.PFile
            .Find(p => p.PokemonId == evolution.EvolvedSpeciesId)
            .FirstOrDefaultAsync();

        if (evolvedSpecies == null)
        {
            return;
        }

        var oldName = pokemon.PokemonName;
        var newName = CapitalizeName(evolvedSpecies.Identifier);

        // Update Pokemon name
        await using var db = await _context.GetConnectionAsync();
        await db.UserPokemon
            .Where(p => p.Id == pokemon.Id)
            .Set(p => p.PokemonName, newName)
            .UpdateAsync();

        // Send evolution message
        var embed = new EmbedBuilder()
            .WithTitle("🎉 Congratulations!!!")
            .WithDescription($"<@{newOwnerId}>, your {oldName} has evolved into {newName}!")
            .WithColor(Color.Gold)
            .Build();

        await channel.SendMessageAsync(embeds: [embed]);
    }

    /// <summary>
    ///     Capitalizes the first letter of a Pokemon name.
    /// </summary>
    /// <param name="name">The name to capitalize.</param>
    /// <returns>The capitalized name.</returns>
    private static string CapitalizeName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        return char.ToUpper(name[0]) + name[1..];
    }
}