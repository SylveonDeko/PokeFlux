using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Market.Services;
using EeveeCore.Modules.Pokemon.Services;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;
using MarketRow = EeveeCore.Database.Linq.Models.Game.Market;
using PokemonModel = EeveeCore.Database.Linq.Models.Pokemon.Pokemon;

namespace EeveeCore.Controllers;

/// <summary>
///     The Pokémon market, with the same bans, trade locks and listing rules as the market commands.
/// </summary>
/// <param name="marketService">The market service.</param>
/// <param name="pokemonService">Turns Pokémon IDs into numbers.</param>
/// <param name="tradeLockService">Keeps players in a trade out of the market.</param>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="cards">Builds Pokémon cards.</param>
[Route("api/market")]
public class MarketController(
    MarketService marketService,
    PokemonService pokemonService,
    ITradeLockService tradeLockService,
    LinqToDbConnectionProvider dbProvider,
    PokemonCardFactory cards) : PokeFluxUserControllerBase
{
    private const string InTrade = "You are currently in a trade!";
    private const string ListingLocked =
        "Someone is already in the process of buying that pokemon. You can try again later.";

    private static readonly string[] Sorts =
        ["price_asc", "price_desc", "level_desc", "level_asc", "iv_desc", "recent", "name"];

    private static readonly string[] Filters = ["all", "shiny", "radiant", "shadow", "legendary"];

    /// <summary>
    ///     Browses the market a page at a time.
    /// </summary>
    /// <param name="page">The page, from 1.</param>
    /// <param name="pageSize">How many listings a page holds, up to 100.</param>
    /// <param name="sort">price_asc, price_desc, level_desc, level_asc, iv_desc, recent or name.</param>
    /// <param name="filter">all, shiny, radiant, shadow or legendary.</param>
    /// <param name="search">Text to find in Pokémon names.</param>
    [HttpGet]
    public async Task<IActionResult> List(int page = 1, int pageSize = 24, string sort = "recent",
        string filter = "all", string? search = null)
    {
        sort = sort.ToLowerInvariant();
        filter = filter.ToLowerInvariant();
        if (!Sorts.Contains(sort))
            return Error(400, $"Unknown sort. Use one of: {string.Join(", ", Sorts)}.");
        if (!Filters.Contains(filter))
            return Error(400, $"Unknown filter. Use one of: {string.Join(", ", Filters)}.");

        var result = await marketService.GetMarketListingsAsync(sort, filter,
            string.IsNullOrWhiteSpace(search) ? null : search.Trim());

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var entries = result.Listings.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        await using var db = await dbProvider.GetConnectionAsync();
        var listingIds = entries.Select(e => e.ListingId).ToList();
        var rows = await LoadListingsAsync(db, db.Market.Where(m => listingIds.Contains(m.Id)));
        var items = listingIds.Where(rows.ContainsKey).Select(id => rows[id]).ToList();

        return Ok(new MarketPage(await BuildListingsAsync(db, items), result.Listings.Count, page, pageSize));
    }

    /// <summary>
    ///     Gets the user's active listings.
    /// </summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var rows = await LoadListingsAsync(db, db.Market.Where(m => m.OwnerId == UserId && m.BuyerId == null));
        return Ok(await BuildListingsAsync(db, rows.Values.OrderByDescending(r => r.Market.Id).ToList()));
    }

    /// <summary>
    ///     Gets an active listing.
    /// </summary>
    /// <param name="listingId">The listing's ID.</param>
    [HttpGet("{listingId}")]
    public async Task<IActionResult> Get(ulong listingId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var rows = await LoadListingsAsync(db, db.Market.Where(m => m.Id == listingId && m.BuyerId == null));
        if (!rows.TryGetValue(listingId, out var row))
            return Error(404, "That listing does not exist or has already ended.");

        var listing = (await BuildListingsAsync(db, [row]))[0];
        var p = row.Pokemon;
        var info = await pokemonService.GetPokemonInfo(p.PokemonName);
        var baseStats = info?.Stats ?? new PokemonStats();
        var stats = await pokemonService.CalculateStats(p, baseStats);
        return Ok(new MarketListingDetail(listing, p.Nature,
            new StatBlock(p.HpIv, p.AttackIv, p.DefenseIv, p.SpecialAttackIv, p.SpecialDefenseIv, p.SpeedIv),
            await cards.MoveSlotsAsync(p),
            await pokemonService.GetAbilityName(p.PokemonName, p.AbilityIndex),
            new StatBlock(p.HpEv, p.AttackEv, p.DefenseEv, p.SpecialAttackEv, p.SpecialDefenseEv, p.SpeedEv),
            new StatBlock(stats.MaxHp, stats.Attack, stats.Defense, stats.SpecialAttack, stats.SpecialDefense,
                stats.Speed)));
    }

    /// <summary>
    ///     Gets the listing rules the market enforces and how many listings the user has.
    /// </summary>
    [HttpGet("rules")]
    public async Task<IActionResult> Rules() =>
        Ok(new MarketRules(MarketService.MinListingPrice, MarketService.MaxListingPrice,
            MarketService.BaseMarketLimit, await marketService.GetActiveListingCountAsync(UserId)));

    /// <summary>
    ///     Lists one of the user's Pokémon on the market.
    /// </summary>
    /// <param name="request">The Pokémon and price.</param>
    [HttpPost]
    public async Task<IActionResult> Add([FromBody] MarketListRequest request)
    {
        if (await marketService.GetMarketBanReasonAsync(UserId) is { } reason)
            return Error(403, $"You are banned from using the market. Reason: {reason}");

        var number = await pokemonService.GetPokemonIndex(UserId, request.PokemonId);
        if (number == 0)
            return Error(404, "You do not have that Pokemon.");

        MarketResult? result = null;
        if (!await tradeLockService.ExecuteWithTradeLockAsync(UserId,
                async () => result = await marketService.AddPokemonToMarketAsync(UserId, number, request.Price)))
            return Error(409, InTrade);

        return result!.Success
            ? Ok(new MarketListResponse((ulong)result.Data!, result.Message))
            : Error(400, result.Message);
    }

    /// <summary>
    ///     Takes one of the user's listings off the market.
    /// </summary>
    /// <param name="listingId">The listing's ID.</param>
    [HttpDelete("{listingId}")]
    public async Task<IActionResult> Remove(ulong listingId)
    {
        if (await marketService.GetMarketBanReasonAsync(UserId) is { } reason)
            return Error(403, $"You are banned from using the market. Reason: {reason}");

        var result = await marketService.RemovePokemonFromMarketAsync(UserId, listingId);
        if (result.Success)
            return Ok(new ApiMessage(result.Message));

        return Error(result.Message == ListingLocked ? 409 : 400, result.Message);
    }

    /// <summary>
    ///     Buys a listing.
    /// </summary>
    /// <param name="listingId">The listing's ID.</param>
    [HttpPost("{listingId}/buy")]
    public async Task<IActionResult> Buy(ulong listingId)
    {
        if (await marketService.GetMarketBanReasonAsync(UserId) is { } reason)
            return Error(403, $"You are banned from using the market. Reason: {reason}");

        if (await tradeLockService.IsUserTradeLockedAsync(UserId))
            return Error(409, InTrade);

        if (await marketService.IsListingLockedAsync(listingId))
            return Error(409, ListingLocked);

        if (await marketService.GetPokemonAsync(listingId) == null)
            return Error(404, "That listing does not exist or has already ended.");

        MarketResult? result = null;
        var locked = false;
        if (!await tradeLockService.ExecuteWithTradeLockAsync(UserId, async () =>
            {
                locked = !await marketService.ExecuteWithMarketLockAsync(listingId,
                    async () => result = await marketService.BuyPokemonFromMarketAsync(UserId, listingId));
            }))
            return Error(409, InTrade);

        if (locked || result is null)
            return Error(409, ListingLocked);

        return result.Success ? Ok(new ApiMessage(result.Message)) : Error(400, result.Message);
    }

    private static async Task<Dictionary<ulong, (MarketRow Market, PokemonModel Pokemon)>> LoadListingsAsync(
        DittoDataConnection db, IQueryable<MarketRow> listings) =>
        (await (from m in listings
                join p in db.UserPokemon on m.PokemonId equals p.Id
                select new { Market = m, Pokemon = p })
            .ToListAsync())
        .ToDictionary(r => r.Market.Id, r => (r.Market, r.Pokemon));

    private async Task<List<MarketListing>> BuildListingsAsync(DittoDataConnection db,
        List<(MarketRow Market, PokemonModel Pokemon)> rows)
    {
        var sellerIds = rows.Select(r => (ulong?)r.Market.OwnerId).Distinct().ToList();
        var sellers = (await db.Users
                .Where(u => sellerIds.Contains(u.UserId))
                .Select(u => new { u.UserId, u.TrainerNickname })
                .ToListAsync())
            .ToDictionary(u => u.UserId!.Value, u => u.TrainerNickname);

        var listings = new List<MarketListing>();
        foreach (var (market, pokemon) in rows)
        {
            var seller = sellers.GetValueOrDefault(market.OwnerId);
            listings.Add(new MarketListing(market.Id, market.Price, market.OwnerId,
                string.IsNullOrWhiteSpace(seller) ? null : seller, market.ListedAt,
                await cards.BuildAsync(pokemon, CardContext.None, true)));
        }

        return listings;
    }
}
