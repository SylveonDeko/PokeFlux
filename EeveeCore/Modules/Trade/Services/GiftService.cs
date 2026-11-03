using System.Text.Json;
using EeveeCore.Modules.Trade.Models;
using TokenType = EeveeCore.Modules.Trade.Models.TokenType;
using LinqToDB;
using LinqToDB.Async;
using TradeLog = EeveeCore.Database.Linq.Models.Game.TradeLog;

namespace EeveeCore.Modules.Trade.Services;

/// <summary>
///     Service for handling gift operations between users.
///     Supports gifting credits, redeems, Pokemon, and tokens with validation and logging.
/// </summary>
public class GiftService : INService
{
    private readonly LinqToDbConnectionProvider _dbProvider;
    private readonly BotLog _botLog;
    private readonly ITradeLockService _tradeLockService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GiftService" /> class.
    /// </summary>
    /// <param name="dbProvider">The LinqToDB connection provider.</param>
    /// <param name="botLog">The bot event log.</param>
    /// <param name="tradeLockService">The trade lock service.</param>
    public GiftService(LinqToDbConnectionProvider dbProvider, BotLog botLog, ITradeLockService tradeLockService)
    {
        _dbProvider = dbProvider;
        _botLog = botLog;
        _tradeLockService = tradeLockService;
    }

    /// <summary>
    ///     Gifts credits from one user to another.
    /// </summary>
    /// <param name="giverId">The user giving the credits.</param>
    /// <param name="receiverId">The user receiving the credits.</param>
    /// <param name="amount">The amount of credits to gift.</param>
    /// <returns>A GiftResult indicating success or failure.</returns>
    public async Task<GiftResult> GiftCreditsAsync(ulong giverId, ulong receiverId, ulong amount)
    {
        if (giverId == receiverId)
        {
            return GiftResult.FailedGift("You cannot give yourself credits.");
        }

        if (amount <= 0)
        {
            return GiftResult.FailedGift("You need to give at least 1 credit!");
        }

        // Validate participants
        var validationResult = await ValidateGiftParticipantsAsync(giverId, receiverId);
        if (!validationResult.Success)
        {
            return GiftResult.FailedGift(validationResult.Message);
        }

        await using var db = await _dbProvider.GetConnectionAsync();
        
        var giver = await db.Users.FirstOrDefaultAsync(u => u.UserId == giverId);
        var receiver = await db.Users.FirstOrDefaultAsync(u => u.UserId == receiverId);

        if (giver == null)
        {
            return GiftResult.FailedGift("You have not started! Start with `/start` first!");
        }

        if (receiver == null)
        {
            return GiftResult.FailedGift("The recipient has not started! They need to start with `/start` first!");
        }

        var giverCredits = giver.MewCoins ?? 0;
        if (giverCredits < amount)
        {
            return GiftResult.FailedGift("You don't have that many credits!");
        }

        // Transfer credits
        await db.Users.Where(u => u.UserId == giverId)
            .Set(u => u.MewCoins, giverCredits - (ulong)amount)
            .UpdateAsync();
            
        await db.Users.Where(u => u.UserId == receiverId)
            .Set(u => u.MewCoins, (receiver.MewCoins ?? 0) + (ulong)amount)
            .UpdateAsync();

        // Log the transaction
        await LogGiftTransactionAsync(new TradeLog
        {
            Command = "gift_credits", SenderId = giverId, ReceiverId = receiverId, SenderCredits = (long)amount
        });

        // Send log message
        await SendLogMessageAsync($"💰 **Credits Gift**\n" +
                                  $"**From**: <@{giverId}> (`{giverId}`)\n" +
                                  $"**To**: <@{receiverId}> (`{receiverId}`)\n" +
                                  $"**Amount**: `{amount:N0}` credits");

        return GiftResult.SuccessfulGift(
            $"You have given <@{receiverId}> {amount:N0} credits.",
            giverId, receiverId, "credits", amount.ToString());
    }

