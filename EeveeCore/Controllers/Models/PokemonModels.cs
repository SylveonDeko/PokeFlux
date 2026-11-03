using EeveeCore.Modules.Extras.Services;

namespace EeveeCore.Controllers.Models;

/// <summary>
///     A Pokémon as the dashboard lists it.
/// </summary>
/// <param name="Id">The Pokémon's global ID.</param>
/// <param name="Number">Its number in the owner's list, or null when the owner no longer holds it.</param>
/// <param name="Name">The species name.</param>
/// <param name="Nickname">The nickname, or null when it has none.</param>
/// <param name="Level">The level.</param>
/// <param name="IvPercent">The IV total as a percentage.</param>
/// <param name="Shiny">Whether it is shiny.</param>
/// <param name="Radiant">Whether it is radiant.</param>
/// <param name="Skin">Its skin, or null.</param>
/// <param name="Gender">male, female or genderless.</param>
/// <param name="Favorite">Whether it is a favorite.</param>
/// <param name="InParty">Whether it is in the loaded party.</param>
/// <param name="Selected">Whether it is the selected Pokémon.</param>
/// <param name="Champion">Whether it is a champion.</param>
/// <param name="MarketListed">Whether it is listed on the market.</param>
/// <param name="HeldItem">The held item, or null.</param>
/// <param name="Types">The species' type names.</param>
/// <param name="SpriteUrl">The absolute sprite link, or null.</param>
public record PokemonCard(
    ulong Id,
    long? Number,
    string Name,
    string? Nickname,
    int Level,
    double IvPercent,
    bool Shiny,
    bool Radiant,
    string? Skin,
    string Gender,
    bool Favorite,
    bool InParty,
    bool Selected,
    bool Champion,
    bool MarketListed,
    string? HeldItem,
    List<string> Types,
    string? SpriteUrl);

/// <summary>
///     A Pokémon in a sorted, filtered list.
/// </summary>
public record PokemonListItem : PokemonCard
{
    /// <summary>
    ///     Creates a list item from a card.
    /// </summary>
    /// <param name="card">The card fields.</param>
    public PokemonListItem(PokemonCard card) : base(card)
    {
    }

    /// <summary>Its place in the whole sorted, filtered list, from 0.</summary>
    public required int Index { get; init; }
}

/// <summary>
///     A page of the user's Pokémon.
/// </summary>
/// <param name="Items">The Pokémon on this page.</param>
/// <param name="Total">How many Pokémon match the filters.</param>
/// <param name="Page">The page number, from 1.</param>
/// <param name="PageSize">How many Pokémon a page holds.</param>
/// <param name="Stats">Counts of the matching Pokémon by kind.</param>
public record PokemonPage(List<PokemonListItem> Items, int Total, int Page, int PageSize, PokemonListStats Stats);

/// <summary>
///     How the user's Pokémon list is sorted, filtered and searched.
/// </summary>
public record PokemonListQuery
{
    /// <summary>default, iv, level, name, recent, type, favorite, party or champion.</summary>
    public string Sort { get; init; } = "default";

    /// <summary>all, shiny, radiant, shadow, legendary, favorite, champion, party, market or egg.</summary>
    public string Filter { get; init; } = "all";

    /// <summary>all, male, female or genderless.</summary>
    public string Gender { get; init; } = "all";

    /// <summary>Text to find in names, nicknames, tags and moves.</summary>
    public string? Search { get; init; }

    /// <summary>Only Pokémon holding this item.</summary>
    public string? HeldItem { get; init; }

    /// <summary>Only Pokémon the breeding list accepts: females and Dittos.</summary>
    public bool Breedable { get; init; }
}

/// <summary>
///     The Pokémon around one Pokémon in a sorted, filtered list.
/// </summary>
/// <param name="Previous">The Pokémon before it, or null at the start.</param>
/// <param name="Next">The Pokémon after it, or null at the end.</param>
/// <param name="Index">Its place in the list, from 0.</param>
/// <param name="Total">How many Pokémon the list holds.</param>
public record PokemonNeighbours(PokemonCard? Previous, PokemonCard? Next, int Index, int Total);

/// <summary>
///     Counts of the Pokémon a list matches, by kind.
/// </summary>
/// <param name="TotalCount">Every Pokémon the user owns, whatever the filters.</param>
/// <param name="Total">The matching Pokémon.</param>
/// <param name="Shiny">Matching shiny Pokémon.</param>
/// <param name="Radiant">Matching radiant Pokémon.</param>
/// <param name="Shadow">Matching shadow Pokémon.</param>
/// <param name="Legendary">Matching legendary Pokémon.</param>
/// <param name="Favorite">Matching favorites.</param>
/// <param name="Champion">Matching champions.</param>
/// <param name="Market">Matching Pokémon listed on the market.</param>
/// <param name="Male">Matching male Pokémon.</param>
/// <param name="Female">Matching female Pokémon.</param>
/// <param name="Genderless">Matching genderless Pokémon.</param>
public record PokemonListStats(
    int TotalCount,
    int Total,
    int Shiny,
    int Radiant,
    int Shadow,
    int Legendary,
    int Favorite,
    int Champion,
    int Market,
    int Male,
    int Female,
    int Genderless)
{
    /// <summary>
    ///     Reads the counts the Pokémon list returns.
    /// </summary>
    /// <param name="stats">The counts, keyed like <c>TotalCount</c> and <c>Shiny</c>.</param>
    public static PokemonListStats From(Dictionary<string, int> stats) => new(
        stats.GetValueOrDefault("TotalCount"),
        stats.GetValueOrDefault("Total"),
        stats.GetValueOrDefault("Shiny"),
        stats.GetValueOrDefault("Radiant"),
        stats.GetValueOrDefault("Shadow"),
        stats.GetValueOrDefault("Legendary"),
        stats.GetValueOrDefault("Favorite"),
        stats.GetValueOrDefault("Champion"),
        stats.GetValueOrDefault("Market"),
        stats.GetValueOrDefault("Male"),
        stats.GetValueOrDefault("Female"),
        stats.GetValueOrDefault("Genderless"));
}

