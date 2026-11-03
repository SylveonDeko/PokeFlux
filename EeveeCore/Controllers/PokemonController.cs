using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Extras.Services;
using EeveeCore.Modules.Pokemon.Services;
using Humanizer;
using Microsoft.AspNetCore.Mvc;
using PokemonModel = EeveeCore.Database.Linq.Models.Pokemon.Pokemon;

namespace EeveeCore.Controllers;

/// <summary>
///     The user's Pokémon: listing, details and the actions the Pokémon commands offer.
/// </summary>
/// <param name="pokemonService">The Pokémon service.</param>
/// <param name="extrasService">Moves and nicknames.</param>
/// <param name="cards">Builds Pokémon cards.</param>
[Route("api")]
public class PokemonController(
    PokemonService pokemonService,
    ExtrasService extrasService,
    PokemonCardFactory cards) : PokeFluxUserControllerBase
{
    private static readonly string[] PokedexVariants = ["national", "shiny", "radiant", "shadow", "skin", "unowned"];

    private static readonly string[] Filters =
        ["all", "shiny", "radiant", "shadow", "legendary", "favorite", "champion", "party", "market", "egg"];

    private static readonly string[] Genders = ["all", "male", "female", "genderless"];

    /// <summary>
    ///     Lists the user's Pokémon a page at a time, sorted, filtered and searched like <c>;p</c>.
    /// </summary>
    /// <param name="query">The sort, filters and search.</param>
    /// <param name="page">The page, from 1.</param>
    /// <param name="pageSize">How many Pokémon a page holds, up to 100.</param>
    [HttpGet("pokemon")]
    public async Task<IActionResult> List([FromQuery] PokemonListQuery query, int page = 1, int pageSize = 24)
    {
        var (result, error) = await QueryListAsync(query);
        if (result is null)
            return error!;

        var (list, stats, party, selected) = result.Value;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var start = (page - 1) * pageSize;
        var owned = query.Filter.ToLowerInvariant() != "market";
        var items = new List<PokemonListItem>();
        foreach (var (entry, i) in list.Skip(start).Take(pageSize).Select((e, i) => (e, i)))
            items.Add(new PokemonListItem(await cards.BuildAsync(entry, party, selected, owned)) { Index = start + i });

        return Ok(new PokemonPage(items, list.Count, page, pageSize, PokemonListStats.From(stats)));
    }

    /// <summary>
    ///     Gets the Pokémon before and after one of the user's Pokémon in a sorted, filtered list.
    /// </summary>
    /// <param name="id">The Pokémon's ID.</param>
    /// <param name="query">The sort, filters and search of the list.</param>
    [HttpGet("pokemon/{id}/neighbours")]
    public async Task<IActionResult> Neighbours(ulong id, [FromQuery] PokemonListQuery query)
    {
        var (result, error) = await QueryListAsync(query);
        if (result is null)
            return error!;

        var (list, _, party, selected) = result.Value;
        var index = list.FindIndex(e => e.BotId == id);
        if (index < 0)
            return Error(404, "That Pokemon is not in this list.");

        var owned = query.Filter.ToLowerInvariant() != "market";
        return Ok(new PokemonNeighbours(
            index > 0 ? await cards.BuildAsync(list[index - 1], party, selected, owned) : null,
            index < list.Count - 1 ? await cards.BuildAsync(list[index + 1], party, selected, owned) : null,
            index, list.Count));
    }

    /// <summary>
    ///     Gets everything about one of the user's Pokémon.
    /// </summary>
    /// <param name="id">The Pokémon's ID.</param>
    [HttpGet("pokemon/{id}")]
    public async Task<IActionResult> Get(ulong id)
    {
        if (await GetOwnedAsync(id) is not { } pokemon)
            return Error(404, "You do not have that Pokemon.");

        var context = await cards.GetContextAsync(UserId, [pokemon.Id]);
        var info = await pokemonService.GetPokemonInfo(pokemon.PokemonName);
        var baseStats = info?.Stats ?? new PokemonStats();
        var calculated = await pokemonService.CalculateStats(pokemon, baseStats);

        var moves = await cards.MoveSlotsAsync(pokemon);

        var forms = (await pokemonService.GetPokemonForms(pokemon.PokemonName))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => f != "None")
            .ToList();
        var evolutionLine = new List<EvolutionStage>();
        foreach (var species in await pokemonService.GetEvolutionChain(pokemon.PokemonName))
            if (!string.IsNullOrEmpty(species.Identifier))
                evolutionLine.Add(new EvolutionStage(species.PokemonId ?? 0,
                    char.ToUpperInvariant(species.Identifier[0]) + species.Identifier[1..],
                    await cards.SpriteUrlAsync(species.Identifier, false, false, null)));

        return Ok(new PokemonDetail(await cards.BuildAsync(pokemon, context))
        {
            Nature = pokemon.Nature,
            Ability = await pokemonService.GetAbilityName(pokemon.PokemonName, pokemon.AbilityIndex),
            Happiness = pokemon.Happiness,
            Experience = pokemon.Experience,
            ExperienceCap = pokemon.ExperienceCap,
            Moves = moves,
            Ivs = new StatBlock(pokemon.HpIv, pokemon.AttackIv, pokemon.DefenseIv, pokemon.SpecialAttackIv,
                pokemon.SpecialDefenseIv, pokemon.SpeedIv),
            Evs = new StatBlock(pokemon.HpEv, pokemon.AttackEv, pokemon.DefenseEv, pokemon.SpecialAttackEv,
                pokemon.SpecialDefenseEv, pokemon.SpeedEv),
            BaseStats = new StatBlock(baseStats.Hp, baseStats.Attack, baseStats.Defense, baseStats.SpecialAttack,
                baseStats.SpecialDefense, baseStats.Speed),
            Stats = new StatBlock(calculated.MaxHp, calculated.Attack, calculated.Defense, calculated.SpecialAttack,
                calculated.SpecialDefense, calculated.Speed),
            CaughtBy = pokemon.CaughtBy is > 0 ? await pokemonService.GetTrainerNickname(pokemon.CaughtBy.Value) : null,
            CaughtById = pokemon.CaughtBy is > 0 ? pokemon.CaughtBy : null,
            CaughtAt = pokemon.Timestamp,
            Tradable = pokemon.Tradable,
            Breedable = pokemon.Breedable,
            Tags = pokemon.Tags.Where(t => !string.IsNullOrEmpty(t)).ToList(),
            Forms = forms,
            EvolutionLine = evolutionLine
        });
    }

    /// <summary>
    ///     Selects one of the user's Pokémon.
    /// </summary>
    /// <param name="id">The Pokémon's ID.</param>
    [HttpPost("pokemon/{id}/select")]
    public async Task<IActionResult> Select(ulong id)
    {
        var number = await pokemonService.GetPokemonIndex(UserId, id);
        if (number == 0)
            return Error(404, "You do not have that Pokemon.");

        return FromResult(await pokemonService.SelectPokemon(UserId, number));
    }

    /// <summary>
    ///     Favorites or unfavorites one of the user's Pokémon.
    /// </summary>
    /// <param name="id">The Pokémon's ID.</param>
    /// <param name="request">Whether it is a favorite.</param>
    [HttpPost("pokemon/{id}/favorite")]
    public async Task<IActionResult> Favorite(ulong id, [FromBody] FavoriteRequest request) =>
        FromResult(await pokemonService.SetFavoriteAsync(UserId, id, request.Favorite));

    /// <summary>
    ///     Sets or clears the nickname of one of the user's Pokémon.
    /// </summary>
    /// <param name="id">The Pokémon's ID.</param>
    /// <param name="request">The nickname, or null to clear it.</param>
    [HttpPost("pokemon/{id}/nickname")]
    public async Task<IActionResult> Nickname(ulong id, [FromBody] NicknameRequest request) =>
        FromResult(await extrasService.SetNicknameAsync(UserId, id, request.Nickname));

    /// <summary>
    ///     Teaches one of the user's Pokémon a move from its learnset.
    /// </summary>
    /// <param name="id">The Pokémon's ID.</param>
    /// <param name="request">The slot and move.</param>
    [HttpPost("pokemon/{id}/moves")]
    public async Task<IActionResult> LearnMove(ulong id, [FromBody] MoveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Move))
            return Error(400, "A move is required.");

        return FromResult(await extrasService.LearnMoveAsync(UserId, id, request.Slot, request.Move));
    }

    /// <summary>
    ///     Lists the moves one of the user's Pokémon can learn.
    /// </summary>
    /// <param name="id">The Pokémon's ID.</param>
    [HttpGet("pokemon/{id}/learnset")]
    public async Task<IActionResult> Learnset(ulong id)
    {
        if (await GetOwnedAsync(id) is not { } pokemon)
            return Error(404, "You do not have that Pokemon.");

        if (pokemon.PokemonName.Equals("egg", StringComparison.OrdinalIgnoreCase))
            return Error(400, "There are no available moves for an Egg");

        var moves = await extrasService.GetMoves(pokemon.PokemonName.ToLower());
        if (moves == null)
            return Error(400, "That pokemon cannot learn any moves! You might need to deform it first.");

        return Ok(await cards.MoveDetailsAsync(moves.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    ///     Releases one of the user's Pokémon. The first Pokémon, favorites, party members, eggs in a hatchery and
    ///     listed Pokémon are refused.
    /// </summary>
    /// <param name="id">The Pokémon's ID.</param>
    [HttpPost("pokemon/{id}/release")]
    public async Task<IActionResult> Release(ulong id) =>
        FromResult(await pokemonService.ReleasePokemonAsync(UserId, id));

    /// <summary>
    ///     Gets the user's Pokédex for a variant.
    /// </summary>
    /// <param name="variant">national, shiny, radiant, shadow, skin or unowned.</param>
    [HttpGet("pokedex")]
    public async Task<IActionResult> Pokedex(string variant = "national")
    {
        variant = variant.ToLowerInvariant();
        if (!PokedexVariants.Contains(variant))
            return Error(400, $"Unknown variant. Use one of: {string.Join(", ", PokedexVariants)}.");

        var species = (await pokemonService.GetAllPokemon())
            .Where(p => !string.IsNullOrEmpty(p.Identifier) && p.PokemonId > 0)
            .ToList();
        var owned = (await pokemonService.GetUserPokemons(UserId)).AsEnumerable();
        owned = variant switch
        {
            "shiny" => owned.Where(p => p.Shiny == true),
            "radiant" => owned.Where(p => p.Radiant == true),
            "shadow" => owned.Where(p => p.Skin == "shadow"),
            "skin" => owned.Where(p => PokemonCardFactory.SkinName(p.Skin) is not null),
            _ => owned
        };
        var counts = owned
            .GroupBy(p => p.PokemonName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var (shiny, radiant, skin) = variant switch
        {
            "shiny" => (true, false, null),
            "radiant" => (false, true, null),
            "shadow" => (false, false, "shadow"),
            _ => (false, false, (string?)null)
        };
        var sprites = new ConcurrentDictionary<string, string?>();
        await Parallel.ForEachAsync(species, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (p, _) => sprites[p.Identifier] = await cards.SpriteUrlAsync(p.Identifier, shiny, radiant, skin));

        var entries = species
            .Select(p => new PokedexEntry(p.PokemonId ?? 0, p.Identifier.Titleize(),
                counts.ContainsKey(p.Identifier), counts.GetValueOrDefault(p.Identifier),
                sprites.GetValueOrDefault(p.Identifier)))
            .ToList();
        var caught = entries.Count(e => e.Caught);
        if (variant == "unowned")
            entries = entries.Where(e => !e.Caught).ToList();

        return Ok(new PokedexResponse(variant, caught, species.Count, entries));
    }

    private async Task<((List<PokemonListEntry> List, Dictionary<string, int> Stats, HashSet<ulong> Party,
        ulong? Selected)? Result, IActionResult? Error)> QueryListAsync(PokemonListQuery query)
    {
        var filter = query.Filter.ToLowerInvariant();
        var gender = query.Gender.ToLowerInvariant();
        if (!Filters.Contains(filter))
            return (null, Error(400, $"Unknown filter. Use one of: {string.Join(", ", Filters)}."));
        if (!Genders.Contains(gender))
            return (null, Error(400, $"Unknown gender. Use one of: {string.Join(", ", Genders)}."));

        var sortOrder = query.Sort.ToLowerInvariant() switch
        {
            "iv" => SortOrder.Iv,
            "level" => SortOrder.Level,
            "name" => SortOrder.Name,
            "recent" => SortOrder.Recent,
            "type" => SortOrder.Type,
            "favorite" => SortOrder.Favorite,
            "party" => SortOrder.Party,
            "champion" => SortOrder.Champion,
            "default" => SortOrder.Default,
            _ => (SortOrder?)null
        };
        if (sortOrder is null)
            return (null, Error(400,
                "Unknown sort. Use default, iv, level, name, recent, type, favorite, party or champion."));

        var result = await pokemonService.GetFilteredPokemonList(UserId, sortOrder.Value, filter, gender,
            query.Search?.Trim() ?? "", query.HeldItem, query.Breedable);
        return result.Stats is null
            ? (null, Error(404, "You have not started! Start with the start command first."))
            : (result, null);
    }

    private async Task<PokemonModel?> GetOwnedAsync(ulong id) =>
        id == 0 || await pokemonService.GetPokemonIndex(UserId, id) == 0
            ? null
            : await pokemonService.GetPokemonById(id);
}