    /// <summary>
    ///     Gifts redeems from one user to another.
    /// </summary>
    /// <param name="giverId">The user giving the redeems.</param>
    /// <param name="receiverId">The user receiving the redeems.</param>
    /// <param name="amount">The amount of redeems to gift.</param>
    /// <returns>A GiftResult indicating success or failure.</returns>
    public async Task<GiftResult> GiftRedeemsAsync(ulong giverId, ulong receiverId, int amount)
    {
        if (giverId == receiverId)
        {
            return GiftResult.FailedGift("You cannot give yourself redeems.");
        }

        if (amount <= 0)
        {
            return GiftResult.FailedGift("You need to give at least 1 redeem!");
        }

        // Validate participants
        var validationResult = await ValidateGiftParticipantsAsync(giverId, receiverId);
        if (!validationResult.Success)
        {
            return GiftResult.FailedGift(validationResult.Message);
        }

        await using var db = await _dbProvider.GetConnectionAsync();
        
        var giver = await db.Users.FirstOrDefaultAsync(u => u.UserId == giverId);
        var receiver = await db.Users.FirstOrDefaultAsync(u => u.UserId == receiverId);

        if (giver == null)
        {
            return GiftResult.FailedGift("You have not started! Start with `/start` first!");
        }

        if (receiver == null)
        {
            return GiftResult.FailedGift("The recipient has not started! They need to start with `/start` first!");
        }

        var giverRedeems = giver.Redeems ?? 0;
        if (giverRedeems < (ulong)amount)
        {
            return GiftResult.FailedGift("You don't have that many redeems!");
        }

        // Transfer redeems
        await db.Users.Where(u => u.UserId == giverId)
            .Set(u => u.Redeems, giverRedeems - (ulong)amount)
            .UpdateAsync();
            
        await db.Users.Where(u => u.UserId == receiverId)
            .Set(u => u.Redeems, (receiver.Redeems ?? 0) + (ulong)amount)
            .UpdateAsync();

        // Log the transaction
        await LogGiftTransactionAsync(new TradeLog
        {
            Command = "gift_redeems", SenderId = giverId, ReceiverId = receiverId, SenderRedeems = (ulong)amount
        });

        // Send log message
        await SendLogMessageAsync($"🎫 **Redeems Gift**\n" +
                                  $"**From**: <@{giverId}> (`{giverId}`)\n" +
                                  $"**To**: <@{receiverId}> (`{receiverId}`)\n" +
                                  $"**Amount**: `{amount}` redeems");

        return GiftResult.SuccessfulGift(
            $"You have given <@{receiverId}> {amount} redeems.",
            giverId, receiverId, "redeems", amount.ToString());
    }

