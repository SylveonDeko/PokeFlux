using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Parties.Services;
using EeveeCore.Modules.Pokemon.Services;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     The user's loaded party and saved parties.
/// </summary>
/// <param name="partyService">The party service.</param>
/// <param name="pokemonService">Turns Pokémon IDs into numbers.</param>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="cards">Builds Pokémon cards.</param>
[Route("api/party")]
public class PartyController(
    PartyService partyService,
    PokemonService pokemonService,
    LinqToDbConnectionProvider dbProvider,
    PokemonCardFactory cards) : PokeFluxUserControllerBase
{
    private const int MaxPartyNameLength = 20;

    /// <summary>
    ///     Gets the loaded party and the names of the saved parties.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        await using var db = await dbProvider.GetConnectionAsync();
        if (!await db.Users.AnyAsync(u => u.UserId == UserId))
            return Error(404, "You have not started! Start with the start command first.");

        var party = await db.Parties.FirstOrDefaultAsync(p => p.UserId == UserId && p.IsCurrentParty);
        return Ok(new PartyResponse(await cards.BuildSlotsAsync(UserId, PokemonCardFactory.PartySlots(party)),
            await partyService.GetUserParties(UserId)));
    }

    /// <summary>
    ///     Gets a saved party.
    /// </summary>
    /// <param name="name">The party's name.</param>
    [HttpGet("saved/{name}")]
    public async Task<IActionResult> GetSaved(string name)
    {
        name = name.Trim().ToLower();
        await using var db = await dbProvider.GetConnectionAsync();
        var party = await db.Parties.FirstOrDefaultAsync(p => p.UserId == UserId && p.Name == name && !p.IsCurrentParty);
        if (party == null)
            return Error(404, $"You do not have a party with the name `{name}`.");

        return Ok(new SavedPartyResponse(name,
            await cards.BuildSlotsAsync(UserId, PokemonCardFactory.PartySlots(party))));
    }

    /// <summary>
    ///     Puts one of the user's Pokémon in a slot of the loaded party, or empties the slot.
    /// </summary>
    /// <param name="request">The slot and Pokémon.</param>
    [HttpPost("slot")]
    public async Task<IActionResult> SetSlot([FromBody] PartySlotRequest request)
    {
        if (request.Slot is < 1 or > 6)
            return Error(400, "Slot must be between 1 and 6!");

        if (request.PokemonId is not { } pokemonId)
            return FromResult(await partyService.RemovePokemonFromParty(UserId, request.Slot));

        var number = await pokemonService.GetPokemonIndex(UserId, pokemonId);
        if (number == 0)
            return Error(404, "You do not have that Pokemon.");

        return FromResult(await partyService.AddPokemonToParty(UserId, request.Slot, (int)number));
    }

    /// <summary>
    ///     Swaps two slots of the loaded party at once. Either slot may be empty.
    /// </summary>
    /// <param name="request">The two slots.</param>
    [HttpPost("swap")]
    public async Task<IActionResult> Swap([FromBody] PartySwapRequest request) =>
        FromResult(await partyService.SwapPartySlots(UserId, request.SlotA, request.SlotB));

    /// <summary>
    ///     Saves the loaded party under a name.
    /// </summary>
    /// <param name="request">The party's name.</param>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] PartyNameRequest request)
    {
        var name = request.Name?.Trim().ToLower();
        if (string.IsNullOrEmpty(name) || name.Contains(' '))
            return Error(400, "A party name without spaces is required.");
        if (name.Length > MaxPartyNameLength)
            return Error(400, "That party name is too long. Please choose a shorter one.");

        return FromResult(await partyService.RegisterParty(UserId, name));
    }

    /// <summary>
    ///     Loads a saved party as the loaded party.
    /// </summary>
    /// <param name="request">The party's name.</param>
    [HttpPost("load")]
    public async Task<IActionResult> Load([FromBody] PartyNameRequest request)
    {
        var name = request.Name?.Trim().ToLower() ?? "";
        if (!await partyService.DoesPartyExist(UserId, name))
            return Error(404, $"You do not have a party with the name `{name}`.");

        return FromResult(await partyService.LoadParty(UserId, name));
    }

    /// <summary>
    ///     Deletes a saved party.
    /// </summary>
    /// <param name="request">The party's name.</param>
    [HttpPost("deregister")]
    public async Task<IActionResult> Deregister([FromBody] PartyNameRequest request)
    {
        var name = request.Name?.Trim().ToLower() ?? "";
        if (!(await partyService.GetUserParties(UserId)).Contains(name))
            return Error(404, $"You do not have a party with the name `{name}`.");

        return FromResult(await partyService.DeregisterParty(UserId, name));
    }

    /// <summary>
    ///     Puts one of the user's Pokémon in a slot of a saved party, or empties the slot.
    /// </summary>
    /// <param name="name">The party's name.</param>
    /// <param name="request">The slot and Pokémon.</param>
    [HttpPost("saved/{name}/slot")]
    public async Task<IActionResult> SetSavedSlot(string name, [FromBody] PartySlotRequest request)
    {
        name = name.Trim().ToLower();
        if (request.Slot is < 1 or > 6)
            return Error(400, "Slot must be between 1 and 6!");

        if (request.PokemonId is not { } pokemonId)
            return FromResult(await partyService.ClearSavedPartySlot(UserId, request.Slot, name));

        if (name.Length > MaxPartyNameLength || name.Contains(' '))
            return Error(400, "A party name of up to 20 characters without spaces is required.");

        var number = await pokemonService.GetPokemonIndex(UserId, pokemonId);
        if (number == 0)
            return Error(404, "You do not have that Pokemon.");

        return FromResult(await partyService.AddPokemonToPartySlot(UserId, request.Slot, (int)number, name));
    }
}
