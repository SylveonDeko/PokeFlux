using EeveeCore.Modules.Duels.Impl;
using EeveeCore.Modules.Duels.Impl.DuelPokemon;
using EeveeCore.Services.Impl;
using LinqToDB.Async;
using Serilog;
using StackExchange.Redis;

namespace EeveeCore.Modules.Duels.Services;

/// <summary>
///     Provides functionality for managing Pokémon battles between users.
///     Handles battle registration, tracking, cooldowns, and cleanup.
///     Maintains both in-memory and Redis-based persistence of battle state.
/// </summary>
public class DuelService : INService
{
    private const string DateFormat = "MM/dd/yyyy, HH:mm:ss";

    /// <summary>
    ///     Dictionary of active battles, keyed by a tuple of the two users' IDs.
    ///     Accessible from both PokemonBattleModule and DuelInteractionHandler.
    /// </summary>
    private readonly Dictionary<(ulong, ulong), Battle> _activeBattles = new();

    private readonly LinqToDbConnectionProvider _db;
    private readonly IMongoService _mongoService;
    private readonly RedisCache _redis;
    private DateTime? _duelResetTime;

    /// <summary>
    ///     Initializes a new instance of the DuelService class with required dependencies.
    ///     Sets up Redis for tracking battle cooldowns and states.
    /// </summary>
    /// <param name="mongoService">The MongoDB service for accessing Pokémon data.</param>
    /// <param name="db">The database context provider for Entity Framework operations.</param>
    /// <param name="redis">The Redis cache for cooldown and battle state persistence.</param>
    public DuelService(
        IMongoService mongoService,
        LinqToDbConnectionProvider db,
        RedisCache redis)
    {
        _mongoService = mongoService;
        _db = db;
        _redis = redis;

        // Initialize Redis cooldown storage
        _ = InitializeAsync();
    }