    /// <summary>
    ///     Gifts a Pokemon from one user to another.
    /// </summary>
    /// <param name="giverId">The user giving the Pokemon.</param>
    /// <param name="receiverId">The user receiving the Pokemon.</param>
    /// <param name="pokemonPosition">The position of the Pokemon in the giver's collection.</param>
    /// <returns>A GiftResult indicating success or failure.</returns>
    public async Task<GiftResult> GiftPokemonAsync(ulong giverId, ulong receiverId, int pokemonPosition)
    {
        if (giverId == receiverId)
        {
            return GiftResult.FailedGift("You cannot give a Pokemon to yourself.");
        }

        if (pokemonPosition <= 1)
        {
            return GiftResult.FailedGift("You cannot give away that Pokemon");
        }

        // Validate participants
        var validationResult = await ValidateGiftParticipantsAsync(giverId, receiverId);
        if (!validationResult.Success)
        {
            return GiftResult.FailedGift(validationResult.Message);
        }

        await using var db = await _dbProvider.GetConnectionAsync();
        
        // Get Pokemon
        var ownership = await db.UserPokemonOwnerships
            .FirstOrDefaultAsync(o => o.UserId == giverId && o.Position == (ulong)pokemonPosition - 1);

        if (ownership == null)
        {
            return GiftResult.FailedGift("Invalid Pokemon Number");
        }

        var pokemon = await db.UserPokemon
            .FirstOrDefaultAsync(p => p.Id == ownership.PokemonId);

        if (pokemon == null)
        {
            return GiftResult.FailedGift("Pokemon not found.");
        }

        // Validate Pokemon
        if (pokemon.PokemonName == "Egg")
        {
            return GiftResult.FailedGift("You cannot give Eggs!");
        }

        if (pokemon.Favorite)
        {
            return GiftResult.FailedGift("You can't give away a favorited Pokemon. Unfavorite it first!");
        }

        if (!pokemon.Tradable)
        {
            return GiftResult.FailedGift("That Pokemon is not tradable.");
        }

        if (pokemon.MarketEnlist)
        {
            return GiftResult.FailedGift("That Pokemon is currently listed on the market!");
        }

        // Find next position for receiver
        var maxPosition = await db.UserPokemonOwnerships
            .Where(o => o.UserId == receiverId)
            .MaxAsync(o => (ulong?)o.Position) ?? 0;

        var newPosition = maxPosition + 1;

        // Transfer ownership
        await db.UserPokemonOwnerships.Where(o => o.UserId == giverId && o.PokemonId == ownership.PokemonId)
            .Set(o => o.UserId, receiverId)
            .Set(o => o.Position, newPosition)
            .UpdateAsync();

        // Reset Pokemon state
        await db.UserPokemon.Where(p => p.Id == pokemon.Id)
            .Set(p => p.MarketEnlist, false)
            .UpdateAsync();

        // Log the transaction
        await LogGiftTransactionAsync(new TradeLog
        {
            Command = "gift_pokemon", SenderId = giverId, ReceiverId = receiverId, SenderPokemon = [pokemon.Id]
        });

        // Send log message
        await SendLogMessageAsync($"🎁 **Pokemon Gift**\n" +
                                  $"**From**: <@{giverId}> (`{giverId}`)\n" +
                                  $"**To**: <@{receiverId}> (`{receiverId}`)\n" +
                                  $"**Pokemon**: `{pokemon.Id}` {pokemon.PokemonName}");

        return GiftResult.SuccessfulGift(
            $"You have given <@{receiverId}> a {pokemon.PokemonName}",
            giverId, receiverId, "pokemon", pokemon.PokemonName);
    }

