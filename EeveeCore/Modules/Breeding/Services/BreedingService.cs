using System.Text.Json;
using EeveeCore.Database.Linq.Models.Pokemon;
using EeveeCore.Database.Models.Mongo.Pokemon;
using EeveeCore.Modules.Achievements.Services;
using EeveeCore.Modules.Missions.Services;
using EeveeCore.Services.Impl;
using LinqToDB;
using LinqToDB.Async;
using MongoDB.Driver;
using Serilog;
using SkiaSharp;

namespace EeveeCore.Modules.Breeding.Services;

/// <summary>
///     Service for Pokémon breeding operations.
/// </summary>
public class BreedingService : INService
{
    // Dictionary to track users with auto-breeding enabled
    private readonly Dictionary<ulong, int?> _autoRedo = new();

    // Dictionary to track auto-breeding attempts
    private readonly Dictionary<(ulong UserId, int MaleId), int> _breedRetries = new();
    private readonly LinqToDbConnectionProvider _dbContextProvider;
    private readonly IMongoService _mongoService;
    private readonly AchievementService _achievementService;
    private readonly MissionService _missionService;

    /// <summary>
    ///     List of nature options for Pokémon
    /// </summary>
    private readonly List<string> _natures =
    [
        "Hardy", "Lonely", "Brave", "Adamant", "Naughty",
        "Bold", "Docile", "Relaxed", "Impish", "Lax",
        "Timid", "Hasty", "Serious", "Jolly", "Naive",
        "Modest", "Mild", "Quiet", "Bashful", "Rash",
        "Calm", "Gentle", "Sassy", "Careful", "Quirky"
    ];

    private readonly Random _random = new();
    private readonly RedisCache _redisCache;

    /// <summary>
    ///     List of egg groups that are considered undiscovered and cannot breed
    /// </summary>
    private readonly List<int> _undiscoveredEggGroups = [15];

    /// <summary>
    ///     Initializes a new instance of the <see cref="BreedingService" /> class.
    /// </summary>
    /// <param name="mongoService">The MongoDB service for database operations.</param>
    /// <param name="dbContextProvider">The database context provider.</param>
    /// <param name="redisCache">The Redis cache service.</param>
    /// <param name="achievementService">The achievement service for tracking milestones.</param>
    /// <param name="missionService">The mission service for tracking mission progress.</param>
    public BreedingService(
        IMongoService mongoService,
        LinqToDbConnectionProvider dbContextProvider,
        RedisCache redisCache,
        AchievementService achievementService,
        MissionService missionService)
    {
        _mongoService = mongoService;
        _dbContextProvider = dbContextProvider;
        _redisCache = redisCache;
        _achievementService = achievementService;
        _missionService = missionService;
        new HttpClient();

        // Initialize the cache for breed cooldowns
        _ = InitializeAsync();
    }

    /// <summary>
    ///     Initializes the Redis cache for breeding cooldowns.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task InitializeAsync()
    {
        await _redisCache.Redis.GetDatabase().ExecuteAsync("HMSET", "breedcooldowns", "examplekey", "examplevalue");
    }

    /// <summary>
    ///     Resets the breeding cooldown for a user.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ResetCooldownAsync(ulong userId)
    {
        await _redisCache.Redis.GetDatabase().ExecuteAsync("HMSET", "breedcooldowns", userId.ToString(), "0");
        _autoRedo[userId] = null;
    }

    /// <summary>
    ///     How long a mother rests after breeding.
    /// </summary>
    public static readonly TimeSpan MotherCooldown = TimeSpan.FromHours(2);

    /// <summary>
    ///     Gets how long a user must wait before breeding again.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <returns>The time left, or zero when the user can breed.</returns>
    public async Task<TimeSpan> GetBreedCooldownAsync(ulong userId)
    {
        var result = await _redisCache.Redis.GetDatabase().ExecuteAsync("HMGET", "breedcooldowns", userId.ToString());
        if (result is not { Length: > 0 } || result[0] is null || !double.TryParse(result[0].ToString(), out var resetAt))
            return TimeSpan.Zero;

        var left = resetAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return left > 0 ? TimeSpan.FromSeconds(left) : TimeSpan.Zero;
    }

    /// <summary>
    ///     Gets when the given Pokémon can next be bred as a mother.
    /// </summary>
    /// <param name="pokemonIds">The Pokémon IDs.</param>
    /// <returns>When each Pokémon that is still resting can breed again.</returns>
    public async Task<Dictionary<ulong, DateTime>> GetMotherCooldownsAsync(IReadOnlyCollection<ulong> pokemonIds)
    {
        await using var db = await _dbContextProvider.GetConnectionAsync();
        var mothers = await db.Mothers
            .Where(m => pokemonIds.Contains(m.PokemonId) && m.EntryTime != null)
            .Select(m => new { m.PokemonId, m.EntryTime })
            .ToListAsync();

        var now = DateTime.UtcNow;
        return mothers
            .GroupBy(m => m.PokemonId)
            .Select(g => (g.Key, Ends: g.Max(m => m.EntryTime!.Value).Add(MotherCooldown)))
            .Where(m => m.Ends > now)
            .ToDictionary(m => m.Key, m => m.Ends);
    }

    /// <summary>
    ///     Gets how many eggs a user has and how many the daycare holds.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    public async Task<(int Eggs, int Limit)> GetDaycareUsageAsync(ulong userId)
    {
        await using var db = await _dbContextProvider.GetConnectionAsync();
        var eggs = await db.UserPokemon.CountAsync(p => p.Owner == userId && p.PokemonName == "Egg");
        var limit = await db.Users.Where(u => u.UserId == userId).Select(u => u.DaycareLimit).FirstOrDefaultAsync();
        return (eggs, limit ?? 1);
    }

    /// <summary>
    ///     Clears all breeding female flags for a user.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ClearUserFemalesAsync(ulong userId)
    {
        await using var db = await _dbContextProvider.GetConnectionAsync();

        // Clear all breeding female flags for this user
        await db.UserPokemonOwnerships
            .Where(o => o.UserId == userId && o.IsBreedingFemale)
            .Set(o => o.IsBreedingFemale, false)
            .UpdateAsync();
    }