/// <summary>
///     A value for each of the six stats.
/// </summary>
/// <param name="Hp">HP.</param>
/// <param name="Attack">Attack.</param>
/// <param name="Defense">Defense.</param>
/// <param name="SpecialAttack">Special Attack.</param>
/// <param name="SpecialDefense">Special Defense.</param>
/// <param name="Speed">Speed.</param>
public record StatBlock(int Hp, int Attack, int Defense, int SpecialAttack, int SpecialDefense, int Speed);

/// <summary>
///     Everything about one of the user's Pokémon: the card fields and more.
/// </summary>
public record PokemonDetail : PokemonCard
{
    /// <summary>
    ///     Creates the detail of a Pokémon from its card.
    /// </summary>
    /// <param name="card">The card fields.</param>
    public PokemonDetail(PokemonCard card) : base(card)
    {
    }

    /// <summary>The nature.</summary>
    public required string Nature { get; init; }

    /// <summary>The ability's name.</summary>
    public required string Ability { get; init; }

    /// <summary>The friendship value.</summary>
    public required int Happiness { get; init; }

    /// <summary>The experience towards the next level.</summary>
    public required int Experience { get; init; }

    /// <summary>The experience needed for the next level.</summary>
    public required int ExperienceCap { get; init; }

    /// <summary>The four move slots, null where a slot is empty or the move is unknown.</summary>
    public required List<MoveDetails?> Moves { get; init; }

    /// <summary>The IVs.</summary>
    public required StatBlock Ivs { get; init; }

    /// <summary>The EVs.</summary>
    public required StatBlock Evs { get; init; }

    /// <summary>The species' base stats.</summary>
    public required StatBlock BaseStats { get; init; }

    /// <summary>The calculated stats.</summary>
    public required StatBlock Stats { get; init; }

    /// <summary>The original trainer's nickname, or null when unknown.</summary>
    public required string? CaughtBy { get; init; }

    /// <summary>The original trainer's ID, or null.</summary>
    public required ulong? CaughtById { get; init; }

    /// <summary>When it was caught, or null.</summary>
    public required DateTime? CaughtAt { get; init; }

    /// <summary>Whether it can be traded.</summary>
    public required bool Tradable { get; init; }

    /// <summary>Whether it can be bred.</summary>
    public required bool Breedable { get; init; }

    /// <summary>Its tags.</summary>
    public required List<string> Tags { get; init; }

    /// <summary>The forms its species has.</summary>
    public required List<string> Forms { get; init; }

    /// <summary>The species of its evolution family, each before what it evolves into; empty when it does not evolve.</summary>
    public required List<EvolutionStage> EvolutionLine { get; init; }
}

/// <summary>
///     A species in an evolution line.
/// </summary>
/// <param name="SpeciesId">The species ID.</param>
/// <param name="Name">The species name.</param>
/// <param name="SpriteUrl">The absolute link of its normal sprite, or null.</param>
public record EvolutionStage(int SpeciesId, string Name, string? SpriteUrl);

/// <summary>
///     Changes whether a Pokémon is a favorite.
/// </summary>
/// <param name="Favorite">Whether it is a favorite.</param>
public record FavoriteRequest(bool Favorite);

/// <summary>
///     Sets or clears a Pokémon's nickname.
/// </summary>
/// <param name="Nickname">The nickname, or null to clear it.</param>
public record NicknameRequest(string? Nickname);

/// <summary>
///     Teaches a Pokémon a move.
/// </summary>
/// <param name="Slot">The slot, from 1 to 4.</param>
/// <param name="Move">The move.</param>
public record MoveRequest(int Slot, string Move);

/// <summary>
///     A Pokédex entry.
/// </summary>
/// <param name="SpeciesId">The species ID.</param>
/// <param name="Name">The species name.</param>
/// <param name="Caught">Whether the user owns one of this variant.</param>
/// <param name="Count">How many of this variant the user owns.</param>
/// <param name="SpriteUrl">The absolute link of the species' sprite for this variant, or null.</param>
public record PokedexEntry(int SpeciesId, string Name, bool Caught, int Count, string? SpriteUrl);

/// <summary>
///     The user's Pokédex for one variant.
/// </summary>
/// <param name="Variant">national, shiny, radiant, shadow, skin or unowned.</param>
/// <param name="Caught">How many species the user owns of this variant.</param>
/// <param name="Total">How many species there are.</param>
/// <param name="Entries">The entries; for unowned only the species the user does not own.</param>
public record PokedexResponse(string Variant, int Caught, int Total, List<PokedexEntry> Entries);
