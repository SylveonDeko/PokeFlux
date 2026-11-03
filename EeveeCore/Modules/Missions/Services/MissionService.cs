using EeveeCore.Common.ModuleBehaviors;
using EeveeCore.Database.Linq.Models.Bot;
using EeveeCore.Database.Models.Mongo.Game;
using EeveeCore.Modules.Missions.Common;
using EeveeCore.Services.Impl;
using LinqToDB;
using LinqToDB.Async;
using MongoDB.Driver;
using Serilog;
using User = EeveeCore.Database.Linq.Models.Bot.User;

namespace EeveeCore.Modules.Missions.Services;

/// <summary>
///     Service for handling mission-related operations including progress tracking,
///     XP management, level calculations, and reward processing.
/// </summary>
public class MissionService(
    FluxerClient client,
    BotLog botLog,
    IMongoService mongoService,
    LinqToDbConnectionProvider dbProvider) : INService, IReadyExecutor
{
    /// <summary>
    ///     Initializes the mission service and registers event handlers.
    /// </summary>
    public async Task OnReadyAsync()
    {
        await Task.CompletedTask;
        RegisterEventHandlers();
    }
    
    #region Custom Events

    /// <summary>
    ///     Event fired when a Pokemon is bred.
    /// </summary>
    public event Func<CommandContext, Database.Linq.Models.Pokemon.Pokemon, bool, Task>? PokemonBred;

    /// <summary>
    ///     Event fired when a Pokemon is caught.
    /// </summary>
    public event Func<CommandContext, Task>? PokemonCaught;

    /// <summary>
    ///     Event fired when a Pokemon is hatched.
    /// </summary>
    public event Func<SocketMessage, Database.Linq.Models.Pokemon.Pokemon, Task>? PokemonHatched;

    /// <summary>
    ///     Event fired when a Pokemon is fished.
    /// </summary>
    public event Func<SocketMessage, User, Task>? PokemonFished;

    /// <summary>
    ///     Event fired when a duel is completed.
    /// </summary>
    public event Func<CommandContext, object, object, Task>? DuelCompleted;

    /// <summary>
    ///     Event fired when an NPC battle is completed.
    /// </summary>
    public event Func<CommandContext, object, object, Task>? NpcBattleCompleted;

    /// <summary>
    ///     Event fired when a user levels up.
    /// </summary>
    public event Func<ulong, int, Task>? UserLeveledUp;

    /// <summary>
    ///     Event fired when EV training occurs.
    /// </summary>
    public event Func<CommandContext, int, Task>? EvTraining;

    /// <summary>
    ///     Event fired when Pokemon setup occurs.
    /// </summary>
    public event Func<CommandContext, string, int, Task>? PokemonSetup;

    /// <summary>
    ///     Event fired when a party is registered.
    /// </summary>
    public event Func<CommandContext, Task>? PartyRegistered;

    /// <summary>
    ///     Event fired when XP is gained.
    /// </summary>
    public event Func<ulong, int, Task>? XpGained;

    /// <summary>
    ///     Event fired when a word search game is completed.
    /// </summary>
    public event Func<CommandContext, int, Task>? GameWordSearchCompleted;

    /// <summary>
    ///     Event fired when a slot machine game is played.
    /// </summary>
    public event Func<CommandContext, bool, Task>? GameSlotsPlayed;

    #endregion

    #region Mission Core Logic

    /// <summary>
    ///     Gets active missions with a specific key and IV requirement.
    /// </summary>
    /// <param name="key">The mission key to search for.</param>
    /// <returns>List of active missions with their details.</returns>
    public async Task<List<MissionInfo>> GetActiveMissionsWithKeyAndIvAsync(string key)
    {
        try
        {
            var activeMissions = await mongoService.Missions
                .Find(m => m.Active && m.Key == key)
                .ToListAsync();

            return activeMissions.Select(mission => new MissionInfo
            {
                Id = mission.MissionId,
                Iv = mission.Iv,
                Reward = mission.Reward
            }).ToList();
        }
        catch (Exception e)
        {
            Log.Error(e, "Error getting active missions with key {Key}", key);
            return [];
        }
    }

    /// <summary>
    ///     Processes user mission progress for a specific mission.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="missionId">The mission ID.</param>
    /// <param name="progressIncrement">How much progress to add.</param>
    /// <param name="targetNumber">The target number for multi-target missions.</param>
    /// <returns>True if the mission was completed, false otherwise.</returns>
    public async Task<bool> ProcessUserMissionProgressAsync(ulong userId, int missionId, int progressIncrement, int targetNumber = 1)
    {
        try
        {
            var mission = await mongoService.Missions.Find(m => m.MissionId == missionId).FirstOrDefaultAsync();
            if (mission == null)
            {
                Log.Warning("Mission not found with ID {MissionId}", missionId);
                return false;
            }

            var missionProgressKey = targetNumber > 1 ? $"{mission.Key}{targetNumber}" : mission.Key;
            var userProgress = await mongoService.UserProgress.Find(up => up.UserId == userId).FirstOrDefaultAsync();

            if (userProgress == null)
            {
                userProgress = new UserProgress
                {
                    UserId = userId,
                    Breed = 0,
                    Catch = 0,
                    DuelLose = 0,
                    DuelWin = 0,
                    Ev = 0,
                    Fish = 0,
                    Npc = 0,
                    Party = 0,
                    PokemonSetup = 0,
                    Vote = 0
                };

                await mongoService.UserProgress.InsertOneAsync(userProgress);
            }

            var currentProgress = GetProgressValue(userProgress, missionProgressKey);
            var targetValue = mission.Target; // For now, using main target. 

            if (currentProgress >= targetValue)
                return false; // Already completed

            var newProgress = Math.Min(currentProgress + progressIncrement, targetValue);
            await UpdateProgressValue(userProgress, missionProgressKey, newProgress);

            if (newProgress >= targetValue)
            {
                // Mission completed - award crystal slime
                await using var db = await dbProvider.GetConnectionAsync();
                await db.Users.Where(u => u.UserId == userId)
                    .Set(u => u.CrystalSlime, u => (u.CrystalSlime ?? 0) + mission.Reward)
                    .UpdateAsync();

                return true;
            }

            return false;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error processing mission progress for user {UserId}, mission {MissionId}", userId, missionId);
            return false;
        }
    }

    /// <summary>
    ///     Gets the progress value from user progress based on the key.
    /// </summary>
    private static int GetProgressValue(UserProgress userProgress, string key)
    {
        return key switch
        {
            "breed" => userProgress.Breed,
            "catch" => userProgress.Catch,
            "duel_win" => userProgress.DuelWin,
            "duel_lose" => userProgress.DuelLose,
            "ev" => userProgress.Ev,
            "fish" => userProgress.Fish,
            "npc" => userProgress.Npc,
            "party" => userProgress.Party,
            "pokemon_setup" => userProgress.PokemonSetup,
            "vote" => userProgress.Vote,
            "game_wordsearch" => userProgress.GameWordSearch,
            "game_slots" => userProgress.GameSlots,
            "game_slots_win" => userProgress.GameSlotsWin,
            _ => 0
        };
    }

    /// <summary>
    ///     Updates the progress value in user progress based on the key.
    /// </summary>
    private async Task UpdateProgressValue(UserProgress userProgress, string key, int value)
    {
        var updateDefinition = key switch
        {
            "breed" => Builders<UserProgress>.Update.Set(up => up.Breed, value),
            "catch" => Builders<UserProgress>.Update.Set(up => up.Catch, value),
            "duel_win" => Builders<UserProgress>.Update.Set(up => up.DuelWin, value),
            "duel_lose" => Builders<UserProgress>.Update.Set(up => up.DuelLose, value),
            "ev" => Builders<UserProgress>.Update.Set(up => up.Ev, value),
            "fish" => Builders<UserProgress>.Update.Set(up => up.Fish, value),
            "npc" => Builders<UserProgress>.Update.Set(up => up.Npc, value),
            "party" => Builders<UserProgress>.Update.Set(up => up.Party, value),
            "pokemon_setup" => Builders<UserProgress>.Update.Set(up => up.PokemonSetup, value),
            "vote" => Builders<UserProgress>.Update.Set(up => up.Vote, value),
            "game_wordsearch" => Builders<UserProgress>.Update.Set(up => up.GameWordSearch, value),
            "game_slots" => Builders<UserProgress>.Update.Set(up => up.GameSlots, value),
            "game_slots_win" => Builders<UserProgress>.Update.Set(up => up.GameSlotsWin, value),
            _ => throw new ArgumentException($"Unknown progress key: {key}")
        };

        await mongoService.UserProgress.UpdateOneAsync(
            up => up.UserId == userProgress.UserId,
            updateDefinition);
    }

    /// <summary>
    ///     Updates user progress for a specific field by incrementing the current value.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="progressKey">The progress field to update.</param>
    /// <param name="increment">The amount to increment by.</param>
    private async Task IncrementUserProgressAsync(ulong userId, string progressKey, int increment = 1)
    {
        try
        {
            var userProgress = await mongoService.UserProgress.Find(up => up.UserId == userId).FirstOrDefaultAsync();
            
            if (userProgress == null)
            {
                userProgress = new UserProgress
                {
                    UserId = userId,
                    Breed = 0,
                    Catch = 0,
                    DuelLose = 0,
                    DuelWin = 0,
                    Ev = 0,
                    Fish = 0,
                    Npc = 0,
                    Party = 0,
                    PokemonSetup = 0,
                    Vote = 0,
                    GameWordSearch = 0,
                    GameSlots = 0,
                    GameSlotsWin = 0
                };
                await mongoService.UserProgress.InsertOneAsync(userProgress);
            }

            var currentValue = GetProgressValue(userProgress, progressKey);
            var newValue = currentValue + increment;
            await UpdateProgressValue(userProgress, progressKey, newValue);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error incrementing user progress for user {UserId}, key {ProgressKey}", userId, progressKey);
        }
    }

    #endregion

    #region XP and Level System

    /// <summary>
    ///     Calculates the XP required for a given level.
    /// </summary>
    /// <param name="level">The level to calculate XP for.</param>
    /// <returns>The XP required for the level.</returns>
    public static int XpForLevel(int level)
    {
        return (int)(MissionConstants.BaseXp * Math.Pow(level, MissionConstants.LevelExponent));
    }

    /// <summary>
    ///     Calculates the level and remaining XP based on current XP.
    /// </summary>
    /// <param name="currentXp">The current XP amount.</param>
    /// <returns>A tuple containing the level and remaining XP.</returns>
    public static (int level, int remainingXp) LevelForXp(int currentXp)
    {
        var level = (int)Math.Pow(currentXp / (double)MissionConstants.BaseXp, 1.0 / MissionConstants.LevelExponent);
        var remainingXp = currentXp - XpForLevel(level);
        return (level, remainingXp);
    }

    /// <summary>
    ///     Adds XP to a user and handles level-ups.
    /// </summary>
    /// <param name="userId">The user ID to add XP to.</param>
    /// <param name="xpToAdd">The amount of XP to add.</param>
    /// <returns>A tuple indicating if the user leveled up, their new level, and new XP.</returns>
    public async Task AddXpAndCheckLevelUpAsync(ulong userId, int xpToAdd)
    {
        try
        {
            await using var db = await dbProvider.GetConnectionAsync();
            var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
            
            if (user == null)
            {
                Log.Warning("User not found with ID {UserId}", userId);
                return;
            }

            var currentXp = user.CurrentXp ?? 0;
            var currentLevel = user.Level ?? 1;

            // Get XP requirements from MongoDB
            var levelsDoc = await mongoService.Levels
                .Find(_ => true)
                .FirstOrDefaultAsync();

            if (levelsDoc?.LevelRequirements == null)
            {
                Log.Warning("XP requirements not found in database");
                return;
            }

            var xpRequirements = levelsDoc.LevelRequirements;

            // Process any pending level-ups first
            var newLevel = currentLevel;
            var workingXp = currentXp;

            while (xpRequirements.ContainsKey(newLevel.ToString()) && workingXp >= Convert.ToInt32(xpRequirements[newLevel.ToString()]))
            {
                workingXp -= Convert.ToInt32(xpRequirements[newLevel.ToString()]);
                newLevel++;
                
                // Fire level up event
                if (UserLeveledUp != null)
                    _ = Task.Run(() => UserLeveledUp(userId, newLevel));
            }

            // Add new XP
            var finalXp = workingXp + xpToAdd;

            // Check for more level-ups after adding XP
            while (xpRequirements.ContainsKey(newLevel.ToString()) && finalXp >= Convert.ToInt32(xpRequirements[newLevel.ToString()]))
            {
                finalXp -= Convert.ToInt32(xpRequirements[newLevel.ToString()]);
                newLevel++;
                
                // Fire level up event
                if (UserLeveledUp != null)
                    _ = Task.Run(() => UserLeveledUp(userId, newLevel));
            }

            // Update user data
            await db.Users.Where(u => u.UserId == userId)
                .Set(u => u.CurrentXp, finalXp)
                .Set(u => u.Level, newLevel)
                .UpdateAsync();

            return;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error adding XP and checking level up for user {UserId}", userId);
            return;
        }
    }

    #endregion

    #region Shop Operations

    /// <summary>
    ///     Gets the user's crystal slime balance.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>The user's crystal slime balance.</returns>
    public async Task<int> GetUserCrystalSlimeAsync(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        return user?.CrystalSlime ?? 0;
    }

    /// <summary>
    ///     Deducts crystal slime from a user's balance.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="amount">The amount to deduct.</param>
    /// <returns>True if successful, false if insufficient balance.</returns>
    public async Task<bool> DeductCrystalSlimeAsync(ulong userId, int amount)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        
        if (user == null || (user.CrystalSlime ?? 0) < amount)
            return false;

        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.CrystalSlime, u => (u.CrystalSlime ?? 0) - amount)
            .UpdateAsync();
        return true;
    }

    /// <summary>
    ///     Adds an item to the user's inventory.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="itemName">The item name.</param>
    /// <param name="quantity">The quantity to add.</param>
    public async Task AddItemToInventoryAsync(ulong userId, string itemName, int quantity = 1)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        
        if (user == null) return;

        // Parse existing items JSON
        var items = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(user.Items ?? "{}");
        items ??= new Dictionary<string, int>();
        
        items[itemName] = (items.ContainsKey(itemName) ? items[itemName] : 0) + quantity;
        
        var serializedItems = System.Text.Json.JsonSerializer.Serialize(items);
        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.Items, serializedItems)
            .UpdateAsync();
    }

    /// <summary>
    ///     Adds MewCoins to a user's balance.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="amount">The amount to add.</param>
    public async Task AddMewCoinsAsync(ulong userId, ulong amount)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        
        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.MewCoins, u => (u.MewCoins ?? 0) + amount)
            .UpdateAsync();
    }

    /// <summary>
    ///     Adds VIP tokens to a user's balance.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="amount">The amount to add.</param>
    public async Task AddVipTokensAsync(ulong userId, int amount)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        
        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.VipTokens, u => u.VipTokens + amount)
            .UpdateAsync();
    }

    /// <summary>
    ///     Gets the user's chain and hunt information.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>A tuple containing chain and hunt information.</returns>
    public async Task<(int chain, string? hunt)> GetUserChainAndHuntAsync(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        return (user?.Chain ?? 0, user?.Hunt);
    }

    /// <summary>
    ///     Increases the user's chain by a specified amount.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="amount">The amount to increase the chain by.</param>
    public async Task IncreaseChainAsync(ulong userId, int amount)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        
        await db.Users.Where(u => u.UserId == userId)
            .Set(u => u.Chain, u => u.Chain + amount)
            .UpdateAsync();
    }

    #endregion

    #region Mission Data Retrieval

    /// <summary>
    ///     Gets all active missions.
    /// </summary>
    /// <returns>List of active missions.</returns>
    public async Task<List<Mission>> GetActiveMissionsAsync()
    {
        return await mongoService.Missions
            .Find(m => m.Active)
            .ToListAsync();
    }

    /// <summary>
    ///     Gets the user's progress for all missions.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>The user's progress data.</returns>
    public async Task<UserProgress?> GetUserProgressAsync(ulong userId)
    {
        return await mongoService.UserProgress
            .Find(up => up.UserId == userId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    ///     Gets the user's XP and level information.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>A tuple containing current XP, level, and crystal slime.</returns>
    public async Task<(int currentXp, int level, int crystalSlime)> GetUserXpInfoAsync(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        
        if (user == null)
            return (0, 1, 0);

        return (user.CurrentXp ?? 0, user.Level ?? 1, user.CrystalSlime ?? 0);
    }

    /// <summary>
    ///     Gets the user's selected title.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>The user's selected title or default title.</returns>
    public async Task<string> GetUserTitleAsync(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        
        if (user == null || string.IsNullOrEmpty(user.SelectedTitle))
            return MissionConstants.DefaultUserTitle;
        
        return user.SelectedTitle;
    }

    #endregion

    #region Event Registration

    /// <summary>
    ///     Registers event handlers for mission-related events.
    /// </summary>
    public void RegisterEventHandlers()
    {
        client.Gateway.MessageCreated += HandleMessageReceived;
        
        // Subscribe to custom events
        PokemonBred += HandlePokemonBred;
        PokemonCaught += HandlePokemonCaught;
        PokemonHatched += HandlePokemonHatched;
        PokemonFished += HandlePokemonFished;
        DuelCompleted += HandleDuelCompleted;
        NpcBattleCompleted += HandleNpcBattleCompleted;
        UserLeveledUp += HandleUserLeveledUp;
        EvTraining += HandleEvTraining;
        PokemonSetup += HandlePokemonSetup;
        PartyRegistered += HandlePartyRegistered;
        XpGained += HandleXpGained;
        GameWordSearchCompleted += HandleGameWordSearchCompleted;
        GameSlotsPlayed += HandleGameSlotsPlayed;
    }

    #endregion

    #region Event Handlers

    private async Task HandleMessageReceived(SocketMessage message)
    {
        // Handle vote events from TopGG
        if (message.Channel.Id == MissionConstants.VoteChannelId && message.Content.Contains("topgg"))
        {
            var parts = message.Content.Split();
            if (parts.Length > 0 && ulong.TryParse(parts[0], out var userId))
            {
                await ProcessVoteEvent(userId);
            }
        }
    }

    private async Task ProcessVoteEvent(ulong userId)
    {
        try
        {
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("vote");
            var xpGained = MissionConstants.VoteXp + MissionConstants.BonusXp;

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                    }
                }
            }

            if (XpGained != null)
                await XpGained(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error processing vote event for user {UserId}", userId);
        }
    }

    private async Task HandlePokemonBred(CommandContext interaction, Database.Linq.Models.Pokemon.Pokemon pokemon, bool shadow)
    {
        try
        {
            var userId = interaction.User.Id;
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("breed");
            
            var ivSum = pokemon.HpIv + pokemon.AttackIv + pokemon.DefenseIv + 
                       pokemon.SpecialAttackIv + pokemon.SpecialDefenseIv + pokemon.SpeedIv;
            var ivPercent = (ivSum / (double)MissionConstants.MaxIvTotal) * 100;
            
            var bonus = ivSum >= MissionConstants.HighIvThreshold ? 1 : 0;
            var xpGained = MissionConstants.BreedXp + bonus;

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    if (ivPercent >= mission.Iv)
                    {
                        var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                        if (completed)
                        {
                            xpGained += 1 + (mission.Reward / 2);
                            await SendMissionCompletionMessage(interaction, mission.Reward);
                        }
                    }
                }
            }

            if (XpGained != null)
                await XpGained(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling pokemon bred event");
        }
    }

    private async Task HandlePokemonCaught(CommandContext interaction)
    {
        try
        {
            var userId = interaction.User.Id;
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("catch");
            var xpGained = MissionConstants.CatchXp + MissionConstants.BonusXp;

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                        await SendMissionCompletionMessage(interaction, mission.Reward);
                    }
                }
            }

            if (XpGained != null)
                await XpGained(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling pokemon caught event");
        }
    }

    private async Task HandlePokemonHatched(SocketMessage message, Database.Linq.Models.Pokemon.Pokemon pokemon)
    {
        try
        {
            var userId = message.Author.Id;
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("hatch");
            var xpGained = MissionConstants.HatchXp + MissionConstants.BonusXp;

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                        // Send completion message to channel
                        await SendMissionCompletionMessage(message.Channel, mission.Reward);
                    }
                }
            }

            if (XpGained != null)
                await XpGained(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling pokemon hatched event");
        }
    }

    private async Task HandlePokemonFished(SocketMessage message, User user)
    {
        try
        {
            var userId = message.Author.Id;
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("fish");
            var xpGained = MissionConstants.FishXp;

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                        // Send completion message to channel
                        await SendMissionCompletionMessage(message.Channel, mission.Reward);
                    }
                }
            }

            if (XpGained != null)
                await XpGained(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling pokemon fished event");
        }
    }

    private async Task HandleDuelCompleted(CommandContext interaction, object battle, object winner)
    {
        try
        {
            var userId = interaction.User.Id;
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("duel");
            var xpGained = MissionConstants.DuelXp;

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                        await SendMissionCompletionMessage(interaction, mission.Reward);
                    }
                }
            }

            if (XpGained != null)
                await XpGained(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling duel completed event");
        }
    }

    private async Task HandleNpcBattleCompleted(CommandContext interaction, object winner, object battle)
    {
        try
        {
            var userId = interaction.User.Id;
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("npc");
            var xpGained = MissionConstants.NpcXp;

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                        await SendMissionCompletionMessage(interaction, mission.Reward);
                    }
                }
            }

            if (XpGained != null)
                await XpGained(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling NPC battle completed event");
        }
    }

    private async Task HandleUserLeveledUp(ulong userId, int newLevel)
    {
        try
        {
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("level_up");
            var xpGained = MissionConstants.VoteXp + MissionConstants.BonusXp;

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                    }
                }
            }

            await botLog.SendAsync($"<@{userId}> is now level {newLevel}!");

            if (XpGained != null)
                await XpGained(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling user leveled up event");
        }
    }

    private async Task HandleEvTraining(CommandContext interaction, int amount)
    {
        try
        {
            var userId = interaction.User.Id;
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("ev");
            var xpGained = MissionConstants.EvTrainingXp + MissionConstants.BonusXp;

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, amount);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                        await SendMissionCompletionMessage(interaction, mission.Reward);
                    }
                }
            }

            if (XpGained != null)
                await XpGained(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling EV training event");
        }
    }

    private async Task HandlePokemonSetup(CommandContext interaction, string actionType, int amount)
    {
        try
        {
            var userId = interaction.User.Id;
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("pokemon_setup");
            var xpGained = MissionConstants.PokemonSetupXp;

            var targetNumber = actionType switch
            {
                "level" => 1,
                "moves" => 2,
                "ev" => 3,
                _ => 1
            };

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, amount, targetNumber);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                        await SendMissionCompletionMessage(interaction, mission.Reward);
                    }
                }
            }

            if (XpGained != null)
                await XpGained(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling pokemon setup event");
        }
    }

    private async Task HandlePartyRegistered(CommandContext interaction)
    {
        try
        {
            var userId = interaction.User.Id;
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("party");
            var xpGained = MissionConstants.PartyRegistrationXp;

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                        await SendMissionCompletionMessage(interaction, mission.Reward);
                    }
                }
            }

            if (XpGained != null)
                await XpGained(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling party registered event");
        }
    }

    private async Task HandleXpGained(ulong userId, int xpGained)
    {
        try
        {
            await AddXpAndCheckLevelUpAsync(userId, xpGained);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling XP gained event");
        }
    }

    /// <summary>
    ///     Handles word search game completion events.
    /// </summary>
    /// <param name="interaction">The context of the game's command.</param>
    /// <param name="wordsFound">The number of words found in the game.</param>
    private async Task HandleGameWordSearchCompleted(CommandContext interaction, int wordsFound)
    {
        try
        {
            var userId = interaction.User.Id;
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("game_wordsearch");
            var xpGained = MissionConstants.GameWordSearchXp;

            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                        await SendMissionCompletionMessage(interaction, mission.Reward);
                    }
                }
            }

            await IncrementUserProgressAsync(userId, "game_wordsearch", 1);
            await XpGained?.Invoke(userId, xpGained)!;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling word search game completion");
        }
    }

    /// <summary>
    ///     Handles slot machine game played events.
    /// </summary>
    /// <param name="interaction">The context of the game's command.</param>
    /// <param name="won">Whether the player won the slot machine game.</param>
    private async Task HandleGameSlotsPlayed(CommandContext interaction, bool won)
    {
        try
        {
            var userId = interaction.User.Id;
            var activeMissions = await GetActiveMissionsWithKeyAndIvAsync("game_slots");
            var activeWinMissions = won ? await GetActiveMissionsWithKeyAndIvAsync("game_slots_win") : new List<MissionInfo>();
            var xpGained = MissionConstants.GameSlotsXp + (won ? MissionConstants.GameSlotsWinXp : 0);

            // Process slot play missions
            if (activeMissions.Count > 0)
            {
                foreach (var mission in activeMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                    if (completed)
                    {
                        xpGained += 1 + (mission.Reward / 2);
                        await SendMissionCompletionMessage(interaction, mission.Reward);
                    }
                }
            }

            // Process slot win missions if applicable
            if (won && activeWinMissions.Count > 0)
            {
                foreach (var mission in activeWinMissions)
                {
                    var completed = await ProcessUserMissionProgressAsync(userId, mission.Id, 1);
                    if (completed)
                    {
                        xpGained += 2 + (mission.Reward / 2);
                        await SendMissionCompletionMessage(interaction, mission.Reward);
                    }
                }
            }

            await IncrementUserProgressAsync(userId, "game_slots", 1);
            if (won)
            {
                await IncrementUserProgressAsync(userId, "game_slots_win", 1);
            }

            await XpGained?.Invoke(userId, xpGained)!;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling slot machine game event");
        }
    }

    private static Task SendMissionCompletionMessage(CommandContext interaction, int reward) =>
        SendMissionCompletionMessage(interaction.Channel, reward);

    private static async Task SendMissionCompletionMessage(Channel channel, int reward)
    {
        try
        {
            var embed = new EmbedBuilder()
                .WithTitle("Congratulations!\nTake this reward!")
                .WithDescription($"{reward} shards of Crystallized Ditto Slime.")
                .WithColor(MissionConstants.MissionCompleteColor)
                .Build();

            await channel.SendMessageAsync("Daily Mission Completed.", [embed]);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error sending mission completion message");
        }
    }

    #endregion

    #region Public Event Triggers

    /// <summary>
    ///     Triggers the GameWordSearchCompleted event.
    /// </summary>
    /// <param name="interaction">The context of the game's command.</param>
    /// <param name="wordsFound">The number of words found in the game.</param>
    public async Task TriggerGameWordSearchCompletedAsync(CommandContext interaction, int wordsFound)
    {
        if (GameWordSearchCompleted != null)
            await GameWordSearchCompleted(interaction, wordsFound);
    }

    /// <summary>
    ///     Triggers the GameSlotsPlayed event.
    /// </summary>
    /// <param name="interaction">The context of the game's command.</param>
    /// <param name="won">Whether the player won the slot machine game.</param>
    public async Task TriggerGameSlotsPlayedAsync(CommandContext interaction, bool won)
    {
        if (GameSlotsPlayed != null)
            await GameSlotsPlayed(interaction, won);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    ///     Fires a pokemon bred event.
    /// </summary>
    public async Task FirePokemonBredEvent(CommandContext interaction, Database.Linq.Models.Pokemon.Pokemon pokemon, bool shadow = false)
    {
        if (PokemonBred != null)
            await PokemonBred(interaction, pokemon, shadow);
    }

    /// <summary>
    ///     Fires a pokemon caught event.
    /// </summary>
    public async Task FirePokemonCaughtEvent(CommandContext interaction)
    {
        if (PokemonCaught != null)
            await PokemonCaught(interaction);
    }

    /// <summary>
    ///     Fires a pokemon hatched event.
    /// </summary>
    public async Task FirePokemonHatchedEvent(SocketMessage message, Database.Linq.Models.Pokemon.Pokemon pokemon)
    {
        if (PokemonHatched != null)
            await PokemonHatched(message, pokemon);
    }

    /// <summary>
    ///     Fires a pokemon fished event.
    /// </summary>
    public async Task FirePokemonFishedEvent(SocketMessage message, User user)
    {
        if (PokemonFished != null)
            await PokemonFished(message, user);
    }

    /// <summary>
    ///     Fires a duel completed event.
    /// </summary>
    public async Task FireDuelCompletedEvent(CommandContext interaction, object battle, object winner)
    {
        if (DuelCompleted != null)
            await DuelCompleted(interaction, battle, winner);
    }

    /// <summary>
    ///     Fires an NPC battle completed event.
    /// </summary>
    public async Task FireNpcBattleCompletedEvent(CommandContext interaction, object winner, object battle)
    {
        if (NpcBattleCompleted != null)
            await NpcBattleCompleted(interaction, winner, battle);
    }

    /// <summary>
    ///     Fires an EV training event.
    /// </summary>
    public async Task FireEvTrainingEvent(CommandContext interaction, int amount = 1)
    {
        if (EvTraining != null)
            await EvTraining(interaction, amount);
    }

    /// <summary>
    ///     Fires a pokemon setup event.
    /// </summary>
    public async Task FirePokemonSetupEvent(CommandContext interaction, string actionType, int amount)
    {
        if (PokemonSetup != null)
            await PokemonSetup(interaction, actionType, amount);
    }

    /// <summary>
    ///     Fires a party registered event.
    /// </summary>
    public async Task FirePartyRegisteredEvent(CommandContext interaction)
    {
        if (PartyRegistered != null)
            await PartyRegistered(interaction);
    }

    #endregion
}

/// <summary>
///     Represents mission information for active missions.
/// </summary>
public class MissionInfo
{
    /// <summary>
    ///     The mission ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    ///     The IV requirement for the mission.
    /// </summary>
    public int Iv { get; set; }

    /// <summary>
    ///     The reward amount for completing the mission.
    /// </summary>
    public int Reward { get; set; }
}