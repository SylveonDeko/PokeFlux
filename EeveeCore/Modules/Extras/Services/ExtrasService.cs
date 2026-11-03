using EeveeCore.Database.Models.Mongo.Pokemon;
using EeveeCore.Modules.Pokemon.Services;
using EeveeCore.Services.Impl;
using LinqToDB;
using LinqToDB.Async;
using MongoDB.Driver;

namespace EeveeCore.Modules.Extras.Services;

/// <summary>
///     Service implementation for the Extras module
/// </summary>
public class ExtrasService : INService
{
    private static readonly string[] BlockedNicknameWords =
        ["@here", "@everyone", "http", "nigger", "nigga", "gay", "fag", "kike", "jew", "faggot"];

    private readonly IMongoService _mongoService;
    private readonly LinqToDbConnectionProvider _dbProvider;
    private readonly PokemonService _pokemonService;
    private readonly SemaphoreSlim _namesLock = new(1, 1);
    private List<string>? _pokemonNames;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ExtrasService" /> class.
    /// </summary>
    /// <param name="mongoService">The MongoDB service for database operations.</param>
    /// <param name="dbProvider">The database connection provider.</param>
    /// <param name="pokemonService">The Pokémon service, for evolutions.</param>
    public ExtrasService(
        IMongoService mongoService,
        LinqToDbConnectionProvider dbProvider,
        PokemonService pokemonService)
    {
        _mongoService = mongoService;
        _dbProvider = dbProvider;
        _pokemonService = pokemonService;
    }

    /// <summary>
    ///     Looks up the type, power, accuracy, PP and damage class of moves.
    /// </summary>
    /// <param name="moves">The move identifiers.</param>
    /// <returns>The details of each move that was found, in the order given.</returns>
    public async Task<List<MoveDetails>> GetMoveDetailsAsync(IEnumerable<string> moves)
    {
        var names = moves.Select(m => m.ToLower()).ToList();
        var lookups = names
            .SelectMany(m => m.Contains('-') ? new[] { m, m.Split('-')[0] } : [m])
            .Distinct()
            .ToList();
        var moveInfos = (await _mongoService.Moves.Find(m => lookups.Contains(m.Identifier)).ToListAsync())
            .GroupBy(m => m.Identifier)
            .ToDictionary(g => g.Key, g => g.First());
        var typeNames = (await _mongoService.Types.Find(_ => true).ToListAsync())
            .GroupBy(t => t.TypeId)
            .ToDictionary(g => g.Key, g => g.First().Identifier.Capitalize() ?? "Unknown");

        var details = new List<MoveDetails>();
        foreach (var name in names)
        {
            if (!moveInfos.TryGetValue(name, out var move) &&
                !(name.Contains('-') && moveInfos.TryGetValue(name.Split('-')[0], out move)))
                continue;

            details.Add(new MoveDetails(name, typeNames.GetValueOrDefault(move.TypeId, "Unknown"), move.Power,
                move.Accuracy, move.PP, move.DamageClassId switch
                {
                    1 => "Status",
                    2 => "Physical",
                    _ => "Special"
                }));
        }

        return details;
    }