    /// <summary>
    ///     Gifts tokens from one user to another.
    /// </summary>
    /// <param name="giverId">The user giving the tokens.</param>
    /// <param name="receiverId">The user receiving the tokens.</param>
    /// <param name="tokenType">The type of tokens to gift.</param>
    /// <param name="amount">The number of tokens to gift.</param>
    /// <returns>A GiftResult indicating success or failure.</returns>
    public async Task<GiftResult> GiftTokensAsync(ulong giverId, ulong receiverId, TokenType tokenType, int amount)
    {
        if (giverId == receiverId)
        {
            return GiftResult.FailedGift("You cannot give yourself tokens.");
        }

        if (amount <= 0)
        {
            return GiftResult.FailedGift("You need to gift at least 1 token!");
        }

        // Validate participants
        var validationResult = await ValidateGiftParticipantsAsync(giverId, receiverId);
        if (!validationResult.Success)
        {
            return GiftResult.FailedGift(validationResult.Message);
        }

        await using var db = await _dbProvider.GetConnectionAsync();
        
        var giver = await db.Users.FirstOrDefaultAsync(u => u.UserId == giverId);
        var receiver = await db.Users.FirstOrDefaultAsync(u => u.UserId == receiverId);

        if (giver == null)
        {
            return GiftResult.FailedGift("You have not started! Start with `/start` first!");
        }

        if (receiver == null)
        {
            return GiftResult.FailedGift("The recipient has not started! They need to start with `/start` first!");
        }

        // Get token data
        var giverTokens = GetUserTokensFromJson(giver.Tokens);
        var receiverTokens = GetUserTokensFromJson(receiver.Tokens);

        var tokenTypeName = tokenType.GetDisplayName();
        if (!giverTokens.TryGetValue(tokenTypeName, out var giverAmount) || giverAmount < amount)
        {
            return GiftResult.FailedGift($"You don't have enough {tokenTypeName} tokens!");
        }

        // Transfer tokens
        giverTokens[tokenTypeName] = giverAmount - amount;
        receiverTokens[tokenTypeName] = receiverTokens.GetValueOrDefault(tokenTypeName, 0) + amount;

        // Update database
        await db.Users.Where(u => u.UserId == giverId)
            .Set(u => u.Tokens, JsonSerializer.Serialize(giverTokens))
            .UpdateAsync();
            
        await db.Users.Where(u => u.UserId == receiverId)
            .Set(u => u.Tokens, JsonSerializer.Serialize(receiverTokens))
            .UpdateAsync();

        // Log the transaction
        await LogGiftTransactionAsync(new TradeLog
        {
            Command = "gift_tokens", SenderId = giverId, ReceiverId = receiverId,
            SenderTokens = JsonSerializer.Serialize(new Dictionary<string, int> { [tokenTypeName] = amount })
        });

        // Send log message
        await SendLogMessageAsync($"{tokenType.GetEmoji()} **{tokenTypeName} Tokens Gift**\n" +
                                  $"**From**: <@{giverId}> (`{giverId}`)\n" +
                                  $"**To**: <@{receiverId}> (`{receiverId}`)\n" +
                                  $"**Amount**: `{amount}` {tokenTypeName} tokens");

        return GiftResult.SuccessfulGift(
            $"You have given <@{receiverId}> {amount} {tokenTypeName} tokens.",
            giverId, receiverId, "tokens", $"{amount} {tokenTypeName}");
    }

    #region Private Helper Methods

    private async Task<TradeResult> ValidateGiftParticipantsAsync(ulong giverId, ulong receiverId)
    {
        await using var db = await _dbProvider.GetConnectionAsync();
        
        // Check if users are trade locked
        var users = await db.Users
            .Where(u => u.UserId == giverId || u.UserId == receiverId)
            .ToListAsync();

        var tradeLocked = users.Where(u => u.TradeLock == true).ToList();
        if (tradeLocked.Any())
        {
            return TradeResult.Failure("A user is not allowed to trade");
        }

        return TradeResult.FromSuccess("Users validated for gifting.");
    }

    private static Dictionary<string, int> GetUserTokensFromJson(string? tokensJson)
    {
        if (string.IsNullOrEmpty(tokensJson))
        {
            return CreateDefaultTokensDictionary();
        }

        try
        {
            var tokens = JsonSerializer.Deserialize<Dictionary<string, int>>(tokensJson);
            return tokens ?? CreateDefaultTokensDictionary();
        }
        catch
        {
            return CreateDefaultTokensDictionary();
        }
    }

    private static Dictionary<string, int> CreateDefaultTokensDictionary()
    {
        return new Dictionary<string, int>
        {
            { "Bug", 0 }, { "Ice", 0 }, { "Dark", 0 }, { "Fire", 0 }, { "Rock", 0 },
            { "Fairy", 0 }, { "Ghost", 0 }, { "Grass", 0 }, { "Steel", 0 }, { "Water", 0 },
            { "Dragon", 0 }, { "Flying", 0 }, { "Ground", 0 }, { "Normal", 0 }, { "Poison", 0 },
            { "Psychic", 0 }, { "Electric", 0 }, { "Fighting", 0 }
        };
    }

    private async Task LogGiftTransactionAsync(TradeLog tradeLog)
    {
        try
        {
            await using var db = await _dbProvider.GetConnectionAsync();
            tradeLog.Time = DateTime.UtcNow;
            await db.InsertAsync(tradeLog);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to write {Command} log", tradeLog.Command);
        }
    }

    private Task SendLogMessageAsync(string message) => _botLog.SendAsync(message);

    #endregion
}