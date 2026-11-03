using System.Text;
using System.Text.Json;
using EeveeCore.Modules.Trade.Models;
using Serilog;
using TokenType = EeveeCore.Modules.Trade.Models.TokenType;
using LinqToDB;
using LinqToDB.Async;
using UserPokemonOwnership = EeveeCore.Database.Linq.Models.Pokemon.UserPokemonOwnership;

namespace EeveeCore.Modules.Trade.Services;

/// <summary>
///     Service for managing trade sessions between users.
///     Handles session creation, item management, validation, and trade execution.
/// </summary>
public class TradeService : INService
{
    private readonly LinqToDbConnectionProvider _context;
    private readonly IDataCache _cache;
    private readonly BotLog _botLog;
    private readonly ITradeLockService _tradeLockService;
    private readonly TradeEvolutionService _evolutionService;
    
    // In-memory trade session cache
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TradeSession> _activeSessions = new();
    
    private const string TradeSessionPrefix = "trade_session:";
    private const int SessionTimeoutMinutes = 6;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TradeService" /> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="cache">The Redis cache service.</param>
    /// <param name="botLog">The bot event log.</param>
    /// <param name="tradeLockService">The trade lock service.</param>
    /// <param name="evolutionService">The trade evolution service.</param>
    public TradeService(LinqToDbConnectionProvider context, IDataCache cache, BotLog botLog, ITradeLockService tradeLockService, TradeEvolutionService evolutionService)
    {
        _context = context;
        _cache = cache;
        _botLog = botLog;
        _tradeLockService = tradeLockService;
        _evolutionService = evolutionService;
    }

    /// <summary>
    ///     Creates a new trade session between two users.
    /// </summary>
    /// <param name="player1Id">The first player's Discord user ID.</param>
    /// <param name="player2Id">The second player's Discord user ID.</param>
    /// <param name="channelId">The channel where the trade is taking place.</param>
    /// <param name="guildId">The guild where the trade is taking place.</param>
    /// <returns>A TradeResult containing the created session or error information.</returns>
    public async Task<TradeResult> CreateTradeSessionAsync(ulong player1Id, ulong player2Id, ulong channelId, ulong guildId)
    {
        // Validate users exist and aren't trade locked
        var validationResult = await ValidateTradeParticipantsAsync(player1Id, player2Id);
        if (!validationResult.Success)
        {
            return validationResult;
        }

        // Check if either user is already in a trade
        if (await IsUserInTradeAsync(player1Id) || await IsUserInTradeAsync(player2Id))
        {
            return TradeResult.Failure("One of the users is already in an active trade session.");
        }

        var session = new TradeSession
        {
            Player1Id = player1Id,
            Player2Id = player2Id,
            ChannelId = channelId,
            GuildId = guildId,
            Status = TradeStatus.Active,
            ExpiresAt = DateTime.UtcNow.AddMinutes(SessionTimeoutMinutes)
        };

        // Store session in memory and Redis
        _activeSessions[session.SessionId] = session;
        await StoreSessionInRedisAsync(session);

        return TradeResult.FromSuccess("Trade session created successfully.", session);
    }

    /// <summary>
    ///     Adds a Pokemon to a trade session.
    /// </summary>
    /// <param name="sessionId">The trade session ID.</param>
    /// <param name="userId">The user adding the Pokemon.</param>
    /// <param name="pokemonPosition">The position of the Pokemon in the user's collection.</param>
    /// <returns>A TradeResult indicating success or failure.</returns>
    public async Task<TradeResult> AddPokemonToTradeAsync(Guid sessionId, ulong userId, int pokemonPosition)
    {
        var session = await GetTradeSessionAsync(sessionId);
        if (session == null)
        {
            return TradeResult.Failure("Trade session not found.");
        }

        if (!session.IsParticipant(userId))
        {
            return TradeResult.Failure("You are not a participant in this trade session.");
        }

        if (session.Status != TradeStatus.Active)
        {
            return TradeResult.Failure("Trade session is not active.");
        }

        // Validate and get Pokemon
        var pokemon = await GetPokemonByPosition(userId, (ulong)pokemonPosition);
        if (pokemon == null)
        {
            return TradeResult.Failure("You don't have that Pokemon or that Pokemon is currently in the market!");
        }

        // Validate Pokemon is tradeable
        var validationResult = ValidatePokemonForTrade(pokemon, pokemonPosition);
        if (!validationResult.Success)
        {
            return validationResult;
        }

        // Check if Pokemon is already in trade
        if (session.GetPokemonBy(userId).Any(p => p.PokemonId == pokemon.Id))
        {
            return TradeResult.Failure("You already have this Pokemon in your trade.");
        }

        // Add to trade
        var entry = TradeEntry.ForPokemon(userId, pokemon);
        session.AddEntry(entry);
        await UpdateSessionInRedisAsync(session);

        return TradeResult.FromSuccess($"Added {pokemon.PokemonName} to your trade list.", entry);
    }

