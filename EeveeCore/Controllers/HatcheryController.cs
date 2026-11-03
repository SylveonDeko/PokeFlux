using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Breeding.Services;
using EeveeCore.Modules.Parties.Services;
using EeveeCore.Modules.Pokemon.Services;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     The user's egg hatchery.
/// </summary>
/// <param name="hatcheryService">The hatchery service.</param>
/// <param name="pokemonService">Turns Pokémon IDs into numbers.</param>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="cards">Builds Pokémon cards.</param>
/// <param name="breedingService">Knows the steps a new egg starts with.</param>
[Route("api/hatchery")]
public class HatcheryController(
    HatcheryService hatcheryService,
    PokemonService pokemonService,
    LinqToDbConnectionProvider dbProvider,
    PokemonCardFactory cards,
    BreedingService breedingService) : PokeFluxUserControllerBase
{
    /// <summary>
    ///     Gets the three hatchery groups with the egg and steps left in each slot.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        await using var db = await dbProvider.GetConnectionAsync();
        if (!await db.Users.AnyAsync(u => u.UserId == UserId))
            return Error(404, "You have not started! Start with the start command first.");

        var groups = await hatcheryService.GetHatcheryGroupsAsync(UserId);
        var eggIds = groups.SelectMany(g => g.Slots).Where(id => id is > 0).Select(id => id!.Value).ToList();
        var eggData = (await db.UserPokemon
                .Where(p => eggIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Counter, p.Name })
                .ToListAsync())
            .ToDictionary(p => p.Id);
        var startingSteps = await breedingService.GetEggStepsAsync(eggData.Values.Select(e => e.Name ?? ""));

        int TotalSteps(ulong eggId)
        {
            if (!eggData.TryGetValue(eggId, out var egg))
                return 0;
            var counter = egg.Counter ?? 0;
            return Math.Max(counter, startingSteps.GetValueOrDefault(egg.Name?.ToLower() ?? "", counter));
        }

        var result = new List<HatcheryGroupInfo>();
        foreach (var group in groups)
        {
            var eggs = await cards.BuildSlotsAsync(UserId, group.Slots);
            var slots = group.Slots
                .Select((id, i) => id is { } eggId
                    ? new HatcherySlotInfo(i + 1, i < group.MaxSlots, eggs[i],
                        eggData.TryGetValue(eggId, out var egg) ? egg.Counter ?? 0 : 0, TotalSteps(eggId))
                    : new HatcherySlotInfo(i + 1, i < group.MaxSlots, eggs[i], 0, 0))
                .ToList();
            result.Add(new HatcheryGroupInfo(group.Group, group.SpeedModifier, group.MaxSlots, slots));
        }

        return Ok(result);
    }

    /// <summary>
    ///     Puts one of the user's eggs in a hatchery slot.
    /// </summary>
    /// <param name="group">The group, from 1 to 3.</param>
    /// <param name="slot">The slot, from 1 to 10.</param>
    /// <param name="request">The egg.</param>
    [HttpPost("{group:int}/slot/{slot:int}")]
    public async Task<IActionResult> AddEgg(int group, int slot, [FromBody] HatcherySlotRequest request)
    {
        if (group is < 1 or > 3 || slot is < 1 or > 10)
            return Error(400, "Invalid group or slot number.");

        var number = await pokemonService.GetPokemonIndex(UserId, request.PokemonId);
        if (number == 0)
            return Error(404, "You do not have that Pokemon.");

        return FromResult(await hatcheryService.AddEggToHatchery(UserId, number, (short)group, slot));
    }

    /// <summary>
    ///     Takes the egg out of a hatchery slot.
    /// </summary>
    /// <param name="group">The group, from 1 to 3.</param>
    /// <param name="slot">The slot, from 1 to 10.</param>
    [HttpDelete("{group:int}/slot/{slot:int}")]
    public async Task<IActionResult> RemoveEgg(int group, int slot) =>
        FromResult(await hatcheryService.RemoveEggFromHatchery(UserId, group, slot));
}
