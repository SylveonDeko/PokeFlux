using EeveeCore.Modules.Spawn.Constants;
using LinqToDB;
using LinqToDB.Async;
using UserPokemonOwnership = EeveeCore.Database.Linq.Models.Pokemon.UserPokemonOwnership;

namespace EeveeCore.Modules.Market.Services;

/// <summary>
///     Represents the result of a market operation.
/// </summary>
public class MarketResult
{
    /// <summary>
    ///     Gets or sets a value indicating whether the operation was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    ///     Gets or sets the message describing the result.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets additional data from the operation.
    /// </summary>
    public object? Data { get; set; }

    /// <summary>
    ///     Creates a successful result.
    /// </summary>
    /// <param name="message">The success message.</param>
    /// <param name="data">Optional data to include.</param>
    /// <returns>A successful MarketResult.</returns>
    public static MarketResult FromSuccess(string message, object? data = null)
    {
        return new MarketResult { Success = true, Message = message, Data = data };
    }

    /// <summary>
    ///     Creates a failed result.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>A failed MarketResult.</returns>
    public static MarketResult FromError(string message)
    {
        return new MarketResult { Success = false, Message = message };
    }
}

/// <summary>
///     Service for managing the Pokemon market system.
/// </summary>
public class MarketService : INService
{
    private readonly LinqToDbConnectionProvider _dbProvider;
    private readonly IDataCache _cache;
    private readonly FluxerClient _client;
    private readonly BotLog _botLog;
    private const string MarketLockKey = "marketlock";

    /// <summary>
    ///     How many active listings a user may have at once.
    /// </summary>
    public const int BaseMarketLimit = 30;

    /// <summary>
    ///     The lowest listing price; lower prices are raised to it.
    /// </summary>
    public const int MinListingPrice = 0;

    /// <summary>
    ///     The highest listing price.
    /// </summary>
    public const int MaxListingPrice = int.MaxValue;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MarketService" /> class.
    /// </summary>
    /// <param name="dbProvider">The LinqToDB connection provider.</param>
    /// <param name="cache">The Redis cache service.</param>
    /// <param name="client">The Fluxer client, for direct messages.</param>
    /// <param name="botLog">The bot event log.</param>
    public MarketService(LinqToDbConnectionProvider dbProvider, IDataCache cache, FluxerClient client, BotLog botLog)
    {
        _dbProvider = dbProvider;
        _cache = cache;
        _client = client;
        _botLog = botLog;
    }