    /// <summary>
    ///     Adds credits to a trade session.
    /// </summary>
    /// <param name="sessionId">The trade session ID.</param>
    /// <param name="userId">The user adding credits.</param>
    /// <param name="credits">The number of credits to add.</param>
    /// <returns>A TradeResult indicating success or failure.</returns>
    public async Task<TradeResult> AddCreditsToTradeAsync(Guid sessionId, ulong userId, ulong credits)
    {
        var session = await GetTradeSessionAsync(sessionId);
        if (session == null)
        {
            return TradeResult.Failure("Trade session not found.");
        }

        if (!session.IsParticipant(userId))
        {
            return TradeResult.Failure("You are not a participant in this trade session.");
        }

        if (session.Status != TradeStatus.Active)
        {
            return TradeResult.Failure("Trade session is not active.");
        }

        if (credits == 0)
        {
            return TradeResult.Failure("You need to add at least 1 credit!");
        }

        // Check user has enough credits
        var userCredits = await GetUserCreditsAsync(userId);
        if (userCredits < credits)
        {
            return TradeResult.Failure("You don't have enough credits to cover that amount!");
        }

        // Remove existing credits entry for this user
        session.RemoveEntriesBy(userId, TradeItemType.Credits);

        // Add new credits entry
        var entry = TradeEntry.ForCredits(userId, credits);
        session.AddEntry(entry);
        await UpdateSessionInRedisAsync(session);

        return TradeResult.FromSuccess($"Added {credits:N0} credits to the trade.", entry);
    }

    /// <summary>
    ///     Adds tokens to a trade session.
    /// </summary>
    /// <param name="sessionId">The trade session ID.</param>
    /// <param name="userId">The user adding tokens.</param>
    /// <param name="tokenType">The type of tokens to add.</param>
    /// <param name="count">The number of tokens to add.</param>
    /// <returns>A TradeResult indicating success or failure.</returns>
    public async Task<TradeResult> AddTokensToTradeAsync(Guid sessionId, ulong userId, TokenType tokenType, int count)
    {
        var session = await GetTradeSessionAsync(sessionId);
        if (session == null)
        {
            return TradeResult.Failure("Trade session not found.");
        }

        if (!session.IsParticipant(userId))
        {
            return TradeResult.Failure("You are not a participant in this trade session.");
        }

        if (session.Status != TradeStatus.Active)
        {
            return TradeResult.Failure("Trade session is not active.");
        }

        if (count <= 0)
        {
            return TradeResult.Failure("You need to add at least 1 token!");
        }

        // Check user has enough tokens
        var userTokens = await GetUserTokensAsync(userId);
        if (!userTokens.TryGetValue(tokenType.GetDisplayName(), out var availableTokens) || availableTokens < count)
        {
            return TradeResult.Failure($"You don't have enough {tokenType.GetDisplayName()} tokens!");
        }

        // Add tokens to existing entry or create new one
        var existingEntry = session.GetEntriesBy(userId)
            .FirstOrDefault(e => e.ItemType == TradeItemType.Tokens && e.TokenType == tokenType);

        if (existingEntry != null)
        {
            existingEntry.TokenCount += count;
        }
        else
        {
            var entry = TradeEntry.ForTokens(userId, tokenType, count);
            session.AddEntry(entry);
        }

        await UpdateSessionInRedisAsync(session);

        return TradeResult.FromSuccess($"Added {count} {tokenType.GetDisplayName()} tokens to the trade.");
    }

    /// <summary>
    ///     Removes a Pokemon from a trade session by collection position.
    /// </summary>
    /// <param name="sessionId">The trade session ID.</param>
    /// <param name="userId">The user removing the Pokemon.</param>
    /// <param name="pokemonPosition">The position of the Pokemon in the user's collection.</param>
    /// <returns>A TradeResult indicating success or failure.</returns>
    public async Task<TradeResult> RemovePokemonFromTradeAsync(Guid sessionId, ulong userId, ulong pokemonPosition)
    {
        var session = await GetTradeSessionAsync(sessionId);
        if (session == null)
        {
            return TradeResult.Failure("Trade session not found.");
        }

        // Get Pokemon to find its ID
        var pokemon = await GetPokemonByPosition(userId, pokemonPosition);
        if (pokemon == null)
        {
            return TradeResult.Failure("Pokemon not found.");
        }

        // Find and remove the entry - check if this specific Pokemon ID is in the trade
        var entry = session.GetPokemonBy(userId).FirstOrDefault(p => p.PokemonId == pokemon.Id);
        if (entry == null)
        {
            // Show user which Pokemon are actually in their trade for debugging
            var pokemonInTrade = session.GetPokemonBy(userId).ToList();
            if (pokemonInTrade.Any())
            {
                var tradeList = string.Join(", ", pokemonInTrade
                    .Where(e => e.PokemonId.HasValue)
                    .Take(3) // Show max 3 for brevity
                    .Select(e => $"ID:{e.PokemonId}"));
                return TradeResult.Failure($"Position {pokemonPosition} ({pokemon.PokemonName} ID:{pokemon.Id}) is not in your trade list! Current trade: {tradeList}");
            }
            return TradeResult.Failure($"You do not have {pokemon.PokemonName} (position {pokemonPosition}) in your trade list!");
        }

        session.RemoveEntry(entry.Id);
        await UpdateSessionInRedisAsync(session);

        return TradeResult.FromSuccess($"Removed {pokemon.PokemonName} from your trade!");
    }

