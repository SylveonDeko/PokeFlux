using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Extras.Services;
using EeveeCore.Modules.Pokemon.Services;
using LinqToDB;
using LinqToDB.Async;
using PokemonModel = EeveeCore.Database.Linq.Models.Pokemon.Pokemon;

namespace EeveeCore.Controllers.Common;

/// <summary>
///     Builds the Pokémon cards the dashboard shows, with types and absolute sprite links.
/// </summary>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="pokemonService">Looks up types and sprite paths.</param>
/// <param name="mediaUrls">Turns sprite paths into hosted links.</param>
/// <param name="extrasService">Looks up move details.</param>
public sealed class PokemonCardFactory(
    LinqToDbConnectionProvider dbProvider,
    PokemonService pokemonService,
    MediaUrls mediaUrls,
    ExtrasService extrasService) : INService
{
    private readonly ConcurrentDictionary<string, List<string>> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string, bool, bool, string), string?> _sprites = new();

    /// <summary>
    ///     Gets what a user's cards need to know: their party, selected Pokémon and the numbers of some Pokémon.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="pokemonIds">The Pokémon to look up numbers for.</param>
    public async Task<CardContext> GetContextAsync(ulong userId, IEnumerable<ulong> pokemonIds)
    {
        var ids = pokemonIds.Distinct().ToList();
        await using var db = await dbProvider.GetConnectionAsync();

        var selected = await db.Users.Where(u => u.UserId == userId).Select(u => u.Selected).FirstOrDefaultAsync();
        var party = await db.Parties.FirstOrDefaultAsync(p => p.UserId == userId && p.IsCurrentParty);
        var numbers = ids.Count == 0
            ? new Dictionary<ulong, ulong>()
            : (await db.UserPokemonOwnerships
                .Where(o => o.UserId == userId && ids.Contains(o.PokemonId))
                .Select(o => new { o.PokemonId, o.Position })
                .ToListAsync())
            .ToDictionary(o => o.PokemonId, o => o.Position + 1);

        return new CardContext(PartySlots(party).Where(id => id is > 0).Select(id => id!.Value).ToHashSet(),
            selected, numbers);
    }

    /// <summary>
    ///     Builds a card for a Pokémon.
    /// </summary>
    /// <param name="pokemon">The Pokémon.</param>
    /// <param name="context">The owner's party, selection and numbers.</param>
    /// <param name="marketListed">Whether the Pokémon is listed on the market.</param>
    public async Task<PokemonCard> BuildAsync(PokemonModel pokemon, CardContext context, bool marketListed = false)
    {
        var shiny = pokemon.Shiny ?? false;
        var radiant = pokemon.Radiant ?? false;
        return new PokemonCard(
            pokemon.Id,
            context.Numbers.TryGetValue(pokemon.Id, out var number) ? (long)number : null,
            pokemon.PokemonName,
            Nickname(pokemon.Nickname, pokemon.PokemonName),
            pokemon.Level,
            IvPercent(pokemon.HpIv + pokemon.AttackIv + pokemon.DefenseIv + pokemon.SpecialAttackIv +
                      pokemon.SpecialDefenseIv + pokemon.SpeedIv),
            shiny,
            radiant,
            SkinName(pokemon.Skin),
            GenderName(pokemon.Gender),
            pokemon.Favorite,
            context.Party.Contains(pokemon.Id),
            context.Selected == pokemon.Id,
            pokemon.Champion,
            marketListed || pokemon.MarketEnlist,
            HeldItem(pokemon.HeldItem),
            await TypesAsync(pokemon.PokemonName),
            await SpriteUrlAsync(pokemon.PokemonName, shiny, radiant, pokemon.Skin));
    }

    /// <summary>
    ///     Builds cards for Pokémon, in the order given.
    /// </summary>
    /// <param name="pokemon">The Pokémon.</param>
    /// <param name="context">The owner's party, selection and numbers.</param>
    public async Task<List<PokemonCard>> BuildAsync(IEnumerable<PokemonModel> pokemon, CardContext context)
    {
        var cards = new List<PokemonCard>();
        foreach (var p in pokemon)
            cards.Add(await BuildAsync(p, context));
        return cards;
    }

    /// <summary>
    ///     Builds cards for slots that hold Pokémon IDs, such as party or hatchery slots.
    /// </summary>
    /// <param name="userId">The owner's ID.</param>
    /// <param name="ids">The ID in each slot, or null for an empty slot.</param>
    /// <returns>A card for each slot, null where the slot is empty or the Pokémon is gone.</returns>
    public async Task<List<PokemonCard?>> BuildSlotsAsync(ulong userId, IReadOnlyList<ulong?> ids)
    {
        var wanted = ids.Where(id => id is > 0).Select(id => id!.Value).Distinct().ToList();
        var context = await GetContextAsync(userId, wanted);

        await using var db = await dbProvider.GetConnectionAsync();
        var pokemon = wanted.Count == 0
            ? new Dictionary<ulong, PokemonModel>()
            : (await db.UserPokemon.Where(p => wanted.Contains(p.Id)).ToListAsync()).ToDictionary(p => p.Id);

        var cards = new List<PokemonCard?>();
        foreach (var id in ids)
            cards.Add(id is > 0 && pokemon.TryGetValue(id.Value, out var p) ? await BuildAsync(p, context) : null);
        return cards;
    }

    /// <summary>
    ///     Builds a card for an entry of the Pokémon list.
    /// </summary>
    /// <param name="entry">The list entry.</param>
    /// <param name="party">The IDs of the Pokémon in the owner's party.</param>
    /// <param name="selected">The ID of the owner's selected Pokémon.</param>
    /// <param name="owned">Whether the entry's number is a position in the owner's list.</param>
    public async Task<PokemonCard> BuildAsync(PokemonListEntry entry, HashSet<ulong> party, ulong? selected,
        bool owned = true) =>
        new(entry.BotId,
            owned ? (long)entry.Number : null,
            entry.Name,
            Nickname(entry.Nickname, entry.Name),
            entry.Level,
            Math.Round(entry.IvPercent * 100, 2),
            entry.Shiny,
            entry.Radiant,
            SkinName(entry.Skin),
            GenderName(entry.Gender),
            entry.Favorite,
            party.Contains(entry.BotId),
            selected == entry.BotId,
            entry.Champion,
            !owned || entry.MarketEnlist,
            HeldItem(entry.HeldItem),
            await TypesAsync(entry.Name),
            await SpriteUrlAsync(entry.Name, entry.Shiny, entry.Radiant, entry.Skin));

    /// <summary>
    ///     Gets the types of a species.
    /// </summary>
    /// <param name="pokemonName">The species name.</param>
    public async Task<List<string>> TypesAsync(string pokemonName)
    {
        if (_types.TryGetValue(pokemonName, out var types))
            return types;

        types = await pokemonService.GetPokemonTypes(pokemonName);
        _types[pokemonName] = types;
        return types;
    }

    /// <summary>
    ///     Gets the hosted sprite link of a Pokémon.
    /// </summary>
    /// <param name="pokemonName">The species name.</param>
    /// <param name="shiny">Whether it is shiny.</param>
    /// <param name="radiant">Whether it is radiant.</param>
    /// <param name="skin">Its skin, if any.</param>
    /// <returns>The absolute link, or null when there is no hosted sprite.</returns>
    public async Task<string?> SpriteUrlAsync(string pokemonName, bool shiny, bool radiant, string? skin)
    {
        skin = SkinName(skin);
        var key = (pokemonName.ToLowerInvariant(), shiny, radiant, skin ?? "");
        if (_sprites.TryGetValue(key, out var url))
            return url;

        try
        {
            var (_, path) = await pokemonService.GetPokemonFormInfo(pokemonName, shiny, radiant, skin ?? "");
            url = mediaUrls.Get(path);
            if (url is null && !string.IsNullOrEmpty(skin))
                url = mediaUrls.Get((await pokemonService.GetPokemonFormInfo(pokemonName, shiny, radiant)).ImagePath);
        }
        catch (ArgumentException)
        {
            url = null;
        }

        _sprites[key] = url;
        return url;
    }

    /// <summary>
    ///     Gets the details of moves, with lowercase types and categories.
    /// </summary>
    /// <param name="moves">The move names.</param>
    /// <returns>The details of the moves that exist.</returns>
    public async Task<List<MoveDetails>> MoveDetailsAsync(IEnumerable<string> moves) =>
        (await extrasService.GetMoveDetailsAsync(moves))
        .Select(m => m with { Type = m.Type.ToLowerInvariant(), Category = m.Category.ToLowerInvariant() })
        .ToList();

    /// <summary>
    ///     Gets the details of a Pokémon's four move slots.
    /// </summary>
    /// <param name="pokemon">The Pokémon.</param>
    /// <returns>Four slots, null where a slot is empty or the move is unknown.</returns>
    public async Task<List<MoveDetails?>> MoveSlotsAsync(PokemonModel pokemon)
    {
        var moveNames = pokemon.Moves.Take(4).ToList();
        var details = (await MoveDetailsAsync(moveNames.Where(m => !string.IsNullOrEmpty(m))))
            .GroupBy(m => m.Name)
            .ToDictionary(g => g.Key, g => g.First());
        return Enumerable.Range(0, 4)
            .Select(i => i < moveNames.Count && !string.IsNullOrEmpty(moveNames[i]) &&
                         details.TryGetValue(moveNames[i].ToLower(), out var move)
                ? move
                : null)
            .ToList();
    }

    /// <summary>
    ///     Gets the six slots of a party.
    /// </summary>
    /// <param name="party">The party, or null.</param>
    public static ulong?[] PartySlots(Database.Linq.Models.Pokemon.Party? party) =>
        party is null
            ? new ulong?[6]
            : [party.Slot1, party.Slot2, party.Slot3, party.Slot4, party.Slot5, party.Slot6];

    /// <summary>
    ///     Turns an IV total out of 186 into a percentage.
    /// </summary>
    /// <param name="ivTotal">The sum of the six IVs.</param>
    public static double IvPercent(int ivTotal) => Math.Round(ivTotal / 186.0 * 100, 2);

    /// <summary>
    ///     Turns a stored gender into male, female or genderless, matching the Pokémon list's gender counts.
    /// </summary>
    /// <param name="gender">The stored gender, such as <c>-m</c>.</param>
    public static string GenderName(string? gender) => gender switch
    {
        "-m" => "male",
        "-f" => "female",
        _ => "genderless"
    };

    /// <summary>
    ///     Gets a stored skin, or null when it is empty or <c>NULL</c>.
    /// </summary>
    /// <param name="skin">The stored skin.</param>
    public static string? SkinName(string? skin) =>
        string.IsNullOrWhiteSpace(skin) || skin.Trim().Equals("NULL", StringComparison.OrdinalIgnoreCase)
            ? null
            : skin;

    private static string? Nickname(string? nickname, string name) =>
        string.IsNullOrWhiteSpace(nickname) || nickname == "None" || nickname == name ? null : nickname;

    private static string? HeldItem(string? heldItem) =>
        string.IsNullOrWhiteSpace(heldItem) || heldItem.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null
            : heldItem;
}

/// <summary>
///     What a user's Pokémon cards need to know about the user.
/// </summary>
/// <param name="Party">The IDs of the Pokémon in the user's party.</param>
/// <param name="Selected">The ID of the user's selected Pokémon.</param>
/// <param name="Numbers">The number of each owned Pokémon looked up, by ID.</param>
public record CardContext(HashSet<ulong> Party, ulong? Selected, Dictionary<ulong, ulong> Numbers)
{
    /// <summary>
    ///     A context for Pokémon that do not belong to the user, such as market listings.
    /// </summary>
    public static CardContext None { get; } = new([], null, []);
}