    /// <summary>
    ///     Teaches one of a user's Pokémon a move it can learn, in one of its four slots.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="pokemonId">The ID of the Pokémon.</param>
    /// <param name="slot">The slot, from 1 to 4.</param>
    /// <param name="move">The move to learn.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    public async Task<(bool Success, string Message)> LearnMoveAsync(ulong userId, ulong pokemonId, int slot,
        string move)
    {
        if (slot is < 1 or > 4)
            return (false, "The slot must be 1, 2, 3 or 4.");

        await using var db = await _dbProvider.GetConnectionAsync();
        var pokemon = await (
            from ownership in db.UserPokemonOwnerships
            join p in db.UserPokemon on ownership.PokemonId equals p.Id
            where ownership.UserId == userId && p.Id == pokemonId
            select p).FirstOrDefaultAsync();
        if (pokemon == null)
            return (false, "You do not have that Pokemon!");

        var moves = await GetMoves(pokemon.PokemonName.ToLower());
        if (moves == null)
            return (false, "That pokemon cannot learn any moves! You might need to `/deform` it first.");

        var resolved = move.Trim().Replace(" ", "-").ToLower();
        if (!moves.Contains(resolved))
        {
            var guess = FindClosest(resolved, moves);
            return (false, $"Your {pokemon.PokemonName} can not learn that Move" +
                           (guess is null ? "" : $"\nDid you mean `{guess}`?"));
        }

        var currentMoves = pokemon.Moves.ToList();
        while (currentMoves.Count < 4)
            currentMoves.Add("tackle");
        currentMoves[slot - 1] = resolved;

        await db.UserPokemon.Where(p => p.Id == pokemon.Id)
            .Set(p => p.Moves, currentMoves.ToArray())
            .UpdateAsync();

        await _pokemonService.TryEvolve(pokemon.Id);
        return (true, $"You have successfully learned {resolved} as your slot {slot} move");
    }

    /// <summary>
    ///     Sets or resets the nickname of one of a user's Pokémon.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="pokemonId">The ID of the Pokémon.</param>
    /// <param name="nickname">The new nickname, or null or "None" to reset it.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    public async Task<(bool Success, string Message)> SetNicknameAsync(ulong userId, ulong pokemonId,
        string? nickname)
    {
        var nick = string.IsNullOrWhiteSpace(nickname) ? "None" : nickname;
        if (nick.Length > 150)
            return (false, "Nickname is too long!");

        if (BlockedNicknameWords.Any(nick.Contains))
            return (false, "Nope.");

        await using var db = await _dbProvider.GetConnectionAsync();
        if (!await db.UserPokemonOwnerships.AnyAsync(o => o.UserId == userId && o.PokemonId == pokemonId))
            return (false, "You do not have that Pokemon!");

        await db.UserPokemon.Where(p => p.Id == pokemonId)
            .Set(p => p.Nickname, nick)
            .UpdateAsync();

        return (true, nick == "None"
            ? "Successfully reset Pokemon nickname."
            : $"Successfully changed Pokemon nickname to {nick}.");
    }

    /// <summary>
    ///     Gets the complete list of moves available to a specific Pokémon.
    /// </summary>
    /// <param name="pokemonName">The name of the Pokémon to get moves for.</param>
    /// <returns>A list of move names the Pokémon can learn, or null if the Pokémon doesn't exist.</returns>
    public async Task<List<string>> GetMoves(string pokemonName)
    {
        if (pokemonName == "smeargle")
        {
            // Moves which are not coded in the bot
            var uncoded = new[]
            {
                266, 270, 476, 495, 502, 511, 597, 602, 603, 607, 622, 623, 624, 625, 626, 627, 628, 629, 630, 631, 632,
                633, 634, 635, 636, 637, 638, 639, 640, 641, 642, 643, 644, 645, 646, 647, 648, 649, 650, 651, 652, 653,
                654, 655, 656, 657, 658, 671, 695, 696, 697, 698, 699, 700, 701, 702, 703, 719, 723, 724, 725, 726, 727,
                728, 811, 10001, 10002, 10003, 10004, 10005, 10006, 10007, 10008, 10009, 10010, 10011, 10012, 10013,
                10014, 10015, 10016, 10017, 10018
            };

            var allMoves = await _mongoService.Moves
                .Find(Builders<Move>.Filter.Nin(m => m.MoveId, uncoded))
                .ToListAsync();

            return allMoves.Select(m => m.Identifier).ToList();
        }

        var moves = await _mongoService.PokemonMoves.Find(p => p.Pokemon == pokemonName).FirstOrDefaultAsync();
        if (moves == null)
            return null;

        return moves.Moves.OrderBy(m => m).ToList();
    }

    /// <summary>
    ///     Gets the identifiers of every Pokémon, loaded once from the Pokémon files.
    /// </summary>
    /// <returns>The lowercase identifiers.</returns>
    public async Task<IReadOnlyList<string>> GetPokemonNames()
    {
        if (_pokemonNames is not null)
            return _pokemonNames;

        await _namesLock.WaitAsync();
        try
        {
            if (_pokemonNames is null)
            {
                var files = await _mongoService.PFile.Find(_ => true).ToListAsync();
                _pokemonNames = files
                    .Where(f => !string.IsNullOrEmpty(f.Identifier))
                    .Select(f => f.Identifier.ToLower())
                    .Distinct()
                    .ToList();
            }

            return _pokemonNames;
        }
        finally
        {
            _namesLock.Release();
        }
    }

    /// <summary>
    ///     Finds the option closest to what a player typed.
    /// </summary>
    /// <param name="input">What the player typed.</param>
    /// <param name="options">The valid options.</param>
    /// <returns>The closest option, or null when there are none.</returns>
    public static string? FindClosest(string input, IEnumerable<string> options)
    {
        var lowered = input.ToLower();
        return options
            .Select(o => (Option: o, Distance: lowered.LevenshteinDistance(o.ToLower())))
            .OrderBy(x => x.Distance)
            .Select(x => x.Option)
            .FirstOrDefault();
    }

    /// <summary>
    ///     Builds a progress bar from the bar emojis.
    /// </summary>
    /// <param name="value">The current value.</param>
    /// <param name="max">The value of a full bar.</param>
    /// <param name="length">The number of pieces.</param>
    /// <returns>The bar.</returns>
    public static string ProgressBar(double value, double max, int length = 10)
    {
        var filled = max <= 0 ? 0 : (int)Math.Clamp(Math.Floor(value / max * length), 0, length);
        var bar = new System.Text.StringBuilder();
        for (var i = 0; i < length; i++)
        {
            var full = i < filled;
            if (i == 0)
                bar.Append(full ? Emojis.Bar.Start : Emojis.Bar.EmptyStart);
            else if (i == length - 1)
                bar.Append(full ? Emojis.Bar.End : Emojis.Bar.EmptyEnd);
            else
                bar.Append(full ? Emojis.Bar.Middle : Emojis.Bar.EmptyMiddle);
        }

        return bar.ToString();
    }

    /// <summary>
    ///     Determines whether a Pokémon is in a formed state.
    /// </summary>
    /// <param name="pokemonName">The name of the Pokémon to check.</param>
    /// <returns>True if the Pokémon is in a formed state, false otherwise.</returns>
    public bool IsFormed(string pokemonName)
    {
        if (string.IsNullOrEmpty(pokemonName))
            return false;

        return pokemonName.EndsWith("-mega") ||
               pokemonName.EndsWith("-x") ||
               pokemonName.EndsWith("-y") ||
               pokemonName.EndsWith("-origin") ||
               pokemonName.EndsWith("-10") ||
               pokemonName.EndsWith("-complete") ||
               pokemonName.EndsWith("-ultra") ||
               pokemonName.EndsWith("-crowned") ||
               pokemonName.EndsWith("-eternamax") ||
               pokemonName.EndsWith("-blade");
    }

    /// <summary>
    ///     Creates a visual health bar representation for Discord messages.
    /// </summary>
    /// <param name="maxHealth">The maximum health value.</param>
    /// <param name="health">The current health value.</param>
    /// <param name="healthDashes">The number of dashes to use in the health bar.</param>
    /// <returns>A string containing Discord emoji that display as a health bar.</returns>
    public string DoHealth(int maxHealth, int health, int healthDashes = 10)
    {
        var dashConvert = maxHealth / healthDashes;
        var currentDashes = health / dashConvert;
        var remainingHealth = healthDashes - currentDashes;
        var cur = $"{Math.Round((double)health)}/{maxHealth}";

        var healthDisplay = string.Join("", Enumerable.Repeat("▰", currentDashes));
        var remainingDisplay = string.Join("", Enumerable.Repeat("▱", remainingHealth));
        var percent = (int)Math.Floor((double)health / maxHealth * 100);
        if (percent < 1)
            percent = 0;

        var result = "";
        var emojis = new Dictionary<bool, Dictionary<string, string>>
        {
            [true] = new()
            {
                ["left"] = Emojis.Bar.Start,
                ["center"] = Emojis.Bar.Middle,
                ["right"] = Emojis.Bar.End
            },
            [false] = new()
            {
                ["left"] = Emojis.Bar.EmptyStart,
                ["center"] = Emojis.Bar.EmptyMiddle,
                ["right"] = Emojis.Bar.EmptyEnd
            }
        };

        for (var i = 0; i < healthDashes; i++)
        {
            var cur2 = emojis[i < currentDashes];
            if (i == 0)
                result += cur2["left"];
            else if (i == healthDashes - 1)
                result += cur2["right"];
            else
                result += cur2["center"];
        }

        return result;
    }

    /// <summary>
    ///     Calculates breeding success multiplier based on level.
    /// </summary>
    /// <param name="level">The level to calculate the multiplier for.</param>
    /// <returns>A formatted string representing the breeding multiplier.</returns>
    public string CalculateBreedingMultiplier(int level)
    {
        var difference = 0.02;
        return $"{Math.Round(1 + level * difference, 2)}x";
    }

    /// <summary>
    ///     Calculates IV multiplier based on level.
    /// </summary>
    /// <param name="level">The level to calculate the multiplier for.</param>
    /// <returns>A formatted string representing the IV multiplier.</returns>
    public string CalculateIvMultiplier(int level)
    {
        var difference = 0.5;
        return $"{Math.Round(level * difference, 1)}%";
    }

    /// <summary>
    ///     Checks if a mission with the specified key is active.
    /// </summary>
    /// <param name="key">The mission key to check.</param>
    /// <returns>True if there are active missions with the key, false otherwise.</returns>
    public async Task<bool> CheckActive(string key)
    {
        var activeMissions = await _mongoService.Missions
            .Find(m => m.Active == true && m.Key == key)
            .ToListAsync();

        return activeMissions.Any();
    }
}

/// <summary>
///     The details of a move.
/// </summary>
/// <param name="Name">The move's identifier.</param>
/// <param name="Type">The move's type.</param>
/// <param name="Power">The move's power, or null for moves without one.</param>
/// <param name="Accuracy">The move's accuracy, or null for moves that never miss.</param>
/// <param name="PP">The move's power points.</param>
/// <param name="Category">Physical, Special or Status.</param>
public record MoveDetails(string Name, string Type, int? Power, int? Accuracy, int PP, string Category);