    /// <summary>
    ///     Gets auto-breeding state for a user.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <returns>The male ID if auto-breeding is enabled, null otherwise.</returns>
    public int? GetAutoBreedState(ulong userId)
    {
        return _autoRedo.TryGetValue(userId, out var value) ? value : null;
    }

    /// <summary>
    ///     Sets auto-breeding state for a user.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="maleId">The male Pokémon ID to use for breeding, or null to disable.</param>
    public void SetAutoBreedState(ulong userId, int? maleId)
    {
        _autoRedo[userId] = maleId;
    }

    /// <summary>
    ///     Gets the retry count for a breeding attempt.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="maleId">The male Pokémon ID.</param>
    /// <returns>The current retry count.</returns>
    public int GetBreedRetries(ulong userId, int maleId)
    {
        var key = (userId, maleId);
        return _breedRetries.TryGetValue(key, out var value) ? value : 0;
    }

    /// <summary>
    ///     Increments the retry count for a breeding attempt.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="maleId">The male Pokémon ID.</param>
    /// <returns>The updated retry count.</returns>
    public int IncrementBreedRetries(ulong userId, int maleId)
    {
        var key = (userId, maleId);
        if (!_breedRetries.TryGetValue(key, out var value)) value = 0;

        value++;
        _breedRetries[key] = value;
        return value;
    }

    /// <summary>
    ///     Resets the retry count for a breeding attempt.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="maleId">The male Pokémon ID.</param>
    public void ResetBreedRetries(ulong userId, int maleId)
    {
        var key = (userId, maleId);
        _breedRetries[key] = 0;
    }

    /// <summary>
    ///     Updates a user's breeding female Pokémon using the ownership table.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="femalePositions">The list of female Pokémon positions (0-based, already converted from user input).</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateUserFemalesAsync(ulong userId, List<ulong> femalePositions)
    {
        try
        {
            await using var db = await _dbContextProvider.GetConnectionAsync();

            // Single optimized update: set all to false first, then set specified ones to true
            // This is faster than separate operations and avoids transaction issues
            await db.UserPokemonOwnerships
                .Where(o => o.UserId == userId)
                .Set(o => o.IsBreedingFemale, o => femalePositions.Contains(o.Position))
                .UpdateAsync();

            Log.Information("Updated breeding females for user {UserId}: {Positions} (0-based)", userId, string.Join(",", femalePositions));
        }
        catch (Exception e)
        {
            Log.Error(e, "Error updating breeding females for user {UserId}", userId);
            throw;
        }
    }

    /// <summary>
    ///     Gets all breeding female Pokémon positions for a user.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <returns>A list of female Pokémon positions (1-based).</returns>
    public async Task<List<ulong>> GetUserFemalesAsync(ulong userId)
    {
        await using var db = await _dbContextProvider.GetConnectionAsync();

        return await db.UserPokemonOwnerships
            .Where(o => o.UserId == userId && o.IsBreedingFemale)
            .OrderBy(o => o.Position)
            .Select(o => o.Position + 1) // Convert to 1-based positions
            .ToListAsync();
    }

    /// <summary>
    ///     Fetches the first breeding female Pokémon position for a user.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <returns>The first female Pokémon position (1-based), or null if none exists.</returns>
    public async Task<ulong?> FetchFirstFemaleAsync(ulong userId)
    {
        await using var db = await _dbContextProvider.GetConnectionAsync();

        var firstFemale = await db.UserPokemonOwnerships
            .Where(o => o.UserId == userId && o.IsBreedingFemale)
            .OrderBy(o => o.Position)
            .Select(o => o.Position + 1) // Convert to 1-based position
            .FirstOrDefaultAsync();

        return firstFemale == 0 ? null : firstFemale;
    }

    /// <summary>
    ///     Removes the first breeding female flag from a user's list.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RemoveFirstFemaleAsync(ulong userId)
    {
        await using var db = await _dbContextProvider.GetConnectionAsync();

        // Find the first breeding female (by position order)
        var firstFemale = await db.UserPokemonOwnerships
            .Where(o => o.UserId == userId && o.IsBreedingFemale)
            .OrderBy(o => o.Position)
            .FirstOrDefaultAsync();

        if (firstFemale == null) return;

        // Remove the breeding female flag
        await db.UserPokemonOwnerships
            .Where(o => o.Id == firstFemale.Id)
            .Set(o => o.IsBreedingFemale, false)
            .UpdateAsync();
    }

    /// <summary>
    ///     Checks if a Pokémon is in a formed state.
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
    ///     Checks if a Pokémon is a regional form.
    /// </summary>
    /// <param name="pokemonName">The name of the Pokémon to check.</param>
    /// <returns>True if the Pokémon is a regional form, false otherwise.</returns>
    public bool IsRegionalForm(string pokemonName)
    {
        return pokemonName.EndsWith("alola") ||
               pokemonName.EndsWith("galar") ||
               pokemonName.EndsWith("hisui") ||
               pokemonName.EndsWith("paldea");
    }

    /// <summary>
    ///     Attempts to breed two Pokémon.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="maleId">The male Pokémon ID.</param>
    /// <param name="femaleId">The female Pokémon ID.</param>
    /// <returns>A <see cref="BreedingResult" /> containing the result of the breeding attempt.</returns>
    public async Task<BreedingResult> AttemptBreedAsync(ulong userId, ulong maleId, ulong femaleId)
    {
        await using var db = await _dbContextProvider.GetConnectionAsync();

        // Check cooldown
        var breedResetResult =
            await _redisCache.Redis.GetDatabase().ExecuteAsync("HMGET", "breedcooldowns", userId.ToString());
        double cooldownTime = 0;

        if (breedResetResult is { Length: > 0 } && breedResetResult[0] != null &&
            !string.IsNullOrEmpty(breedResetResult[0].ToString()))
            cooldownTime = double.Parse(breedResetResult[0].ToString());

        if (cooldownTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds() && userId != 280835732728184843)
        {
            var resetIn = cooldownTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = $"Command on cooldown for {Math.Round(resetIn)}s"
            };
        }

        // Set cooldown
        await _redisCache.Redis.GetDatabase().ExecuteAsync(
            "HMSET",
            "breedcooldowns",
            userId.ToString(),
            (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10).ToString());

