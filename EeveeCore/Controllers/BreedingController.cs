using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Breeding.Services;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     The user's breeding list, breeding cooldown and daycare.
/// </summary>
/// <param name="breedingService">The breeding service.</param>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="cards">Builds Pokémon cards.</param>
[Route("api/breeding")]
public class BreedingController(
    BreedingService breedingService,
    LinqToDbConnectionProvider dbProvider,
    PokemonCardFactory cards) : PokeFluxUserControllerBase
{
    /// <summary>
    ///     Gets the females on the breeding list, the breeding cooldown and how full the daycare is.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        await using (var db = await dbProvider.GetConnectionAsync())
        {
            if (!await db.Users.AnyAsync(u => u.UserId == UserId))
                return Error(404, "You have not started! Start with the start command first.");
        }

        var positions = await breedingService.GetUserFemalesAsync(UserId);
        var females = (await breedingService.GetBreedingFemalesBatch(UserId, positions))
            .Select(f => f.Pokemon)
            .ToList();
        var context = await cards.GetContextAsync(UserId, females.Select(f => f.Id));
        var cooldowns = await breedingService.GetMotherCooldownsAsync(females.Select(f => f.Id).ToList());

        var list = new List<BreedingFemale>();
        foreach (var female in females)
            list.Add(new BreedingFemale(await cards.BuildAsync(female, context),
                cooldowns.TryGetValue(female.Id, out var ends) ? ends : null));

        var cooldown = await breedingService.GetBreedCooldownAsync(UserId);
        var (eggs, limit) = await breedingService.GetDaycareUsageAsync(UserId);
        return Ok(new BreedingResponse(list, (int)Math.Ceiling(cooldown.TotalSeconds), eggs, limit));
    }

    /// <summary>
    ///     Replaces the breeding list. Only the user's female Pokémon and Dittos are kept.
    /// </summary>
    /// <param name="request">The females' IDs.</param>
    [HttpPost("females")]
    public async Task<IActionResult> SetFemales([FromBody] BreedingFemalesRequest request)
    {
        var ids = (request.PokemonIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            await breedingService.ClearUserFemalesAsync(UserId);
            return Ok(new BreedingFemalesResponse([], [], "Cleared your female list."));
        }

        Dictionary<ulong, ulong> numbers;
        await using (var db = await dbProvider.GetConnectionAsync())
        {
            numbers = (await db.UserPokemonOwnerships
                    .Where(o => o.UserId == UserId && ids.Contains(o.PokemonId))
                    .Select(o => new { o.PokemonId, o.Position })
                    .ToListAsync())
                .ToDictionary(o => o.PokemonId, o => o.Position + 1);
        }

        var validation = await breedingService.ValidateFemaleBatchAsync(UserId, numbers.Values.ToList());
        var valid = ids.Where(id => numbers.TryGetValue(id, out var number) && validation.GetValueOrDefault(number))
            .ToList();
        var invalid = ids.Except(valid).ToList();

        if (valid.Count > 0)
            await breedingService.UpdateUserFemalesAsync(UserId, valid.Select(id => numbers[id] - 1).ToList());

        var message = valid.Count > 0
            ? $"Your female Pokémon list has been updated with {valid.Count} Pokémon."
            : "None of those Pokémon can be bred as a female. Your list was not changed.";
        if (valid.Count > 0 && invalid.Count > 0)
            message += $" {invalid.Count} were left out as they are not yours, or not female or Ditto.";

        return Ok(new BreedingFemalesResponse(valid, invalid, message));
    }
}