    /// <summary>
    ///     Initializes Redis hash maps for tracking duel cooldowns and reset times.
    ///     Sets up example values to ensure the hash maps exist and retrieves or sets the duel reset time.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task InitializeAsync()
    {
        try
        {
            // Initialize Redis cooldown hash maps
            var db = _redis.Redis.GetDatabase();

            // Make sure the duelcooldowns hash exists
            await db.HashSetAsync("duelcooldowns", "examplekey", "examplevalue");

            // Make sure the daily cooldowns hash exists
            await db.HashSetAsync("dailyduelcooldowns", "examplekey", "examplevalue");

            // Get or set the duel reset time
            var resetTime = await db.StringGetAsync("duelcooldownreset");
            if (!resetTime.HasValue)
            {
                _duelResetTime = DateTime.UtcNow;
                await db.StringSetAsync("duelcooldownreset", _duelResetTime.Value.ToString(DateFormat));
            }
            else
            {
                _duelResetTime = DateTime.ParseExact(resetTime.ToString(), DateFormat, null);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error initializing Redis for duel cooldowns");
        }
    }

    /// <summary>
    ///     Retrieves a user's Pokémon party from the database and converts it to DuelPokemon objects.
    ///     Creates a MemberTrainer with the party and sets up owner references.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="username">The user's name, shown in the battle.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns a list of DuelPokemon objects.
    ///     Returns an empty list if the user has no party or an error occurs.
    /// </returns>
    public async Task<List<DuelPokemon>> GetUserPokemonParty(ulong userId, string username)
    {
        var duelPokemon = new List<DuelPokemon>();

        try
        {
            await using var db = await _db.GetConnectionAsync();
            // Get the user's current party from Parties table
            var currentParty = await db.Parties
                .FirstOrDefaultAsync(p => p.UserId == userId && p.IsCurrentParty);

            if (currentParty == null) return duelPokemon; // Return empty list

            // Get party Pokemon IDs from slots
            var partyIds = new[] { currentParty.Slot1, currentParty.Slot2, currentParty.Slot3, 
                                  currentParty.Slot4, currentParty.Slot5, currentParty.Slot6 }
                .Where(id => id.HasValue && id.Value > 0)
                .Select(id => id!.Value)
                .ToList();
            if (!partyIds.Any()) return duelPokemon;

            // Fetch all Pokémon data for the party in a single query
            var partyPokemon = await db.UserPokemon
                .Where(x => x.Owner.HasValue)
                .Where(p => partyIds.Contains(p.Id))
                .ToListAsync();

            // Filter out eggs
            partyPokemon = partyPokemon
                .Where(p => !p.PokemonName.Equals("Egg", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Sort the Pokémon in the order they appear in the party array
            partyPokemon = partyPokemon
                .OrderBy(p => partyIds.IndexOf(p.Id))
                .ToList();

            // Create DuelPokemon objects for each party Pokémon
            foreach (var pokemon in partyPokemon)
            {
                // Use the factory method to create a DuelPokemon object
                var duelPoke = await DuelPokemon.Create(pokemon, _mongoService);
                if (duelPoke != null) duelPokemon.Add(duelPoke);
            }

            // Create a MemberTrainer to be the owner of these Pokémon
            if (duelPokemon.Any())
            {
                var trainer = new MemberTrainer(userId, username, duelPokemon);

                // Set the owner reference for each Pokémon
                foreach (var pokemon in duelPokemon) pokemon.Owner = trainer;
            }

            return duelPokemon;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred getting Pokemon party: {Error}", ex.Message);
            return [];
        }
    }


    #region Battle Management

    /// <summary>
    ///     Registers a battle in both the local dictionary and Redis for persistence.
    ///     Sets up user battle flags and metadata with appropriate expiration times.
    /// </summary>
    /// <param name="userId1">The Discord ID of the first user in the battle.</param>
    /// <param name="userId2">The Discord ID of the second user in the battle, or 0 for NPC battles.</param>
    /// <param name="battle">The Battle object to register.</param>
    /// <param name="interactionId">The Discord interaction ID used as a unique battle identifier.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RegisterBattle(ulong userId1, ulong userId2, Battle battle, ulong interactionId)
    {
        try
        {
            Log.Debug("Registering battle {BattleId} between users {User1} and {User2}",
                interactionId, userId1, userId2);

            // Add to local dictionary
            _activeBattles[(userId1, userId2)] = battle;

            // If it's a PvP battle, add the reverse mapping too for easier lookup
            if (userId2 != 0)
                _activeBattles[(userId2, userId1)] = battle;

            // Store in Redis
            var db = _redis.Redis.GetDatabase();

            // Mark users as in battle
            await db.StringSetAsync($"user_in_battle:{userId1}", "true", TimeSpan.FromHours(2));
            if (userId2 != 0)
                await db.StringSetAsync($"user_in_battle:{userId2}", "true", TimeSpan.FromHours(2));

            // Store battle metadata in a hash
            var battleKey = $"battle:{interactionId}";
            await db.HashSetAsync(battleKey, [
                new HashEntry("user1", userId1),
                new HashEntry("user2", userId2),
                new HashEntry("created", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            ]);

            // Set expiration to 2 hours (max battle duration)
            await db.KeyExpireAsync(battleKey, TimeSpan.FromHours(2));

            // Create user-to-battle mapping for quick lookup
            await db.StringSetAsync($"user_battle:{userId1}", interactionId, TimeSpan.FromHours(2));
            if (userId2 != 0)
                await db.StringSetAsync($"user_battle:{userId2}", interactionId, TimeSpan.FromHours(2));

            Log.Information("Battle registered: {InteractionId} between {User1} and {User2}",
                interactionId, userId1, userId2);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error registering battle {BattleId} in Redis", interactionId);
        }
    }

    /// <summary>
    ///     Finds a battle that a specified user is participating in.
    ///     Searches the local dictionary of active battles.
    /// </summary>
    /// <param name="userId">The Discord ID of the user to find a battle for.</param>
    /// <returns>
    ///     The Battle object if found, or null if the user is not in an active battle.
    /// </returns>
    public Battle FindBattle(ulong userId)
    {
        foreach (var kvp in _activeBattles)
        {
            var battle = kvp.Value;

            // Check if user is either trainer
            if ((battle.Trainer1 as MemberTrainer)?.Id == userId ||
                (battle.Trainer2 as MemberTrainer)?.Id == userId)
                return battle;
        }

        return null;
    }

    /// <summary>
    ///     Determines whether a user is currently in a battle.
    ///     Checks both the local dictionary and Redis for battle state.
    /// </summary>
    /// <param name="userId">The Discord ID of the user to check.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns true if the user is in a battle,
    ///     false otherwise.
    /// </returns>
    public async Task<bool> IsUserInBattle(ulong userId)
    {
        // Check local dictionary first
        if (FindBattle(userId) != null)
            return true;

        // If not found locally, check Redis
        try
        {
            var db = _redis.Redis.GetDatabase();
            var exists = await db.KeyExistsAsync($"user_in_battle:{userId}");
            if (!exists) return false;
            await db.KeyDeleteAsync($"user_in_battle:{userId}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking if user {UserId} is in battle", userId);
            return false;
        }
    }

    /// <summary>
    ///     Ends a battle and cleans up all references in both the local dictionary and Redis.
    ///     Removes battle metadata and user battle flags.
    /// </summary>
    /// <param name="battle">The Battle object to end.</param>
    /// <param name="battleId">The optional battle ID for Redis cleanup. If null, only local cleanup is performed.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task EndBattle(Battle battle, string battleId = null)
    {
        try
        {
            // Get user IDs
            ulong user1Id = 0;
            ulong user2Id = 0;

            if (battle.Trainer1 is MemberTrainer t1)
                user1Id = t1.Id;

            if (battle.Trainer2 is MemberTrainer t2)
                user2Id = t2.Id;

            Log.Debug("Ending battle between users {User1} and {User2}", user1Id, user2Id);

            // Remove from dictionary
            if (user1Id != 0 && user2Id != 0)
            {
                _activeBattles.Remove((user1Id, user2Id));
                _activeBattles.Remove((user2Id, user1Id));
            }
            else if (user1Id != 0)
            {
                _activeBattles.Remove((user1Id, 0));
            }

            // Clean up Redis
            var db = _redis.Redis.GetDatabase();

            // Remove user battle flags
            if (user1Id != 0)
                await db.KeyDeleteAsync($"user_in_battle:{user1Id}");
            if (user2Id != 0)
                await db.KeyDeleteAsync($"user_in_battle:{user2Id}");

            // If we have a battle ID, clean up battle metadata
            if (!string.IsNullOrEmpty(battleId))
            {
                // Delete user-to-battle mappings
                if (user1Id != 0)
                    await db.KeyDeleteAsync($"user_battle:{user1Id}");
                if (user2Id != 0)
                    await db.KeyDeleteAsync($"user_battle:{user2Id}");

                // Delete battle metadata
                await db.KeyDeleteAsync($"battle:{battleId}");

                Log.Information("Battle metadata removed: {BattleId}", battleId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error ending battle");
        }
    }

    #endregion
}