        // Basic validation
        if (maleId == femaleId)
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = "You can not breed the same Pokemon!"
            };
        }

        // Fetch the user
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user == null)
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = "You have not started!\nStart with `/start` first."
            };
        }

        // Check if the user has enough daycare slots
        var pokemonCount = await db.UserPokemon
            .CountAsync(p => p.Owner == userId && p.PokemonName == "Egg");

        if (pokemonCount > user.DaycareLimit && user.UserId != 280835732728184843)
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = "You already have enough Pokemon in the Daycare!"
            };
        }

        // Get the male and female Pokémon by position
        var malePosition = maleId - 1;
        var femalePosition = femaleId - 1;

        var maleOwnership = await db.UserPokemonOwnerships
            .FirstOrDefaultAsync(o => o.UserId == userId && o.Position == malePosition);

        var femaleOwnership = await db.UserPokemonOwnerships
            .FirstOrDefaultAsync(o => o.UserId == userId && o.Position == femalePosition);

        if (maleOwnership == null || femaleOwnership == null)
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = maleOwnership == null ? "Male pokemon not found!" : "Female pokemon not found!"
            };
        }

        // Get the Pokémon details
        var fatherDetails = await db.UserPokemon
            .FirstOrDefaultAsync(p => p.Id == maleOwnership.PokemonId);

        var motherDetails = await db.UserPokemon
            .FirstOrDefaultAsync(p => p.Id == femaleOwnership.PokemonId);

        if (fatherDetails == null || motherDetails == null)
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = "You do not have that many pokemon!"
            };
        }

        // Check if the Pokémon are eggs
        if (fatherDetails.PokemonName == "Egg" || motherDetails.PokemonName == "Egg")
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = "You cannot breed an egg!"
            };
        }

        // Check if the Pokémon are Dittos
        if (fatherDetails.PokemonName == "Ditto" && motherDetails.PokemonName == "Ditto")
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = "You cannot breed two dittos!"
            };
        }

        // Check if the father is male or a Ditto
        if (fatherDetails.Gender != "-m" && fatherDetails.Gender != "-x" && fatherDetails.PokemonName != "Ditto")
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = "That is not a male pokemon ID, please try again!"
            };
        }

        // Check if the Pokémon are breedable
        if (!fatherDetails.Breedable || !motherDetails.Breedable)
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage =
                    $"{fatherDetails.PokemonName} or {motherDetails.PokemonName} have been set to **NOT BREEDABLE**.\nYou cannot breed these pokemon."
            };
        }

        // Ditto is always the father since mother passes the pokename
        if (motherDetails.PokemonName == "Ditto") (motherDetails, fatherDetails) = (fatherDetails, motherDetails);

        var motherRecord = await db.Mothers
            .FirstOrDefaultAsync(c => c.PokemonId == motherDetails.Id);

        if (motherRecord != null)
        {
            // Check if the cooldown period has elapsed
            var cooldownEnds = motherRecord.EntryTime?.Add(MotherCooldown) ?? DateTime.UtcNow;

            if (DateTime.UtcNow < cooldownEnds)
            {
                // Still on cooldown
                var timeRemaining = cooldownEnds - DateTime.UtcNow;
                await ResetCooldownAsync(userId);
                return new BreedingResult
                {
                    Success = false,
                    ErrorMessage = $"Your {motherDetails.PokemonName} is on cooldown for another {timeRemaining.Hours}h {timeRemaining.Minutes}m."
                };
            }
            else
            {
                // Cooldown expired, remove the entry
                await db.DeleteAsync(motherRecord);
            }
        }

        // Get parent Pokémon data for breeding
        var father = await GetParentAsync(fatherDetails);
        var mother = await GetParentAsync(motherDetails);

        if (father == null)
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = $"You can not breed a {fatherDetails.PokemonName}! You might need to `/deform` it first."
            };
        }

        if (mother == null)
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = $"You can not breed a {motherDetails.PokemonName}! You might need to `/deform` it first."
            };
        }

        // Check egg groups
        if (_undiscoveredEggGroups.Any(g => father.EggGroups.Contains(g)) ||
            _undiscoveredEggGroups.Any(g => mother.EggGroups.Contains(g)))
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = "You can not breed undiscovered egg groups!"
            };
        }

        // Check IV limits (preventing hexas)
        var fatherTotalIv = father.Hp + father.Attack + father.Defense + father.SpAtk + father.SpDef + father.Speed;
        var motherTotalIv = mother.Hp + mother.Attack + mother.Defense + mother.SpAtk + mother.SpDef + mother.Speed;

        if (fatherTotalIv == 186 || motherTotalIv == 186)
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = "You cannot breed 100% iv pokemon!"
            };
        }

        // Check compatibility
        var breedable = father.EggGroups.Any(g => mother.EggGroups.Contains(g));
        var dittoed = father.Name == "Ditto" || mother.Name == "Ditto";
        var manaphied = (father.Name == "Manaphy" || father.Name == "Phione" ||
                         mother.Name == "Manaphy" || mother.Name == "Phione") && dittoed;

        if (!breedable && !dittoed && !manaphied)
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = "These Two Pokemon are not breedable!"
            };
        }

        // Get user's inventory
        var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Inventory ?? "{}") ??
                        new Dictionary<string, int>();

        // Get shiny multiplier and determine if child will be shiny
        var shinyMultiplier = inventory.GetValueOrDefault("shiny-multiplier", 0);
        var shinyThreshold = 8000 - (int)(8000 * (shinyMultiplier / 100.0));
        var isShiny = _random.Next(0, shinyThreshold) == 0;

        // Generate child
        var (child, counter, inheritedStats) = await GetChildAsync(mother, father, isShiny);

        if (child == null)
        {
            await ResetCooldownAsync(userId);
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = $"You can not breed a {motherDetails.PokemonName}! You might need to `/deform` it first."
            };
        }

        // Calculate chance of success
        if (fatherTotalIv < 40) fatherTotalIv = 120;
        if (motherTotalIv < 40) motherTotalIv = 120;

        var chance = (double)(Math.Min(100, father.CaptureRate) + Math.Min(100, mother.CaptureRate)) /
                     ((fatherTotalIv + motherTotalIv) * 3);

        // Apply breeding multiplier
        var breedMultiplier = inventory.GetValueOrDefault("breeding-multiplier", 0);
        var multiplier = breedMultiplier / 50.0 + 1.0; // 1.0 - 2.0
        chance *= multiplier;

        // Determine if breeding is successful
        var success = _random.NextDouble() < chance;

        if (!success)
            return new BreedingResult
            {
                Success = false,
                ErrorMessage = "Breeding attempt failed",
                Chance = chance,
                Child = null
            };

        // Check for shadow hunt
        var isShadow = false;
        if (!isShiny)
            // This would be a call to a service
            isShadow = await CheckShadowHuntAsync(userId, child.Name);

        // If successful, add egg to user's Pokémon
        await InsertEggAsync(userId, child, counter, isShadow);

        // Add mother to cooldown table
        await db.InsertAsync(new Mother
        {
            PokemonId = motherDetails.Id,
            OwnerId = userId,
            EntryTime = DateTime.UtcNow
        });

        // Update achievements
        if (isShadow)
            await db.Achievements
                .Where(a => a.UserId == userId)
                .Set(a => a.ShadowBred, a => a.ShadowBred + 1)
                .UpdateAsync();
        else if (isShiny)
            await db.Achievements
                .Where(a => a.UserId == userId)
                .Set(a => a.ShinyBred, a => a.ShinyBred + 1)
                .UpdateAsync();
        else
            await db.Achievements
                .Where(a => a.UserId == userId)
                .Set(a => a.BreedSuccess, a => a.BreedSuccess + 1)
                .UpdateAsync();

        // Comprehensive achievement tracking with IV analysis and milestones
        try
        {
            var childIvs = new[] { child.Hp, child.Attack, child.Defense, child.SpAtk, child.SpDef, child.Speed };
            
            // Track comprehensive breeding achievements including milestones and rewards
            await _achievementService.TrackBreedingAsync(userId, childIvs, isShiny, isShadow);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error tracking comprehensive breeding achievements for user {UserId}", userId);
        }

        return new BreedingResult
        {
            Success = true,
            Child = child,
            Counter = counter,
            Chance = chance,
            IsShiny = isShiny,
            IsShadow = isShadow,
            InheritedStats = inheritedStats
        };
    }

    /// <summary>
    ///     Gets a parent Pokémon for breeding from a database record.
    /// </summary>
    /// <param name="pokemon">The Pokémon database record.</param>
    /// <returns>A <see cref="BreedingPokemon" /> representing the parent, or null if invalid.</returns>
    private async Task<BreedingPokemon?> GetParentAsync(Database.Linq.Models.Pokemon.Pokemon pokemon)
    {
        var pokemonName = pokemon.PokemonName.ToLower();

        // Get PFile for the Pokémon
        var pokemonPFile = await _mongoService.PFile.Find(f => f.Identifier == pokemonName).FirstOrDefaultAsync();

        if (pokemonPFile == null && pokemonName.Contains("alola"))
            // Try without alola suffix
            pokemonPFile = await _mongoService.PFile
                .Find(f => f.Identifier == pokemonName.Substring(0, pokemonName.Length - 5)).FirstOrDefaultAsync();

        if (pokemonPFile == null) return null;

        // Get the form info
        var formInfo = await _mongoService.Forms.Find(f => f.Identifier == pokemonName).FirstOrDefaultAsync();
        if (formInfo == null) return null;

        // Get abilities
        var abilityIds = new List<int>();
        await _mongoService.PokeAbilities
            .Find(a => a.PokemonId == formInfo.PokemonId)
            .ForEachAsync(a => abilityIds.Add(a.AbilityId));

        // Get egg groups
        var eggGroupsRecord =
            await _mongoService.EggGroups.Find(e => e.SpeciesId == formInfo.PokemonId).FirstOrDefaultAsync();
        var eggGroups = eggGroupsRecord?.Groups.ToList() ?? [1]; // Default to 'Monster' group

        // Get base Pokémon in evolution chain
        var pokemonName2 = pokemonPFile.Identifier;
        var currentPFile = pokemonPFile;

        while (currentPFile.EvolvesFromSpeciesId.HasValue)
        {
            currentPFile = await _mongoService.PFile.Find(f => f.PokemonId == currentPFile.EvolvesFromSpeciesId.Value)
                .FirstOrDefaultAsync();
            if (currentPFile == null) break;
            pokemonName2 = currentPFile.Identifier;
        }

        // Handle manaphy/phione special case
        if (pokemonName2 == "manaphy")
        {
            var phione = await _mongoService.PFile.Find(f => f.Identifier == "phione").FirstOrDefaultAsync();
            if (phione != null)
            {
                pokemonName2 = "phione";
                pokemonPFile = phione;
            }
        }

        // Create the parent Pokémon
        return new BreedingPokemon
        {
            Name = pokemon.PokemonName,
            Gender = pokemon.Gender,
            Hp = pokemon.HpIv,
            Attack = pokemon.AttackIv,
            Defense = pokemon.DefenseIv,
            SpAtk = pokemon.SpecialAttackIv,
            SpDef = pokemon.SpecialDefenseIv,
            Speed = pokemon.SpeedIv,
            Level = pokemon.Level,
            Shiny = pokemon.Shiny.GetValueOrDefault(),
            HeldItem = pokemon.HeldItem,
            Happiness = 0, // Default to 0 for breeding
            AbilityId = abilityIds.Count > pokemon.AbilityIndex
                ? abilityIds[pokemon.AbilityIndex]
                : abilityIds.FirstOrDefault(),
            AbilityIds = abilityIds,
            EggGroups = eggGroups,
            Nature = pokemon.Nature,
            CaptureRate = pokemonPFile.CaptureRate.GetValueOrDefault()
        };
    }

    private static int EggSteps(PokemonFile species) => species.HatchCounter.GetValueOrDefault() * 2;

    /// <summary>
    ///     Gets the steps a new egg of each species starts with.
    /// </summary>
    /// <param name="species">The species the eggs hold.</param>
    /// <returns>The starting steps by lowercase species name, for the species that exist.</returns>
    public async Task<Dictionary<string, int>> GetEggStepsAsync(IEnumerable<string> species)
    {
        var names = species.Where(s => !string.IsNullOrEmpty(s)).Select(s => s.ToLower()).Distinct().ToList();
        if (names.Count == 0)
            return [];

        return (await _mongoService.PFile.Find(f => names.Contains(f.Identifier)).ToListAsync())
            .GroupBy(f => f.Identifier)
            .ToDictionary(g => g.Key, g => EggSteps(g.First()));
    }

    /// <summary>
    ///     Generates a child Pokémon from two parents.
    /// </summary>
    /// <param name="mother">The mother Pokémon.</param>
    /// <param name="father">The father Pokémon.</param>
    /// <param name="shiny">Whether the child should be shiny.</param>
    /// <returns>A tuple containing the child, hatch counter, and inherited stats.</returns>
    private async Task<(BreedingPokemon Child, int Counter, List<string> InheritedStats)> GetChildAsync(
        BreedingPokemon mother, BreedingPokemon father, bool shiny)
    {
        // Determine nature
        var natures = new List<string>();
        if (father.HeldItem == "everstone") natures.Add(father.Nature);
        if (mother.HeldItem == "everstone") natures.Add(mother.Nature);
        var nature = natures.Count > 0 ? natures[_random.Next(natures.Count)] : _natures[_random.Next(_natures.Count)];

        // Determine base stats
        var threshold = _random.Next(0, 30) == 0 ? 25 : 22;

        var hp = _random.Next(0, threshold + 1);
        var attack = _random.Next(0, threshold + 1);
        var defense = _random.Next(0, threshold + 1);
        var specialAttack = _random.Next(0, threshold + 1);
        var specialDefense = _random.Next(0, threshold + 1);
        var speed = _random.Next(0, threshold + 1);

        // Get species info for the mother
        var identifier = mother.Name.ToLower();
        var pokemonPFile = await _mongoService.PFile.Find(f => f.Identifier == identifier).FirstOrDefaultAsync();

        if (pokemonPFile == null) return (null, 0, null);

        // Get the base evolution
        while (pokemonPFile.EvolvesFromSpeciesId.HasValue)
        {
            pokemonPFile = await _mongoService.PFile.Find(f => f.PokemonId == pokemonPFile.EvolvesFromSpeciesId.Value)
                .FirstOrDefaultAsync();
            if (pokemonPFile == null) return (null, 0, null);
        }

        // Handle manaphy -> phione case
        if (pokemonPFile.Identifier == "manaphy")
            pokemonPFile = await _mongoService.PFile.Find(f => f.Identifier == "phione").FirstOrDefaultAsync();

        var name = pokemonPFile.Identifier;
        var genderRate = pokemonPFile.GenderRate;
        var id = pokemonPFile.PokemonId;
        var counter = EggSteps(pokemonPFile);

        // Get ability IDs
        var abilityIds = new List<int>();
        await _mongoService.PokeAbilities
            .Find(a => a.PokemonId == id)
            .ForEachAsync(a => abilityIds.Add(a.AbilityId));

        // Get egg groups
        var eggGroups = await _mongoService.EggGroups
            .Find(e => e.SpeciesId == id)
            .FirstOrDefaultAsync();

        if (eggGroups == null) eggGroups = new EggGroup { Groups = [1] };

        // Inherit stats from parents
        var stats = new List<string> { "hp", "attack", "defense", "spatk", "spdef", "speed" };
        var parents = new List<BreedingPokemon> { father, mother };
        var inheritedStats = stats.OrderBy(_ => _random.Next()).Take(2).ToList();

        for (var i = 0; i < inheritedStats.Count; i++)
        {
            var stat = inheritedStats[i];
            var parent = parents[i];

            switch (stat)
            {
                case "hp":
                    hp = parent.Hp;
                    break;
                case "attack":
                    attack = parent.Attack;
                    break;
                case "defense":
                    defense = parent.Defense;
                    break;
                case "spatk":
                    specialAttack = parent.SpAtk;
                    break;
                case "spdef":
                    specialDefense = parent.SpDef;
                    break;
                case "speed":
                    speed = parent.Speed;
                    break;
            }
        }

        // Handle destiny knot
        var knotted = false;
        var numStats = 0;
        BreedingPokemon knotParent = null;

        if (mother.HeldItem == "ultra-destiny-knot")
        {
            knotParent = mother;
            numStats = _random.Next(0, 10) == 0 ? 4 : 3;
            knotted = true;
        }
        else if (father.HeldItem == "ultra-destiny-knot")
        {
            knotParent = father;
            numStats = _random.Next(0, 10) == 0 ? 4 : 3;
            knotted = true;
        }
        else if (mother.HeldItem == "destiny-knot")
        {
            knotParent = mother;
            numStats = _random.Next(0, 3) == 0 ? 2 : 1;
            knotted = true;
        }
        else if (father.HeldItem == "destiny-knot")
        {
            knotParent = father;
            numStats = _random.Next(0, 3) == 0 ? 2 : 1;
            knotted = true;
        }

        var knotStats = new List<string>();

        if (knotted)
        {
            // Remove already inherited stats
            var remainingStats = stats.Except(inheritedStats).ToList();

            // Select additional stats to inherit
            knotStats = remainingStats.OrderBy(_ => _random.Next()).Take(numStats).ToList();

            foreach (var stat in knotStats)
                switch (stat)
                {
                    case "hp":
                        hp = knotParent.Hp;
                        break;
                    case "attack":
                        attack = knotParent.Attack;
                        break;
                    case "defense":
                        defense = knotParent.Defense;
                        break;
                    case "spatk":
                        specialAttack = knotParent.SpAtk;
                        break;
                    case "spdef":
                        specialDefense = knotParent.SpDef;
                        break;
                    case "speed":
                        speed = knotParent.Speed;
                        break;
                }
        }

        // Calculate steps (for evolution)
        var steps = counter * 257 / 20;

        // Determine gender
        string gender;

        if (name.Contains("nidoran"))
            gender = name.EndsWith("-m") ? "-m" : "-f";
        else if (name.ToLower() == "illumise")
            gender = "-f";
        else if (name.ToLower() == "volbeat")
            gender = "-m";
        else if (genderRate == -1)
            gender = "-x"; // Genderless
        else if (_random.Next(0, 8) < genderRate)
            gender = "-f";
        else
            gender = "-m";

        // Determine ability
        int abilityIdx;
        var motherAbilityIds = mother.AbilityIds.Select(id => abilityIds.IndexOf(id)).Where(idx => idx >= 0).ToList();

        if (motherAbilityIds.Any())
            abilityIdx = motherAbilityIds[_random.Next(motherAbilityIds.Count)];
        else
            abilityIdx = _random.Next(abilityIds.Count);

        // Create the child Pokémon
        var child = new BreedingPokemon
        {
            Name = name.Capitalize(),
            Gender = gender,
            Hp = hp,
            Attack = attack,
            Defense = defense,
            SpAtk = specialAttack,
            SpDef = specialDefense,
            Speed = speed,
            Level = 1,
            Shiny = shiny,
            HeldItem = "None",
            Happiness = 0,
            AbilityId = abilityIdx,
            AbilityIds = abilityIds,
            EggGroups = eggGroups.Groups?.ToList(),
            Nature = nature,
            CaptureRate = 0
        };

        // Combine inherited stats lists
        var allInheritedStats = inheritedStats.Concat(knotStats).ToList();

        return (child, counter, allInheritedStats);
    }

    /// <summary>
    ///     Checks if a Pokémon is eligible for shadow hunt.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="pokemonName">The name of the Pokémon.</param>
    /// <returns>True if the Pokémon is eligible, false otherwise.</returns>
    private async Task<bool> CheckShadowHuntAsync(ulong userId, string pokemonName)
    {
        await using var db = await _dbContextProvider.GetConnectionAsync();

        var user = await db.Users
            .Where(u => u.UserId == userId)
            .Select(u => new { u.Hunt, u.Chain })
            .FirstOrDefaultAsync();

        if (user == null || string.IsNullOrEmpty(user.Hunt)) return false;

        if (user.Hunt.ToLower() == pokemonName.ToLower())
        {
            // Calculate shadow chance based on chain
            var chain = user.Chain;
            var baseChance = 0.001; // 0.1% base chance
            var bonusChance = chain * 0.001; // 0.1% per chain level

            return _random.NextDouble() < baseChance + bonusChance;
        }

        return false;
    }

    /// <summary>
    ///     Inserts an egg into the database.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="child">The child Pokémon.</param>
    /// <param name="counter">The hatch counter.</param>
    /// <param name="isShadow">Whether the egg contains a shadow Pokémon.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task InsertEggAsync(ulong userId, BreedingPokemon child, int counter, bool isShadow)
    {
        try
        {
            await using var db = await _dbContextProvider.GetConnectionAsync();

            // Create new egg Pokémon
            var eggPokemon = new Database.Linq.Models.Pokemon.Pokemon
            {
                PokemonName = "Egg",
                HpIv = child.Hp,
                AttackIv = child.Attack,
                DefenseIv = child.Defense,
                SpecialAttackIv = child.SpAtk,
                SpecialDefenseIv = child.SpDef,
                SpeedIv = child.Speed,
                HpEv = 0,
                AttackEv = 0,
                DefenseEv = 0,
                SpecialAttackEv = 0,
                SpecialDefenseEv = 0,
                SpeedEv = 0,
                Level = 5,
                Moves = ["tackle", "tackle", "tackle", "tackle"],
                HeldItem = "None",
                Experience = 1,
                Nature = child.Nature,
                ExperienceCap = 35,
                Nickname = "None",
                Price = 0,
                MarketEnlist = false,
                Happiness = 0,
                Favorite = false,
                AbilityIndex = child.AbilityId,
                Counter = counter,
                Name = child.Name,
                Gender = child.Gender,
                CaughtBy = userId,
                Shiny = child.Shiny,
                Skin = isShadow ? "shadow" : null,
                Breedable = true,
                Tradable = true,
                Champion = false,
                Temporary = false,
                Voucher = false,
                Owner = userId,
                Timestamp = DateTime.UtcNow
            };

            // Save the egg to get its ID (no transaction to avoid LinqToDB ID issues)
            eggPokemon.Id = (ulong)await db.InsertWithInt64IdentityAsync(eggPokemon);

            // Find the highest position for this user's Pokémon
            var highestPosition = await db.UserPokemonOwnerships
                .Where(o => o.UserId == userId)
                .OrderByDescending(o => o.Position)
                .Select(o => (int?)o.Position)
                .FirstOrDefaultAsync() ?? -1;

            // Add a new ownership entry with the next position
            var ownership = new UserPokemonOwnership
            {
                UserId = userId,
                PokemonId = eggPokemon.Id,
                Position = (ulong)(highestPosition + 1),
                IsBreedingFemale = false // Eggs are not breeding females
            };

            await db.InsertAsync(ownership);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error inserting egg for user {UserId}", userId);
            throw; // Re-throw so the caller can handle the error properly
        }
    }

    /// <summary>
    ///     Creates a success image for a successful breeding result using SkiaSharp.
    /// </summary>
    /// <param name="result">The breeding result.</param>
    /// <param name="fatherName">The father's name.</param>
    /// <param name="motherName">The mother's name.</param>
    /// <returns>A byte array containing the image data.</returns>
    public async Task<byte[]> CreateSuccessImageAsync(BreedingResult result, string fatherName, string motherName)
    {
        // Determine image path based on result
        string imagePath;
        if (result.IsShadow)
            imagePath = Path.Combine("data", "images", "eggstatsshadow2.png");
        else if (result.IsShiny)
            imagePath = Path.Combine("data", "images", "eggstatsshiny.png");
        else
            imagePath = Path.Combine("data", "images", "eggstats.png");

        // Load image with SkiaSharp
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Egg stats image not found at: {imagePath}");
        }
        
        using var bitmap = SKBitmap.Decode(imagePath);
        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height));
        var canvas = surface.Canvas;

        // Draw background image
        canvas.DrawBitmap(bitmap, 0, 0);

        // Create paint objects for different colors and styles
        var greenPaint = new SKPaint
        {
            Color = new SKColor(0, 139, 0),
            TextSize = 35,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright)
        };

        var redPaint = new SKPaint
        {
            Color = new SKColor(139, 0, 0),
            TextSize = 25,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright)
        };

        var bluePaint = new SKPaint
        {
            Color = new SKColor(116, 140, 255),
            TextSize = 35,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright)
        };

        var pinkPaint = new SKPaint
        {
            Color = new SKColor(255, 116, 140),
            TextSize = 35,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright)
        };

        var whitePaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 35,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright)
        };

        var blackPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = 35,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright)
        };

        // Get child stats
        var statsValues = new[]
        {
            result.Child.Hp,
            result.Child.Attack,
            result.Child.Defense,
            result.Child.SpAtk,
            result.Child.SpDef,
            result.Child.Speed
        };

        // Format inherited stats
        var inheritedStats = result.InheritedStats.Count > 5
            ? result.InheritedStats.GetRange(0, 5)
            : [..result.InheritedStats];

        while (inheritedStats.Count < 5) inheritedStats.Add("N/A");

        for (var i = 0; i < inheritedStats.Count; i++)
        {
            if (inheritedStats[i] == "attack") inheritedStats[i] = "atk";
            if (inheritedStats[i] == "defense") inheritedStats[i] = "def";
        }

        // Define positions for stats
        var statsPositions = new[]
        {
            new SKPoint(748, 356),
            new SKPoint(784, 416),
            new SKPoint(784, 476),
            new SKPoint(788, 536),
            new SKPoint(781, 599),
            new SKPoint(760, 659)
        };

        // Define positions for inherited stats
        var inheritedPositions = new[]
        {
            new SKPoint(205, 375),
            new SKPoint(185, 431),
            new SKPoint(166, 501),
            new SKPoint(173, 566),
            new SKPoint(193, 623)
        };

        // Draw stats values
        for (var i = 0; i < statsValues.Length; i++)
            canvas.DrawText(statsValues[i].ToString(), statsPositions[i], greenPaint);

        // Draw inherited stats
        for (var i = 0; i < inheritedStats.Count; i++)
            canvas.DrawText(inheritedStats[i], inheritedPositions[i], redPaint);

        // Determine gender text
        var genderSign = result.Child.Gender switch
        {
            "-m" => "Male ",
            "-f" => "Female ",
            _ => ""
        };

        // Draw parent names
        canvas.DrawText(fatherName, 635, 861, bluePaint);
        canvas.DrawText(motherName, 205, 861, pinkPaint);

        // Draw egg info
        canvas.DrawText($"{genderSign}{result.Child.Name} Egg!", 326, 136, whitePaint);
        canvas.DrawText($"IV % {result.Child.CalculateIvPercentage()}", 590, 202, blackPaint);
        canvas.DrawText(result.Child.Nature, 693, 713, blackPaint);

        // Convert to bytes
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();

        return bytes;
    }

    /// <summary>
    ///     Creates a failure image for an unsuccessful breeding result using SkiaSharp.
    /// </summary>
    /// <param name="retryCount">The current retry count.</param>
    /// <param name="auto">Whether this is an auto-retry.</param>
    /// <returns>A byte array containing the image data.</returns>
    public async Task<byte[]> CreateFailureImageAsync(int retryCount, bool auto)
    {
        // Load the failure image from local data
        var failureImagePath = Path.Combine("data", "images", "failure.png");
        if (!File.Exists(failureImagePath))
        {
            throw new FileNotFoundException($"Failure image not found at: {failureImagePath}");
        }

        // Load image with SkiaSharp
        using var bitmap = SKBitmap.Decode(failureImagePath);
        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height));
        var canvas = surface.Canvas;

        // Draw background image
        canvas.DrawBitmap(bitmap, 0, 0);

        // Create paint objects
        var smallPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = 10,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
                SKFontStyleSlant.Italic)
        };

        var bigPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = 18,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright)
        };

        // Draw text
        canvas.DrawText("IV: 0% - Cooldown Started", 48, 250, smallPaint);

        if (auto) canvas.DrawText($"Attempt #{retryCount} out of 15", 16, 195, bigPaint);

        // Convert to bytes
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    ///     Validates multiple Pokémon IDs in a single batch operation.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="pokemonIds">The list of Pokémon IDs to validate (1-based).</param>
    /// <returns>A dictionary mapping each ID to whether it's valid for breeding.</returns>
    public async Task<Dictionary<ulong, bool>> ValidateFemaleBatchAsync(ulong userId, List<ulong> pokemonIds)
    {
        try
        {
            await using var db = await _dbContextProvider.GetConnectionAsync();

            // Convert to 0-based positions for database lookup
            var positions = pokemonIds.Select(id => id - 1).ToList();

            // Single query to get all Pokemon data at once
            var pokemonData = await db.UserPokemonOwnerships
                .Where(o => o.UserId == userId && positions.Contains(o.Position))
                .Join(db.UserPokemon, o => o.PokemonId, p => p.Id, (o, p) => new 
                { 
                    Position = o.Position + 1, // Convert back to 1-based
                    p.Gender, 
                    p.PokemonName 
                })
                .ToListAsync();

            // Create result dictionary
            var results = new Dictionary<ulong, bool>();
            
            foreach (var id in pokemonIds)
            {
                var pokemon = pokemonData.FirstOrDefault(p => p.Position == id);
                if (pokemon == null)
                {
                    results[id] = false;
                }
                else
                {
                    // Valid if the Pokémon is female or a Ditto
                    results[id] = pokemon.Gender == "-f" || pokemon.PokemonName.ToLower() == "ditto";
                }
            }

            return results;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error batch validating females for user {UserId}", userId);
            throw;
        }
    }

    /// <summary>
    ///     Gets breeding female Pokémon data in a single batch query for performance.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="femalePositions">List of female Pokémon positions (1-based).</param>
    /// <returns>A list of Pokemon with their actual positions maintained.</returns>
    public async Task<List<(Database.Linq.Models.Pokemon.Pokemon Pokemon, ulong Position)>> GetBreedingFemalesBatch(ulong userId, List<ulong> femalePositions)
    {
        await using var db = await _dbContextProvider.GetConnectionAsync();

        // Convert to 0-based positions for database lookup
        var zeroBasedPositions = femalePositions.Select(p => p - 1).ToList();

        // Single query with JOIN to get all Pokemon data at once
        var pokemonWithPositions = await db.UserPokemonOwnerships
            .Where(o => o.UserId == userId && zeroBasedPositions.Contains(o.Position))
            .Join(db.UserPokemon, o => o.PokemonId, p => p.Id, (o, p) => new 
            { 
                Pokemon = p,
                Position = o.Position + 1 // Convert back to 1-based for display
            })
            .ToListAsync();

        // Maintain the original order based on femalePositions
        var result = new List<(Database.Linq.Models.Pokemon.Pokemon, ulong)>();
        foreach (var position in femalePositions)
        {
            var match = pokemonWithPositions.FirstOrDefault(p => p.Position == position);
            if (match != null)
            {
                result.Add((match.Pokemon, position));
            }
        }

        return result;
    }

    /// <summary>
    ///     Validates if a single Pokémon ID belongs to a female Pokémon or Ditto.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="pokemonId">The ID of the Pokémon to validate.</param>
    /// <returns>True if the Pokémon is female or a Ditto, false otherwise.</returns>
    public async Task<bool> ValidateFemaleIdAsync(ulong userId, ulong pokemonId)
    {
        var results = await ValidateFemaleBatchAsync(userId, [pokemonId]);
        return results.GetValueOrDefault(pokemonId, false);
    }

    /// <summary>
    ///     Gets the names of a breeding pair's parents.
    /// </summary>
    /// <param name="userId">The user's Discord ID.</param>
    /// <param name="malePosition">The ID of the male Pokémon.</param>
    /// <param name="femalePosition">The ID of the female Pokémon.</param>
    /// <returns>A tuple containing the father's and mother's names.</returns>
    public async Task<(string FatherName, string MotherName)> GetParentNamesAsync(ulong userId, ulong malePosition,
        ulong femalePosition)
    {
        await using var db = await _dbContextProvider.GetConnectionAsync();


        var maleOwnership = await db.UserPokemonOwnerships
            .Where(o => o.UserId == userId && o.Position == malePosition)
            .FirstOrDefaultAsync();

        var femaleOwnership = await db.UserPokemonOwnerships
            .Where(o => o.UserId == userId && o.Position == femalePosition)
            .FirstOrDefaultAsync();

        if (maleOwnership == null || femaleOwnership == null)
            return ("Unknown", "Unknown");

        // Get the father and mother details
        var fatherDetails = await db.UserPokemon
            .Where(p => p.Id == maleOwnership.PokemonId)
            .Select(p => new { p.PokemonName, p.Gender })
            .FirstOrDefaultAsync();

        var motherDetails = await db.UserPokemon
            .Where(p => p.Id == femaleOwnership.PokemonId)
            .Select(p => new { p.PokemonName, p.Gender })
            .FirstOrDefaultAsync();

        if (fatherDetails == null || motherDetails == null)
            return ("Unknown", "Unknown");

        // Swap father and mother if the "mother" is a Ditto
        // Since Ditto is always treated as the father in breeding
        if (motherDetails.PokemonName == "Ditto")
            return (motherDetails.PokemonName, fatherDetails.PokemonName);

        return (fatherDetails.PokemonName, motherDetails.PokemonName);
    }

    /// <summary>
    ///     Represents a Pokémon for breeding purposes.
    /// </summary>
    public class BreedingPokemon
    {
        /// <summary>
        ///     Gets or sets the Pokémon's name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's gender.
        /// </summary>
        public string Gender { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's HP IV.
        /// </summary>
        public int Hp { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's Attack IV.
        /// </summary>
        public int Attack { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's Defense IV.
        /// </summary>
        public int Defense { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's Special Attack IV.
        /// </summary>
        public int SpAtk { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's Special Defense IV.
        /// </summary>
        public int SpDef { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's Speed IV.
        /// </summary>
        public int Speed { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's level.
        /// </summary>
        public int Level { get; set; }

        /// <summary>
        ///     Gets or sets whether the Pokémon is shiny.
        /// </summary>
        public bool Shiny { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's held item.
        /// </summary>
        public string HeldItem { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's happiness value.
        /// </summary>
        public int Happiness { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's ability ID.
        /// </summary>
        public int AbilityId { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's available ability IDs.
        /// </summary>
        public List<int> AbilityIds { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's egg groups.
        /// </summary>
        public List<int> EggGroups { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's nature.
        /// </summary>
        public string Nature { get; set; }

        /// <summary>
        ///     Gets or sets the Pokémon's capture rate.
        /// </summary>
        public int CaptureRate { get; set; }

        /// <summary>
        ///     Calculates the total IV percentage of this Pokémon.
        /// </summary>
        /// <returns>The IV percentage as a decimal between 0 and 100.</returns>
        public double CalculateIvPercentage()
        {
            double totalIvs = Hp + Attack + Defense + SpAtk + SpDef + Speed;
            return Math.Round(totalIvs / 186.0 * 100, 2);
        }
    }

    /// <summary>
    ///     Result of a breeding operation.
    /// </summary>
    public class BreedingResult
    {
        /// <summary>
        ///     Gets or sets whether the breeding attempt was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        ///     Gets or sets the error message if the breeding attempt failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        ///     Gets or sets the child Pokémon if the breeding attempt was successful.
        /// </summary>
        public BreedingPokemon Child { get; set; }

        /// <summary>
        ///     Gets or sets the hatch counter for the egg if the breeding attempt was successful.
        /// </summary>
        public int Counter { get; set; }

        /// <summary>
        ///     Gets or sets the chance of success for the breeding attempt.
        /// </summary>
        public double Chance { get; set; }

        /// <summary>
        ///     Gets or sets whether the child Pokémon is shiny.
        /// </summary>
        public bool IsShiny { get; set; }

        /// <summary>
        ///     Gets or sets whether the child Pokémon is shadow.
        /// </summary>
        public bool IsShadow { get; set; }

        /// <summary>
        ///     Gets or sets the stats that were inherited from the parents.
        /// </summary>
        public List<string> InheritedStats { get; set; }
    }
}