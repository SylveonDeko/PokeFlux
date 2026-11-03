using System.Text.RegularExpressions;
using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Database.Models.Mongo.Pokemon;
using EeveeCore.Modules.Extras.Services;
using EeveeCore.Modules.Pokemon.Services;
using EeveeCore.Services.Impl;
using Humanizer;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace EeveeCore.Controllers;

/// <summary>
///     Species information from the game data, for any species whether the user owns it or not.
/// </summary>
/// <param name="pokemonService">The Pokémon service.</param>
/// <param name="extrasService">Learnsets.</param>
/// <param name="cards">Sprites and move details.</param>
/// <param name="mongo">The game data.</param>
[Route("api/species")]
public class SpeciesController(
    PokemonService pokemonService,
    ExtrasService extrasService,
    PokemonCardFactory cards,
    IMongoService mongo) : PokeFluxUserControllerBase
{
    private static readonly string[] Triggers = ["", "level-up", "trade", "use-item", "shed"];

    /// <summary>
    ///     Gets a species or form by its ID or identifier.
    /// </summary>
    /// <param name="idOrName">A species ID such as <c>133</c>, or an identifier such as <c>vulpix-alola</c>.</param>
    [HttpGet("{idOrName}")]
    public async Task<IActionResult> Get(string idOrName)
    {
        if (await FindFormAsync(idOrName) is not { } form)
            return Error(404, "There is no Pokemon by that name.");

        var info = await pokemonService.GetPokemonInfo(form.Identifier);
        var root = await RootAsync(form);
        var pfile = await mongo.PFile.Find(p => p.Identifier == form.Identifier).FirstOrDefaultAsync()
                    ?? await mongo.PFile.Find(p => p.Identifier == root.Identifier).FirstOrDefaultAsync();
        var speciesId = pfile?.PokemonId ?? form.PokemonId;

        var abilityRefs = (await mongo.PokeAbilities.Find(a => a.PokemonId == form.PokemonId).ToListAsync())
            .OrderBy(a => a.Slot)
            .ToList();
        var abilityIds = abilityRefs.Select(a => a.AbilityId).ToList();
        var abilityNames = (await mongo.Abilities.Find(a => abilityIds.Contains(a.AbilityId)).ToListAsync())
            .GroupBy(a => a.AbilityId)
            .ToDictionary(g => g.Key, g => g.First().Identifier);
        var abilities = abilityRefs
            .Where(a => abilityNames.ContainsKey(a.AbilityId))
            .Select(a => new SpeciesAbility(abilityNames[a.AbilityId], a.IsHidden == 1))
            .ToList();

        var eggGroupRef = await mongo.EggGroups.Find(e => e.SpeciesId == form.PokemonId).FirstOrDefaultAsync()
                          ?? await mongo.EggGroups.Find(e => e.SpeciesId == speciesId).FirstOrDefaultAsync();
        var groupIds = eggGroupRef?.Groups ?? [];
        var groupNames = (await mongo.EggGroupsInfo.Find(e => groupIds.Contains(e.GroupId)).ToListAsync())
            .ToDictionary(e => e.GroupId, e => e.Identifier);
        var eggGroups = groupIds.Where(groupNames.ContainsKey).Select(id => groupNames[id]).ToList();

        var moves = await extrasService.GetMoves(form.Identifier);
        var learnset = moves is null
            ? []
            : (await cards.MoveDetailsAsync(moves.Distinct(StringComparer.OrdinalIgnoreCase)))
            .OrderBy(m => m.Name)
            .ToList();

        var (owned, ownedShiny) = await pokemonService.CountOwnedSpecies(UserId, form.Identifier);
        var stats = info?.Stats ?? new Modules.Pokemon.Services.PokemonStats();

        return Ok(new SpeciesDetail
        {
            SpeciesId = speciesId,
            FormId = form.PokemonId,
            Identifier = form.Identifier,
            Name = form.Identifier.Titleize(),
            FormName = string.IsNullOrEmpty(form.FormIdentifier) ? null : form.FormIdentifier,
            Mega = form.IsMega == 1,
            BattleOnly = form.IsBattleOnly == 1,
            SpriteUrl = await cards.SpriteUrlAsync(form.Identifier, false, false, null),
            ShinySpriteUrl = await cards.SpriteUrlAsync(form.Identifier, true, false, null),
            Types = info?.Types ?? await cards.TypesAsync(form.Identifier),
            BaseStats = new StatBlock(stats.Hp, stats.Attack, stats.Defense, stats.SpecialAttack,
                stats.SpecialDefense, stats.Speed),
            Abilities = abilities,
            Generation = pfile?.GenerationId,
            EggGroups = eggGroups,
            FemalePercent = pfile?.GenderRate is >= 0 and var rate ? rate * 12.5 : null,
            CatchRate = pfile?.CaptureRate,
            BaseHappiness = pfile?.BaseHappiness,
            HatchCounter = pfile?.HatchCounter,
            Baby = pfile?.IsBaby == 1,
            WeightKg = form.Weight is > 0 ? form.Weight / 10.0 : null,
            Evolution = await EvolutionAsync(pfile?.Identifier ?? root.Identifier),
            Forms = await FormsAsync(form, root),
            Learnset = learnset,
            Owned = owned,
            OwnedShiny = ownedShiny
        });
    }

    private async Task<Form?> FindFormAsync(string idOrName)
    {
        if (!int.TryParse(idOrName, out var id))
        {
            var name = Regex.Replace(idOrName.Trim().ToLowerInvariant(), @"\s+", "-");
            return name == "egg" ? null : await mongo.Forms.Find(f => f.Identifier == name).FirstOrDefaultAsync();
        }

        var species = await mongo.PFile.Find(p => p.PokemonId == id).FirstOrDefaultAsync();
        if (!string.IsNullOrEmpty(species?.Identifier) &&
            await mongo.Forms.Find(f => f.Identifier == species.Identifier).FirstOrDefaultAsync() is { } form)
            return form;

        return (await mongo.Forms.Find(f => f.PokemonId == id).ToListAsync())
            .Where(f => f.Identifier != "egg")
            .OrderBy(f => f.FormOrder)
            .ThenBy(f => f.Order)
            .FirstOrDefault();
    }

    private async Task<Form> RootAsync(Form form)
    {
        if (!string.IsNullOrEmpty(form.FormIdentifier) && form.Identifier.EndsWith($"-{form.FormIdentifier}"))
        {
            var baseName = form.Identifier[..^(form.FormIdentifier.Length + 1)];
            if (await mongo.Forms.Find(f => f.Identifier == baseName).FirstOrDefaultAsync() is { } stripped)
                return stripped;
        }

        if (form.BaseId is { } baseId && baseId != form.PokemonId &&
            await mongo.PFile.Find(p => p.PokemonId == baseId).FirstOrDefaultAsync() is { } species &&
            await mongo.Forms.Find(f => f.Identifier == species.Identifier).FirstOrDefaultAsync() is { } baseForm)
            return baseForm;

        return form;
    }

    private async Task<List<SpeciesForm>> FormsAsync(Form form, Form root)
    {
        var baseIds = new[] { root.PokemonId, root.BaseId ?? root.PokemonId }.Distinct().ToList();
        var prefix = $"^{Regex.Escape(root.Identifier)}-";
        var candidates = await mongo.Forms.Find(Builders<Form>.Filter.Or(
            Builders<Form>.Filter.In(f => f.BaseId, baseIds.Select(id => (int?)id)),
            Builders<Form>.Filter.Regex(f => f.Identifier, new MongoDB.Bson.BsonRegularExpression(prefix)),
            Builders<Form>.Filter.Eq(f => f.Identifier, root.Identifier))).ToListAsync();

        var family = candidates
            .Where(f => f.Identifier == root.Identifier || (f.BaseId is { } id && baseIds.Contains(id)) ||
                        (!string.IsNullOrEmpty(f.FormIdentifier) &&
                         f.Identifier == $"{root.Identifier}-{f.FormIdentifier}"))
            .Where(f => f.Identifier != form.Identifier)
            .DistinctBy(f => f.Identifier)
            .OrderBy(f => f.Identifier == root.Identifier ? 0 : 1)
            .ThenBy(f => f.PokemonId)
            .ThenBy(f => f.Order)
            .ToList();

        var forms = new List<SpeciesForm>();
        foreach (var f in family)
            forms.Add(new SpeciesForm(f.Identifier, f.Identifier.Titleize(),
                string.IsNullOrEmpty(f.FormIdentifier) ? null : f.FormIdentifier, f.IsMega == 1,
                await cards.SpriteUrlAsync(f.Identifier, false, false, null)));
        return forms;
    }

    private async Task<List<SpeciesEvolution>> EvolutionAsync(string identifier)
    {
        var chain = (await pokemonService.GetEvolutionChain(identifier))
            .Where(p => !string.IsNullOrEmpty(p.Identifier) && p.PokemonId is not null)
            .ToList();
        if (chain.Count == 0)
            return [];

        var ids = chain.Select(p => p.PokemonId!.Value).ToList();
        var evolutions = (await mongo.Evolution.Find(e => ids.Contains(e.EvolvedSpeciesId)).ToListAsync())
            .GroupBy(e => e.EvolvedSpeciesId)
            .ToDictionary(g => g.Key, g => g.First());

        var itemIds = evolutions.Values.SelectMany(e => new[] { e.TriggerItemId, e.HeldItemId })
            .OfType<int>().Distinct().ToList();
        var items = (await mongo.Items.Find(i => itemIds.Contains(i.ItemId)).ToListAsync())
            .GroupBy(i => i.ItemId)
            .ToDictionary(g => g.Key, g => g.First().Identifier);
        var moveIds = evolutions.Values.Select(e => e.KnownMoveId).OfType<int>().Distinct().ToList();
        var moves = (await mongo.Moves.Find(m => moveIds.Contains(m.MoveId)).ToListAsync())
            .GroupBy(m => m.MoveId)
            .ToDictionary(g => g.Key, g => g.First().Identifier);
        var inChain = ids.ToHashSet();

        var stages = new List<SpeciesEvolution>();
        foreach (var species in chain)
        {
            var from = species.EvolvesFromSpeciesId is { } parent && inChain.Contains(parent) ? parent : (int?)null;
            var method = from is not null && evolutions.TryGetValue(species.PokemonId!.Value, out var evo)
                ? new EvolutionMethod
                {
                    Trigger = evo.EvolutionTriggerId > 0 && evo.EvolutionTriggerId < Triggers.Length
                        ? Triggers[evo.EvolutionTriggerId]
                        : "level-up",
                    Level = evo.MinimumLevel,
                    Item = evo.TriggerItemId is { } item ? items.GetValueOrDefault(item) : null,
                    HeldItem = evo.HeldItemId is { } held ? items.GetValueOrDefault(held) : null,
                    Gender = evo.GenderId switch { 1 => "female", 2 => "male", _ => null },
                    KnownMove = evo.KnownMoveId is { } move ? moves.GetValueOrDefault(move) : null,
                    Happiness = evo.MinimumHappiness,
                    AttackVsDefense = evo.RelativePhysicalStats switch
                    {
                        1 => "higher",
                        -1 => "lower",
                        0 => "equal",
                        _ => null
                    },
                    Region = string.IsNullOrEmpty(evo.Region) ? null : evo.Region
                }
                : null;
            stages.Add(new SpeciesEvolution(species.PokemonId!.Value, species.Identifier,
                species.Identifier.Titleize(), await cards.SpriteUrlAsync(species.Identifier, false, false, null),
                from, method));
        }

        return stages;
    }
}