    /// <summary>
    ///     Removes tokens a user offered from a trade session.
    /// </summary>
    /// <param name="sessionId">The trade session ID.</param>
    /// <param name="userId">The user removing tokens.</param>
    /// <param name="tokenType">The type of tokens to remove.</param>
    /// <param name="count">The number of tokens to remove.</param>
    /// <returns>A TradeResult indicating success or failure.</returns>
    public async Task<TradeResult> RemoveTokensFromTradeAsync(Guid sessionId, ulong userId, TokenType tokenType, int count)
    {
        var session = await GetTradeSessionAsync(sessionId);
        if (session == null)
        {
            return TradeResult.Failure("Trade session not found.");
        }

        if (count <= 0)
        {
            return TradeResult.Failure("Invalid token count, please enter a positive number.");
        }

        if (!session.GetTokensBy(userId).TryGetValue(tokenType, out var currentAmount) || currentAmount < count)
        {
            return TradeResult.Failure($"You don't have {count} {tokenType.GetDisplayName()} tokens in this trade!");
        }

        var remaining = count;
        foreach (var entry in session.GetEntriesBy(userId)
                     .Where(e => e.ItemType == TradeItemType.Tokens && e.TokenType == tokenType)
                     .ToList())
        {
            if (remaining <= 0) break;

            if (entry.TokenCount <= remaining)
            {
                remaining -= entry.TokenCount;
                session.RemoveEntry(entry.Id);
            }
            else
            {
                entry.TokenCount -= remaining;
                remaining = 0;
            }
        }

        await UpdateSessionInRedisAsync(session);

        return TradeResult.FromSuccess($"Removed {count} {tokenType.GetDisplayName()} tokens from the trade.");
    }

    /// <summary>
    ///     Gets the trade session a user is currently taking part in.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>The active session, or null when the user is not trading.</returns>
    public TradeSession? GetActiveSessionForUser(ulong userId)
    {
        return _activeSessions.Values.FirstOrDefault(s => s.IsParticipant(userId) &&
            s.Status is TradeStatus.Active or TradeStatus.PendingConfirmation or TradeStatus.Processing);
    }

    /// <summary>
    ///     Removes a Pokemon from a trade session by trade entry index.
    /// </summary>
    /// <param name="sessionId">The trade session ID.</param>
    /// <param name="userId">The user removing the Pokemon.</param>
    /// <param name="tradeEntryIndex">The index of the Pokemon in the trade list to remove (0-based).</param>
    /// <returns>A TradeResult indicating success or failure.</returns>
    public async Task<TradeResult> RemovePokemonFromTradeByIndexAsync(Guid sessionId, ulong userId, ulong tradeEntryIndex)
    {
        var session = await GetTradeSessionAsync(sessionId);
        if (session == null)
        {
            return TradeResult.Failure("Trade session not found.");
        }

        // Get user's Pokemon entries in the trade
        var userEntries = session.GetPokemonBy(userId).ToList();
        
        if (tradeEntryIndex >= (ulong)userEntries.Count)
        {
            return TradeResult.Failure("Invalid trade entry index.");
        }

        var entry = userEntries[(int)tradeEntryIndex];
        
        // Get the Pokemon name for feedback
        var pokemonName = "Pokemon";
        if (entry.PokemonId.HasValue)
        {
            await using var db = await _context.GetConnectionAsync();
            var pokemon = await db.UserPokemon
                .FirstOrDefaultAsync(p => p.Id == entry.PokemonId.Value);
            pokemonName = pokemon?.PokemonName ?? "Pokemon";
        }

        session.RemoveEntry(entry.Id);
        await UpdateSessionInRedisAsync(session);

        return TradeResult.FromSuccess($"Removed {pokemonName} from your trade!");
    }