    /// <inheritdoc />
    public async Task<MarketResult> AddPokemonToMarketAsync(ulong userId, ulong pokemonPosition, int price)
    {
        try
        {
            price = Math.Max(price, MinListingPrice);
            if (price > MaxListingPrice)
                return MarketResult.FromError("Price too high.");

            if (pokemonPosition <= 1)
                return MarketResult.FromError("You cannot list your first Pokemon on the market.");

            await using var db = await _dbProvider.GetConnectionAsync();

            var user = await db.Users
                .FirstOrDefaultAsync(u => u.UserId == userId);

            if (user == null)
                return MarketResult.FromError("You have not started!\nStart with `/start`");

            if (user.TradeLock == true)
                return MarketResult.FromError("You are not allowed to trade.");

            // Get Pokemon from ownership table
            var ownership = await db.UserPokemonOwnerships
                .FirstOrDefaultAsync(o => o.UserId == userId && o.Position == pokemonPosition - 1);

            if (ownership == null)
                return MarketResult.FromError("You don't have that Pokemon.");

            var pokemon = await db.UserPokemon
                .FirstOrDefaultAsync(p => p.Id == ownership.PokemonId);

            if (pokemon == null)
                return MarketResult.FromError("You don't have that Pokemon.");

            if (pokemon.PokemonName == "Egg")
                return MarketResult.FromError("You can't market an Egg!");

            if (!pokemon.Tradable)
                return MarketResult.FromError("That pokemon cannot be listed on the market!");

            if (pokemon.Favorite)
                return MarketResult.FromError($"You cannot market your {pokemon.PokemonName} as it is favorited.\n" +
                                              $"Unfavorite it first with `/fav remove {pokemonPosition}`.");

            var currentListings = await GetActiveListingCountAsync(userId);

            if (currentListings >= BaseMarketLimit)
                return MarketResult.FromError(
                    $"You are only allowed to list {BaseMarketLimit} pokemon on the market at once.");

            // Create market listing
            var marketListing = new Database.Linq.Models.Game.Market
            {
                PokemonId = pokemon.Id,
                OwnerId = userId,
                Price = price,
                BuyerId = null,
                ListedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ViewCount = 0
            };

            marketListing.Id = (ulong)await db.InsertWithInt64IdentityAsync(marketListing);

            // Remove Pokemon from user's ownership
            await db.DeleteAsync(ownership);

            await LogMarketTransactionAsync(
                $"**ID** - `{marketListing.Id}`\n➕ :<@{userId}>(`{userId}`) **ADDED** **{pokemon.PokemonName}**.\n-----------------------------");

            return MarketResult.FromSuccess(
                $"You have added your {pokemon.PokemonName} to the market! It is market listing #{marketListing.Id}.",
                marketListing.Id);

        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MarketResult> RemovePokemonFromMarketAsync(ulong userId, ulong listingId)
    {
        if (await IsListingLockedAsync(listingId))
            return MarketResult.FromError("Someone is already in the process of buying that pokemon. You can try again later.");

        await using var db = await _dbProvider.GetConnectionAsync();
        
        var listing = await db.Market
            .FirstOrDefaultAsync(m => m.Id == listingId);

        if (listing == null)
            return MarketResult.FromError("That listing does not exist.");

        if (listing.OwnerId != userId)
            return MarketResult.FromError("You do not own that listing.");

        if (listing.BuyerId != null)
            return MarketResult.FromError("That listing has already ended.");

        var pokemon = await db.UserPokemon
            .FirstOrDefaultAsync(p => p.Id == listing.PokemonId);

        if (pokemon == null)
            return MarketResult.FromError("Pokemon not found.");

        // Find the next available position for this user
        var maxPosition = await db.UserPokemonOwnerships
            .Where(o => o.UserId == userId)
            .MaxAsync(o => o.Position);

        var newPosition = maxPosition + 1;

        // Add Pokemon back to user's ownership
        var ownership = new UserPokemonOwnership
        {
            UserId = userId,
            PokemonId = listing.PokemonId,
            Position = newPosition
        };

        await db.InsertAsync(ownership);

        // Mark listing as removed (buyer = 0)
        await db.Market.Where(m => m.Id == listingId)
            .Set(m => m.BuyerId, 0UL)
            .UpdateAsync();

        await LogMarketTransactionAsync(
            $"**ID** - `{listingId}`\n➖ :<@{userId}>(`{userId}`) **REMOVED** {pokemon.PokemonName} from the market.\n-----------------------------");

        return MarketResult.FromSuccess($"You have removed your {pokemon.PokemonName} from the market");
    }

    /// <inheritdoc />
    public async Task<MarketResult> BuyPokemonFromMarketAsync(ulong buyerId, ulong listingId)
    {
        await using var db = await _dbProvider.GetConnectionAsync();
        
        var listing = await db.Market
            .FirstOrDefaultAsync(m => m.Id == listingId);

        if (listing == null)
            return MarketResult.FromError("That listing does not exist.");

        if (listing.OwnerId == buyerId)
            return MarketResult.FromError("You cannot buy your own pokemon.");

        if (listing.BuyerId != null)
            return MarketResult.FromError("That listing has already ended.");

        var pokemon = await db.UserPokemon
            .FirstOrDefaultAsync(p => p.Id == listing.PokemonId);

        if (pokemon == null)
            return MarketResult.FromError("That pokemon does not exist?");

        var buyer = await db.Users
            .FirstOrDefaultAsync(u => u.UserId == buyerId);

        if (buyer == null)
            return MarketResult.FromError("You have not started!\nStart with `/start` first.");

        if (buyer.TradeLock == true)
            return MarketResult.FromError("You are not allowed to trade.");

        if (listing.Price > (long)(buyer.MewCoins ?? 0))
            return MarketResult.FromError("You don't have enough credits to buy that pokemon.");

        // Find the next available position for the buyer
        var maxPosition = await db.UserPokemonOwnerships
            .Where(o => o.UserId == buyerId)
            .MaxAsync(o => o.Position);

        var newPosition = maxPosition + 1;

        // Transfer ownership
        var ownership = new UserPokemonOwnership
        {
            UserId = buyerId,
            PokemonId = listing.PokemonId,
            Position = newPosition
        };

        await db.InsertAsync(ownership);

        // Update buyer's credits
        await db.Users.Where(u => u.UserId == buyerId)
            .Set(u => u.MewCoins, u => (u.MewCoins ?? 0) - (ulong)listing.Price)
            .UpdateAsync();

        // Update seller's credits
        await db.Users.Where(u => u.UserId == listing.OwnerId)
            .Set(u => u.MewCoins, u => (u.MewCoins ?? 0) + (ulong)listing.Price)
            .UpdateAsync();

        // Mark listing as sold
        await db.Market.Where(m => m.Id == listingId)
            .Set(m => m.BuyerId, buyerId)
            .UpdateAsync();

        // Update achievements
        await db.Achievements.Where(a => a.UserId == buyerId)
            .Set(a => a.MarketPurchased, a => a.MarketPurchased + 1)
            .UpdateAsync();

        await db.Achievements.Where(a => a.UserId == listing.OwnerId)
            .Set(a => a.MarketSold, a => a.MarketSold + 1)
            .UpdateAsync();

        await LogMarketTransactionAsync(
            $"**ID** - `{listingId}`\n➕ : <@{buyerId}>(`{buyerId}`) **BOUGHT** a **{pokemon.PokemonName}**. Seller - (<@{listing.OwnerId}>)`{listing.OwnerId}`.\n-----------------------------");

        // Send DMs
        try
        {
            await _client.SendDirectMessageAsync(buyerId, $"You have Successfully Bought A {pokemon.PokemonName} for {listing.Price} credits.");
        }
        catch { }

        try
        {
            await _client.SendDirectMessageAsync(listing.OwnerId, $"Your {pokemon.PokemonName} has been sold for {listing.Price} credits.");
        }
        catch { }

        return MarketResult.FromSuccess($"You have Successfully Bought A {pokemon.PokemonName} for {listing.Price} credits.");
    }

    /// <inheritdoc />
    public async Task<Database.Linq.Models.Pokemon.Pokemon?> GetPokemonAsync(ulong listingId)
    {
        await using var db = await _dbProvider.GetConnectionAsync();
        
        // Join market listing with pokemon data to get full info including price
        var result = await (from market in db.Market
                           join pokemon in db.UserPokemon on market.PokemonId equals pokemon.Id
                           where market.Id == listingId && market.BuyerId == null
                           select new { Market = market, Pokemon = pokemon })
            .FirstOrDefaultAsync();

        if (result == null)
            return null;

        // Set the price from the market listing
        result.Pokemon.Price = result.Market.Price;
        return result.Pokemon;
    }

    /// <inheritdoc />
    public async Task<bool> IsListingLockedAsync(ulong listingId)
    {
        var database = _cache.Redis.GetDatabase();
        var lockedListings = await database.ListRangeAsync(MarketLockKey);
        
        return lockedListings.Any(value => 
            value.HasValue && 
            ulong.TryParse((string?)value, out var id) &&
            id == listingId);
    }

    /// <inheritdoc />
    public async Task AddMarketLockAsync(ulong listingId)
    {
        var database = _cache.Redis.GetDatabase();
        await database.ListLeftPushAsync(MarketLockKey, listingId.ToString());
    }

    /// <inheritdoc />
    public async Task RemoveMarketLockAsync(ulong listingId)
    {
        var database = _cache.Redis.GetDatabase();
        await database.ListRemoveAsync(MarketLockKey, listingId.ToString());
    }

    /// <inheritdoc />
    public async Task<bool> ExecuteWithMarketLockAsync(ulong listingId, Func<Task> action)
    {
        if (await IsListingLockedAsync(listingId))
            return false;

        try
        {
            await AddMarketLockAsync(listingId);
            await action();
            return true;
        }
        finally
        {
            await RemoveMarketLockAsync(listingId);
        }
    }

    /// <summary>
    ///     Counts a user's listings that have no buyer yet.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    public async Task<int> GetActiveListingCountAsync(ulong userId)
    {
        await using var db = await _dbProvider.GetConnectionAsync();
        return await db.Market.CountAsync(m => m.OwnerId == userId && m.BuyerId == null);
    }

    /// <summary>
    ///     Gets why a user is banned from the market.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <returns>The ban reason, or null when the user is not banned.</returns>
    public async Task<string?> GetMarketBanReasonAsync(ulong userId)
    {
        await using var db = await _dbProvider.GetConnectionAsync();
        var user = await db.Users
            .Where(u => u.UserId == userId)
            .Select(u => new { u.MarketBanned, u.MarketBanReason })
            .FirstOrDefaultAsync();

        return user?.MarketBanned == true ? user.MarketBanReason ?? "Fraud detection" : null;
    }

    /// <summary>
    ///     Logs a market listing, removal or sale to the bot log.
    /// </summary>
    /// <param name="message">The log message.</param>
    public Task LogMarketTransactionAsync(string message) => _botLog.SendAsync(message);

    /// <summary>
    ///     Gets all current market listings with sorting and filtering.
    /// </summary>
    /// <param name="sortBy">How to sort the listings.</param>
    /// <param name="filter">Filter to apply.</param>
    /// <param name="search">Search term for Pokemon names.</param>
    /// <returns>A market listings result containing the listings and filter information.</returns>
    public async Task<MarketListingsResult> GetMarketListingsAsync(string sortBy, string filter, string? search)
    {
        var db = await _dbProvider.GetConnectionAsync();
        var query = from market in db.Market
            join pokemon in db.UserPokemon on market.PokemonId equals pokemon.Id
            where market.BuyerId == null
            select new MarketListingEntry
            {
                ListingId = market.Id,
                PokemonId = pokemon.Id,
                ListedAt = market.ListedAt,
                PokemonName = pokemon.PokemonName,
                Level = pokemon.Level,
                Price = market.Price,
                HpIv = pokemon.HpIv,
                AttackIv = pokemon.AttackIv,
                DefenseIv = pokemon.DefenseIv,
                SpecialAttackIv = pokemon.SpecialAttackIv,
                SpecialDefenseIv = pokemon.SpecialDefenseIv,
                SpeedIv = pokemon.SpeedIv,
                Shiny = pokemon.Shiny,
                Radiant = pokemon.Radiant,
                Skin = pokemon.Skin,
                Gender = pokemon.Gender,
                Nature = pokemon.Nature,
                OwnerId = market.OwnerId
            };

        // Determine if any filtering criteria were applied
        var hasFilter = filter != "all";
        var hasSearch = !string.IsNullOrEmpty(search);
        var hasFilters = hasFilter || hasSearch;

        // Apply filter
        query = filter switch
        {
            "shiny" => query.Where(p => p.Shiny == true),
            "radiant" => query.Where(p => p.Radiant == true),
            "shadow" => query.Where(p => p.Skin == "shadow"),
            "legendary" => query.Where(p => PokemonList.LegendList.Contains(p.PokemonName)),
            _ => query
        };

        // Apply search
        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(p => p.PokemonName.Contains(search));
        }

        // Apply sorting
        query = sortBy switch
        {
            "price_asc" => query.OrderBy(p => p.Price),
            "price_desc" => query.OrderByDescending(p => p.Price),
            "level_desc" => query.OrderByDescending(p => p.Level),
            "level_asc" => query.OrderBy(p => p.Level),
            "iv_desc" => query.OrderByDescending(p => p.HpIv + p.AttackIv + p.DefenseIv + p.SpecialAttackIv + p.SpecialDefenseIv + p.SpeedIv),
            "name" => query.OrderBy(p => p.PokemonName),
            _ => query.OrderByDescending(p => p.ListingId) // recent
        };

        var listings = await query.ToListAsync();

        return new MarketListingsResult
        {
            Listings = listings,
            HasFilters = hasFilters
        };
    }
}

/// <summary>
///     Represents the result of a market listings query.
/// </summary>
public class MarketListingsResult
{
    /// <summary>
    ///     Gets or sets the list of market listings.
    /// </summary>
    public List<MarketListingEntry> Listings { get; set; } = new();
    
