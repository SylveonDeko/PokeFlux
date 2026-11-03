using EeveeCore.Modules.Extras.Services;

namespace EeveeCore.Controllers.Models;

/// <summary>
///     A market listing.
/// </summary>
/// <param name="ListingId">The listing's ID.</param>
/// <param name="Price">The price in credits.</param>
/// <param name="SellerId">The seller's ID.</param>
/// <param name="SellerName">The seller's trainer nickname, or null.</param>
/// <param name="ListedAt">When the Pokémon was listed.</param>
/// <param name="Pokemon">The listed Pokémon.</param>
public record MarketListing(
    ulong ListingId,
    int Price,
    ulong SellerId,
    string? SellerName,
    DateTime ListedAt,
    PokemonCard Pokemon);

/// <summary>
///     A market listing with the Pokémon's nature, ability, moves, IVs, EVs and stats.
/// </summary>
/// <param name="Listing">The listing.</param>
/// <param name="Nature">The Pokémon's nature.</param>
/// <param name="Ivs">The Pokémon's IVs.</param>
/// <param name="Moves">The four move slots, null where a slot is empty or the move is unknown.</param>
/// <param name="Ability">The ability's name.</param>
/// <param name="Evs">The Pokémon's EVs.</param>
/// <param name="Stats">The calculated stats.</param>
public record MarketListingDetail(
    MarketListing Listing,
    string Nature,
    StatBlock Ivs,
    List<MoveDetails?> Moves,
    string Ability,
    StatBlock Evs,
    StatBlock Stats);

/// <summary>
///     The listing rules the market enforces.
/// </summary>
/// <param name="MinPrice">The lowest price; lower prices are raised to it.</param>
/// <param name="MaxPrice">The highest price.</param>
/// <param name="ListingLimit">How many active listings a user may have.</param>
/// <param name="ListingCount">How many active listings the user has.</param>
public record MarketRules(int MinPrice, int MaxPrice, int ListingLimit, int ListingCount);

/// <summary>
///     A page of market listings.
/// </summary>
/// <param name="Items">The listings on this page.</param>
/// <param name="Total">How many listings match.</param>
/// <param name="Page">The page number, from 1.</param>
/// <param name="PageSize">How many listings a page holds.</param>
public record MarketPage(List<MarketListing> Items, int Total, int Page, int PageSize);

/// <summary>
///     Lists a Pokémon on the market.
/// </summary>
/// <param name="PokemonId">The Pokémon's ID.</param>
/// <param name="Price">The price in credits.</param>
public record MarketListRequest(ulong PokemonId, int Price);

/// <summary>
///     The result of listing a Pokémon.
/// </summary>
/// <param name="ListingId">The new listing's ID.</param>
/// <param name="Message">What happened.</param>
public record MarketListResponse(ulong ListingId, string Message);

/// <summary>
///     An item in the shop.
/// </summary>
/// <param name="Id">The item's ID.</param>
/// <param name="Name">The item's name.</param>
/// <param name="BuyName">The name to buy it by.</param>
/// <param name="Description">What it does.</param>
/// <param name="Price">The price.</param>
/// <param name="Currency">credits, or crystal-slime in the crystal category.</param>
/// <param name="Stock">How many are left, or -1 for unlimited.</param>
/// <param name="Rarity">The rarity.</param>
/// <param name="PokemonType">The Pokémon type it is tied to, or null.</param>
/// <param name="Affordable">Whether the user has enough of its currency.</param>
public record ShopItemInfo(
    string Id,
    string Name,
    string BuyName,
    string Description,
    int Price,
    string Currency,
    int Stock,
    string Rarity,
    string? PokemonType,
    bool Affordable);

/// <summary>
///     A chest the shop sells.
/// </summary>
/// <param name="Type">rare, mythic or legend.</param>
/// <param name="CreditPrice">The price in credits.</param>
/// <param name="RedeemPrice">The price in redeems.</param>
/// <param name="BoughtThisWeek">How many the user bought with credits this week.</param>
/// <param name="WeeklyLimit">How many can be bought with credits each week.</param>
public record ChestInfo(string Type, int CreditPrice, int RedeemPrice, int BoughtThisWeek, int WeeklyLimit);

/// <summary>
///     A shop category.
/// </summary>
/// <param name="Category">general, radiant or crystal.</param>
/// <param name="Credits">The user's credits.</param>
/// <param name="CrystalSlime">The user's crystallized slime.</param>
/// <param name="Items">The items.</param>
/// <param name="Redeems">The user's redeems.</param>
/// <param name="MinimumPokemon">How many Pokémon the shop requires.</param>
/// <param name="PokemonCount">How many Pokémon the user has.</param>
/// <param name="MeetsRequirement">Whether the user has enough Pokémon to use the shop.</param>
/// <param name="Chests">The chests and the user's weekly purchases.</param>
public record ShopResponse(
    string Category,
    long Credits,
    int CrystalSlime,
    List<ShopItemInfo> Items,
    long Redeems,
    int MinimumPokemon,
    int PokemonCount,
    bool MeetsRequirement,
    List<ChestInfo> Chests);

/// <summary>
///     Buys a chest.
/// </summary>
/// <param name="Type">rare, mythic or legend.</param>
/// <param name="Currency">credits or redeems.</param>
public record ChestBuyRequest(string Type, string Currency);

/// <summary>
///     Buys an item.
/// </summary>
/// <param name="Item">The item's name.</param>
/// <param name="Amount">How many to buy, or null for the default.</param>
public record ShopBuyRequest(string Item, int? Amount);