    /// <summary>
    ///     Attempts to execute a trade after both users have confirmed.
    /// </summary>
    /// <param name="sessionId">The trade session ID.</param>
    /// <returns>A TradeResult indicating success or failure.</returns>
    public async Task<TradeResult> ExecuteTradeAsync(Guid sessionId)
    {
        var session = await GetTradeSessionAsync(sessionId);
        if (session == null)
        {
            return TradeResult.Failure("Trade session not found.");
        }

        if (session.Status != TradeStatus.PendingConfirmation && session.Status != TradeStatus.Processing)
        {
            return TradeResult.Failure("Trade is not ready for execution.");
        }

        if (!session.IsBothConfirmed())
        {
            return TradeResult.Failure("Both users must confirm the trade.");
        }

        if (session.IsAttemptingTrade)
        {
            return TradeResult.Failure("Trade is already being processed.");
        }

        // Fraud detection is now done during confirmation stage to prevent delays
        // Only set status if not already processing (race condition protection)
        if (session.Status != TradeStatus.Processing)
        {
            session.Status = TradeStatus.Processing;
        }
        session.IsAttemptingTrade = true;
        await UpdateSessionInRedisAsync(session);

        try
        {
            await using var db = await _context.GetConnectionAsync();

            await using var transaction = await db.BeginTransactionAsync();

            // Revalidate all items before executing
            var revalidationResult = await RevalidateTradeItemsAsync(session);
            if (!revalidationResult.Success)
            {
                session.Status = TradeStatus.Failed;
                await UpdateSessionInRedisAsync(session);
                return revalidationResult;
            }

            // Execute the trade
            await TransferTokensAsync(session);
            await TransferCreditsAsync(session);
            var pokemonEvolutions = await TransferPokemonAsync(session);

            await transaction.CommitAsync();

            // Mark session as completed
            session.Status = TradeStatus.Completed;
            await UpdateSessionInRedisAsync(session);

            // Log the trade
            await LogTradeAsync(session);

            return TradeResult.FromSuccess("Trade completed successfully!");
        }
        catch (Exception ex)
        {
            session.Status = TradeStatus.Failed;
            session.IsAttemptingTrade = false;
            await UpdateSessionInRedisAsync(session);
            
            return TradeResult.Failure($"Trade execution failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Clears trade locks for both participants when a session is not found.
    /// </summary>
    /// <param name="userId">The current user requesting the session.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ClearOrphanedTradeLocksAsync(ulong userId)
    {
        // Clear the current user's lock
        await _tradeLockService.RemoveTradeLockAsync(userId);
        
        // Find and clear any other users locked with this user
        // Since we don't have the session, we need to check all active sessions
        var userSessions = _activeSessions.Values.Where(s => s.IsParticipant(userId)).ToList();
        
        foreach (var session in userSessions)
        {
            var otherParticipant = session.GetOtherPlayer(userId);
            if (otherParticipant.HasValue)
            {
                await _tradeLockService.RemoveTradeLockAsync(otherParticipant.Value);
            }
            
            // Remove from active sessions
            _activeSessions.TryRemove(session.SessionId, out _);
        }
    }

    /// <summary>
    ///     Gets a trade session by ID.
    /// </summary>
    /// <param name="sessionId">The session ID to retrieve.</param>
    /// <returns>The trade session if found, null otherwise.</returns>
    public async Task<TradeSession?> GetTradeSessionAsync(Guid sessionId)
    {
        // Try memory cache first
        if (_activeSessions.TryGetValue(sessionId, out var cachedSession))
        {
            return cachedSession;
        }

        // Try Redis cache
        var database = _cache.Redis.GetDatabase();
        var sessionData = await database.StringGetAsync($"{TradeSessionPrefix}{sessionId}");
        
        if (!sessionData.HasValue)
        {
            return null;
        }

        var session = JsonSerializer.Deserialize<TradeSession>((string)sessionData!);
        if (session != null)
        {
            // Check if session has expired
            if (DateTime.UtcNow > session.ExpiresAt)
            {
                // Session expired, clean it up
                await database.KeyDeleteAsync($"{TradeSessionPrefix}{sessionId}");
                await _tradeLockService.RemoveTradeLockAsync(session.Player1Id);
                await _tradeLockService.RemoveTradeLockAsync(session.Player2Id);
                return null;
            }
            
            _activeSessions[sessionId] = session;
        }

        return session;
    }

    /// <summary>
    ///     Cancels a trade session.
    /// </summary>
    /// <param name="sessionId">The session ID to cancel.</param>
    /// <param name="cancelledBy">The user who cancelled the trade.</param>
    /// <returns>A TradeResult indicating success or failure.</returns>
    public async Task<TradeResult> CancelTradeSessionAsync(Guid sessionId, ulong cancelledBy)
    {
        var session = await GetTradeSessionAsync(sessionId);
        if (session == null)
        {
            return TradeResult.Failure("Trade session not found.");
        }

        if (!session.IsParticipant(cancelledBy))
        {
            return TradeResult.Failure("You are not a participant in this trade session.");
        }

        // Remove trade locks for both participants
        await _tradeLockService.RemoveTradeLockAsync(session.Player1Id);
        await _tradeLockService.RemoveTradeLockAsync(session.Player2Id);

        session.Status = TradeStatus.Cancelled;
        
        // Update the interface to show cancelled state and disable buttons
        await UpdateCancelledTradeInterfaceAsync(session);
        
        // Remove from active sessions and Redis
        _activeSessions.TryRemove(sessionId, out _);
        var database = _cache.Redis.GetDatabase();
        await database.KeyDeleteAsync($"{TradeSessionPrefix}{sessionId}");

        return TradeResult.FromSuccess("Trade session has been cancelled.");
    }

    /// <summary>
    ///     Generates a formatted trade summary as an embed description.
    /// </summary>
    /// <param name="session">The trade session to summarize.</param>
    /// <returns>A formatted string containing the trade summary.</returns>
    public async Task<string> GenerateTradeSummaryAsync(TradeSession session)
    {
        var summary = new StringBuilder();

        // Player 1 offerings
        summary.AppendLine($"**<@{session.Player1Id}>** is offering:");
        await AppendUserOfferingsAsync(summary, session, session.Player1Id);

        summary.AppendLine("━━━━━━━━━━━━━━━━");

        // Player 2 offerings
        summary.AppendLine($"**<@{session.Player2Id}>** is offering:");
        await AppendUserOfferingsAsync(summary, session, session.Player2Id);

        return summary.ToString();
    }

    /// <summary>
    ///     Updates the trade interface message with current trade state.
    /// </summary>
    /// <param name="session">The trade session to display.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateTradeInterfaceAsync(TradeSession session)
    {
        if (session.TradeMessage == null)
        {
            return;
        }

        var tradeSummary = await GenerateTradeSummaryAsync(session);

        var embed = new EmbedBuilder()
            .WithTitle("🔄 Active Trade Session")
            .WithDescription(tradeSummary)
            .WithColor(Color.Blue)
            .AddField("Confirmed",
                $"{(session.Player1Confirmed ? "✅" : "⬜")} <@{session.Player1Id}>\n" +
                $"{(session.Player2Confirmed ? "✅" : "⬜")} <@{session.Player2Id}>")
            .AddField("Commands",
                "`trade add <ids>` · `trade remove <ids>` · `trade credits <amount>`\n" +
                "`trade tokens <type> <amount>` · `trade untokens <type> <amount>`\n" +
                (session.HasItems() ? "`trade confirm` · " : "") + "`trade cancel`")
            .WithFooter($"Session ID: {session.SessionId}")
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();

        try
        {
            await session.TradeMessage.ModifyAsync(embeds: [embed]);
        }
        catch
        {
            // Message might have been deleted or we might not have permissions
            // This is not a critical error, just continue
        }
    }

    /// <summary>
    ///     Checks if a user has any active trade sessions.
    /// </summary>
    /// <param name="userId">The user ID to check.</param>
    /// <returns>True if the user has an active trade session, false otherwise.</returns>
    public async Task<bool> HasActiveTradeSessionAsync(ulong userId)
    {
        // Check in-memory sessions first
        var hasInMemorySession = _activeSessions.Values.Any(s => 
            s.IsParticipant(userId) && 
            s.Status is TradeStatus.Active or TradeStatus.PendingConfirmation or TradeStatus.Processing);
        
        if (hasInMemorySession)
        {
            return true;
        }

        // Check Redis for any sessions this user might be in
        // This is more expensive but necessary for detecting broken states
        var database = _cache.Redis.GetDatabase();
        var server = _cache.Redis.GetServers().FirstOrDefault();
        if (server != null)
        {
            var keys = server.Keys(pattern: $"{TradeSessionPrefix}*");
            foreach (var key in keys)
            {
                try
                {
                    var sessionData = await database.StringGetAsync(key);
                    if (sessionData.HasValue)
                    {
                        var session = JsonSerializer.Deserialize<TradeSession>((string)sessionData!);
                        if (session != null && 
                            session.IsParticipant(userId) && 
                            session.Status is TradeStatus.Active or TradeStatus.PendingConfirmation or TradeStatus.Processing)
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                    // If we can't deserialize or check a session, continue to the next one
                    continue;
                }
            }
        }

        return false;
    }


    /// <summary>
    ///     Updates the trade interface to show that the trade has been cancelled.
    /// </summary>
    /// <param name="session">The cancelled trade session.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateCancelledTradeInterfaceAsync(TradeSession session)
    {
        if (session.TradeMessage == null)
        {
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("❌ Trade Cancelled")
            .WithDescription("This trade session has been cancelled. All trade locks have been removed.")
            .WithColor(Color.Red)
            .WithFooter($"Session ID: {session.SessionId}")
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();

        try
        {
            await session.TradeMessage.ModifyAsync(embeds: [embed]);
        }
        catch
        {
            // Message might have been deleted or we might not have permissions
            // This is not a critical error, just continue
        }
    }

    #region Private Helper Methods

    private async Task<TradeResult> ValidateTradeParticipantsAsync(ulong player1Id, ulong player2Id)
    {
        if (player1Id == player2Id)
        {
            return TradeResult.Failure("You cannot trade with yourself!");
        }

        await using var db = await _context.GetConnectionAsync();
        
        // Check if users exist in database
        var users = await db.Users
            .Where(u => u.UserId == player1Id || u.UserId == player2Id)
            .ToListAsync();

        if (users.Count != 2)
        {
            var missingUser = users.Any(u => u.UserId == player1Id) ? player2Id : player1Id;
            return TradeResult.Failure($"<@{missingUser}> has not started! Start with `/start` first!");
        }

        // Check for trade locks
        var tradeLocked = users.Where(u => u.TradeLock == true).ToList();
        if (tradeLocked.Any())
        {
            return TradeResult.Failure("A user is not allowed to trade.");
        }

        return TradeResult.FromSuccess("Users validated for trading.");
    }

    private async Task<bool> IsUserInTradeAsync(ulong userId)
    {
        // Check in-memory sessions first
        if (_activeSessions.Values.Any(s => s.IsParticipant(userId) && 
            s.Status is TradeStatus.Active or TradeStatus.PendingConfirmation or TradeStatus.Processing))
        {
            return true;
        }

        // Check trade lock service
        return await _tradeLockService.IsUserTradeLockedAsync(userId);
    }

    /// <summary>
    ///     Gets a Pokemon by user ID and the number it shows as in their list.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="position">The 1-based number in the collection.</param>
    /// <returns>The Pokemon at the specified position, or null if not found.</returns>
    public async Task<Database.Linq.Models.Pokemon.Pokemon?> GetPokemonByPosition(ulong userId, ulong position)
    {
        if (position <= 0)
        {
            return null;
        }

        await using var db = await _context.GetConnectionAsync();

        var ownership = await db.UserPokemonOwnerships
            .FirstOrDefaultAsync(o => o.UserId == userId && o.Position == position - 1);

        if (ownership == null)
        {
            return null;
        }

        return await db.UserPokemon
            .FirstOrDefaultAsync(p => p.Id == ownership.PokemonId);
    }

    /// <summary>
    ///     Validates if a Pokemon can be traded.
    /// </summary>
    /// <param name="pokemon">The Pokemon to validate.</param>
    /// <param name="position">The position of the Pokemon in the collection.</param>
    /// <returns>A TradeResult indicating success or failure with a message.</returns>
    public TradeResult ValidatePokemonForTrade(Database.Linq.Models.Pokemon.Pokemon pokemon, int position)
    {
        if (position == 1)
        {
            return TradeResult.Failure("You cannot trade your first Pokemon.");
        }

        if (pokemon.PokemonName == "Egg")
        {
            return TradeResult.Failure("You cannot trade eggs!");
        }

        if (!pokemon.Tradable)
        {
            return TradeResult.Failure("This Pokemon is not tradable.");
        }

        if (pokemon.Favorite)
        {
            return TradeResult.Failure($"You cannot trade your {pokemon.PokemonName} as it is favorited. Unfavorite it first with `/fav remove {position}`.");
        }

        return TradeResult.FromSuccess("Pokemon is valid for trading.");
    }

    private async Task<ulong> GetUserCreditsAsync(ulong userId)
    {
        await using var db = await _context.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        return user?.MewCoins ?? 0;
    }

    private async Task<Dictionary<string, int>> GetUserTokensAsync(ulong userId)
    {
        await using var db = await _context.GetConnectionAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user?.Tokens == null)
        {
            return new Dictionary<string, int>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(user.Tokens) ?? new Dictionary<string, int>();
        }
        catch
        {
            return new Dictionary<string, int>();
        }
    }

    private async Task StoreSessionInRedisAsync(TradeSession session)
    {
        var database = _cache.Redis.GetDatabase();
        var sessionJson = JsonSerializer.Serialize(session);
        await database.StringSetAsync($"{TradeSessionPrefix}{session.SessionId}", sessionJson, 
            TimeSpan.FromMinutes(SessionTimeoutMinutes + 1));
    }

    /// <summary>
    ///     Updates a trade session in Redis with current timestamp.
    /// </summary>
    /// <param name="session">The session to update.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateSessionInRedisAsync(TradeSession session)
    {
        session.LastUpdated = DateTime.UtcNow;
        session.ExpiresAt = DateTime.UtcNow.AddMinutes(SessionTimeoutMinutes);
        await StoreSessionInRedisAsync(session);
    }

    private async Task<TradeResult> RevalidateTradeItemsAsync(TradeSession session)
    {
        await using var db = await _context.GetConnectionAsync();

        
        // Revalidate Pokemon ownership and tradeability
        foreach (var pokemonEntry in session.TradeEntries.Where(e => e.ItemType == TradeItemType.Pokemon))
        {
            var pokemon = await db.UserPokemon.FirstOrDefaultAsync(p => p.Id == pokemonEntry.PokemonId);
            if (pokemon == null)
            {
                return TradeResult.Failure($"<@{pokemonEntry.OfferedBy}> no longer owns one or more of the Pokemon they were trading!");
            }

            var ownership = await db.UserPokemonOwnerships
                .FirstOrDefaultAsync(o => o.UserId == pokemonEntry.OfferedBy && o.PokemonId == pokemon.Id);
            if (ownership == null)
            {
                return TradeResult.Failure($"<@{pokemonEntry.OfferedBy}> no longer owns one or more of the Pokemon they were trading!");
            }
        }

        // Revalidate credits
        foreach (var playerGroup in session.TradeEntries.Where(e => e.ItemType == TradeItemType.Credits).GroupBy(e => e.OfferedBy))
        {
            var totalCredits = playerGroup.Sum(e => (long)e.Credits);
            var userCredits = await GetUserCreditsAsync(playerGroup.Key);
            if (userCredits < (ulong)totalCredits)
            {
                return TradeResult.Failure($"<@{playerGroup.Key}> does not have enough credits to complete this trade!");
            }
        }

        // Revalidate tokens
        foreach (var playerGroup in session.TradeEntries.Where(e => e.ItemType == TradeItemType.Tokens).GroupBy(e => e.OfferedBy))
        {
            var userTokens = await GetUserTokensAsync(playerGroup.Key);
            foreach (var tokenGroup in playerGroup.GroupBy(e => e.TokenType))
            {
                var totalTokens = tokenGroup.Sum(e => e.TokenCount);
                var tokenType = tokenGroup.Key?.GetDisplayName() ?? "";
                if (!userTokens.TryGetValue(tokenType, out var available) || available < totalTokens)
                {
                    return TradeResult.Failure($"<@{playerGroup.Key}> does not have enough {tokenType} tokens to complete the trade!");
                }
            }
        }

        return TradeResult.FromSuccess("All trade items revalidated successfully.");
    }

    private async Task TransferTokensAsync(TradeSession session)
    {
        await using var db = await _context.GetConnectionAsync();

        var p1Tokens = await GetUserTokensAsync(session.Player1Id);
        var p2Tokens = await GetUserTokensAsync(session.Player2Id);

        // Transfer tokens from P1 to P2
        foreach (var tokenEntry in session.GetEntriesBy(session.Player1Id).Where(e => e.ItemType == TradeItemType.Tokens))
        {
            var tokenType = tokenEntry.TokenType!.Value.GetDisplayName();
            p1Tokens[tokenType] -= tokenEntry.TokenCount;
            p2Tokens[tokenType] = p2Tokens.GetValueOrDefault(tokenType, 0) + tokenEntry.TokenCount;
        }

        // Transfer tokens from P2 to P1
        foreach (var tokenEntry in session.GetEntriesBy(session.Player2Id).Where(e => e.ItemType == TradeItemType.Tokens))
        {
            var tokenType = tokenEntry.TokenType!.Value.GetDisplayName();
            p2Tokens[tokenType] -= tokenEntry.TokenCount;
            p1Tokens[tokenType] = p1Tokens.GetValueOrDefault(tokenType, 0) + tokenEntry.TokenCount;
        }

        // Update database
        await db.Users.Where(u => u.UserId == session.Player1Id)
            .Set(u => u.Tokens, JsonSerializer.Serialize(p1Tokens))
            .UpdateAsync();
            
        await db.Users.Where(u => u.UserId == session.Player2Id)
            .Set(u => u.Tokens, JsonSerializer.Serialize(p2Tokens))
            .UpdateAsync();
    }

    private async Task TransferCreditsAsync(TradeSession session)
    {
        await using var db = await _context.GetConnectionAsync();

        var p1Credits = session.GetCreditsBy(session.Player1Id);
        var p2Credits = session.GetCreditsBy(session.Player2Id);

        if (p1Credits > 0 || p2Credits > 0)
        {
            await db.Users.Where(u => u.UserId == session.Player1Id)
                .Set(u => u.MewCoins, u => (u.MewCoins ?? 0) - p1Credits + p2Credits)
                .UpdateAsync();
                
            await db.Users.Where(u => u.UserId == session.Player2Id)
                .Set(u => u.MewCoins, u => (u.MewCoins ?? 0) - p2Credits + p1Credits)
                .UpdateAsync();
        }
    }

    private async Task<List<(ulong PokemonId, string PokemonName, ulong NewOwnerId, string? Evolution)>> TransferPokemonAsync(TradeSession session)
    {
        var evolutions = new List<(ulong PokemonId, string PokemonName, ulong NewOwnerId, string? Evolution)>();
        
        // Transfer Pokemon from P1 to P2
        foreach (var pokemonEntry in session.GetPokemonBy(session.Player1Id))
        {
            var evolution = await TransferPokemonOwnershipAsync(pokemonEntry.PokemonId!.Value, session.Player1Id, session.Player2Id);
            evolutions.Add((pokemonEntry.PokemonId!.Value, pokemonEntry.Pokemon?.PokemonName ?? "Unknown", session.Player2Id, evolution));
        }

        // Transfer Pokemon from P2 to P1
        foreach (var pokemonEntry in session.GetPokemonBy(session.Player2Id))
        {
            var evolution = await TransferPokemonOwnershipAsync(pokemonEntry.PokemonId!.Value, session.Player2Id, session.Player1Id);
            evolutions.Add((pokemonEntry.PokemonId!.Value, pokemonEntry.Pokemon?.PokemonName ?? "Unknown", session.Player1Id, evolution));
        }

        return evolutions;
    }

    private async Task<string?> TransferPokemonOwnershipAsync(ulong pokemonId, ulong fromUserId, ulong toUserId)
    {
        // Remove from old owner
        await using var db = await _context.GetConnectionAsync();

        var oldOwnership = await db.UserPokemonOwnerships
            .FirstOrDefaultAsync(o => o.UserId == fromUserId && o.PokemonId == pokemonId);
        
        if (oldOwnership != null)
        {
            await db.UserPokemonOwnerships
                .Where(o => o.UserId == fromUserId && o.PokemonId == pokemonId)
                .DeleteAsync();
        }

        // Find next available position for new owner
        var maxPosition = await db.UserPokemonOwnerships
            .Where(o => o.UserId == toUserId)
            .MaxAsync(o => (ulong?)o.Position) ?? 0;

        // Add to new owner
        var newOwnership = new UserPokemonOwnership
        {
            UserId = toUserId,
            PokemonId = pokemonId,
            Position = maxPosition + 1
        };

        await db.InsertAsync(newOwnership);

        // Update Pokemon owner and reset market status
        await db.UserPokemon
            .Where(p => p.Id == pokemonId)
            .Set(p => p.MarketEnlist, false)
            .UpdateAsync();
        
        // Get the pokemon for evolution check
        var pokemon = await db.UserPokemon.FirstAsync(p => p.Id == pokemonId);
        
        // Check for trade evolution
        return await _evolutionService.CheckTradeEvolution(pokemon);
    }

    private async Task LogTradeAsync(TradeSession session)
    {
        try
        {
            await using var db = await _context.GetConnectionAsync();
            await db.InsertAsync(new Database.Linq.Models.Game.TradeLog
            {
                Command = "trade",
                Time = DateTime.UtcNow,
                SenderId = session.Player1Id,
                SenderPokemon = PokemonIdsBy(session, session.Player1Id),
                SenderCredits = (long)session.GetCreditsBy(session.Player1Id),
                SenderTokens = TokensJson(session, session.Player1Id),
                ReceiverId = session.Player2Id,
                ReceiverPokemon = PokemonIdsBy(session, session.Player2Id),
                ReceiverCredits = (long)session.GetCreditsBy(session.Player2Id),
                ReceiverTokens = TokensJson(session, session.Player2Id)
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write trade log for session {SessionId}", session.SessionId);
        }

        try
        {
            var summary = await GenerateTradeSummaryAsync(session);
            var logMessage = $"🔄 **Trade Completed**\n" +
                            $"**Session ID**: `{session.SessionId}`\n" +
                            $"**Participants**: <@{session.Player1Id}> (`{session.Player1Id}`) ↔ <@{session.Player2Id}> (`{session.Player2Id}`)\n" +
                            $"**Completed**: <t:{new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds()}:F>\n\n" +
                            $"{summary}";

            await _botLog.SendAsync(logMessage);
        }
        catch
        {
            // Logging errors are not critical
        }
    }

    private static ulong[] PokemonIdsBy(TradeSession session, ulong userId)
    {
        return session.GetPokemonBy(userId).Where(e => e.PokemonId.HasValue).Select(e => e.PokemonId!.Value).ToArray();
    }

    private static string? TokensJson(TradeSession session, ulong userId)
    {
        var tokens = session.GetTokensBy(userId);
        return tokens.Count == 0
            ? null
            : JsonSerializer.Serialize(tokens.ToDictionary(t => t.Key.GetDisplayName(), t => t.Value));
    }

    private async Task AppendUserOfferingsAsync(StringBuilder summary, TradeSession session, ulong userId)
    {
        var userEntries = session.GetEntriesBy(userId).ToList();

        if (!userEntries.Any())
        {
            summary.AppendLine("Nothing Added");
            return;
        }

        // Pokemon
        var pokemonEntries = userEntries.Where(e => e.ItemType == TradeItemType.Pokemon).ToList();
        if (pokemonEntries.Any())
        {
            summary.AppendLine("**Pokemon:**");
            foreach (var entry in pokemonEntries)
            {
                summary.AppendLine($"- {entry.GetDisplayString()}");
            }
        }

        // Credits
        var creditsTotal = session.GetCreditsBy(userId);
        if (creditsTotal > 0)
        {
            summary.AppendLine($"**Credits**\n- `{creditsTotal:N0}`");
        }

        // Tokens
        var tokens = session.GetTokensBy(userId);
        if (tokens.Any())
        {
            summary.AppendLine("\n**Radiant Tokens:**");
            foreach (var (tokenType, count) in tokens)
            {
                summary.AppendLine($"- {tokenType.GetEmoji()} **{tokenType.GetDisplayName()}**: `{count}`");
            }
        }
    }

    /// <summary>
    ///     Gets the user's tradeable Pokemon for quick selection.
    /// </summary>
    /// <param name="userId">The user ID to get Pokemon for.</param>
    /// <returns>A list of tradeable Pokemon with their details.</returns>
    public async Task<List<TradeablePokemonInfo>> GetUserTradeablePokemonAsync(ulong userId)
    {
        try
        {
            await using var db = await _context.GetConnectionAsync();

            var userPokemon = await db.UserPokemonOwnerships
                .Where(o => o.UserId == userId)
                .Join(db.UserPokemon, o => o.PokemonId, p => p.Id, (o, p) => new { o.Position, Pokemon = p })
                .Where(x => x.Position > 1 && // Cannot trade position 1
                           x.Pokemon.PokemonName != "Egg" && // Cannot trade eggs
                           !x.Pokemon.Favorite && // Cannot trade favorited
                           x.Pokemon.Tradable && // Must be tradable
                           !x.Pokemon.MarketEnlist) // Cannot trade if on market
                .OrderBy(x => x.Position)
                .Take(20) // Limit to top 20 for select menu
                .Select(x => new TradeablePokemonInfo
                {
                    Position = x.Position,
                    PokemonName = x.Pokemon.PokemonName!,
                    Nickname = x.Pokemon.Nickname,
                    Level = x.Pokemon.Level,
                    Shiny = x.Pokemon.Shiny,
                    Radiant = x.Pokemon.Radiant
                })
                .ToListAsync();

            return userPokemon;
        }
        catch (Exception ex)
        {
            Log.Information($"Error getting user tradeable Pokemon: {ex.Message}");
            return new List<TradeablePokemonInfo>();
        }
    }

    #endregion
}