    /// <summary>
    ///     Gets or sets a value indicating whether any filtering criteria were applied.
    /// </summary>
    public bool HasFilters { get; set; }
}

/// <summary>
///     Represents a market listing entry for display.
/// </summary>
public class MarketListingEntry
{
    /// <summary>
    ///     Gets or sets the unique identifier for the market listing.
    /// </summary>
    public ulong ListingId { get; set; }

    /// <summary>
    ///     Gets or sets the ID of the listed Pokemon.
    /// </summary>
    public ulong PokemonId { get; set; }

    /// <summary>
    ///     Gets or sets when the Pokemon was listed.
    /// </summary>
    public DateTime ListedAt { get; set; }

    /// <summary>
    ///     Gets or sets the name of the Pokemon.
    /// </summary>
    public string PokemonName { get; set; } = string.Empty;
    
    /// <summary>
    ///     Gets or sets the level of the Pokemon.
    /// </summary>
    public int Level { get; set; }
    
    /// <summary>
    ///     Gets or sets the price of the Pokemon in coins.
    /// </summary>
    public int Price { get; set; }
    
    /// <summary>
    ///     Gets or sets the HP IV value.
    /// </summary>
    public int HpIv { get; set; }
    
    /// <summary>
    ///     Gets or sets the Attack IV value.
    /// </summary>
    public int AttackIv { get; set; }
    
    /// <summary>
    ///     Gets or sets the Defense IV value.
    /// </summary>
    public int DefenseIv { get; set; }
    
    /// <summary>
    ///     Gets or sets the Special Attack IV value.
    /// </summary>
    public int SpecialAttackIv { get; set; }
    
    /// <summary>
    ///     Gets or sets the Special Defense IV value.
    /// </summary>
    public int SpecialDefenseIv { get; set; }
    
    /// <summary>
    ///     Gets or sets the Speed IV value.
    /// </summary>
    public int SpeedIv { get; set; }
    
    /// <summary>
    ///     Gets or sets a value indicating whether the Pokemon is shiny.
    /// </summary>
    public bool? Shiny { get; set; }
    
    /// <summary>
    ///     Gets or sets a value indicating whether the Pokemon is radiant.
    /// </summary>
    public bool? Radiant { get; set; }
    
    /// <summary>
    ///     Gets or sets the skin variant of the Pokemon (e.g., "shadow").
    /// </summary>
    public string? Skin { get; set; }
    
    /// <summary>
    ///     Gets or sets the gender of the Pokemon.
    /// </summary>
    public string Gender { get; set; } = string.Empty;
    
    /// <summary>
    ///     Gets or sets the nature of the Pokemon.
    /// </summary>
    public string Nature { get; set; } = string.Empty;
    
    /// <summary>
    ///     Gets or sets the user ID of the Pokemon's owner.
    /// </summary>
    public ulong OwnerId { get; set; }
}