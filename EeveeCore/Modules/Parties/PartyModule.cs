using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Parties.Services;

namespace EeveeCore.Modules.Parties;

/// <summary>
///     Commands for loading, registering, and deleting parties.
/// </summary>
[Group("party")]
[Name("Party")]
public class PartyModule : EeveeModule<PartyService>
{
    /// <summary>
    ///     Shows the loaded party.
    /// </summary>
    [Command]
    [Summary("View your loaded party.")]
    public Task Party() => PartyView();

    /// <summary>
    ///     Shows the loaded party, or a saved party.
    /// </summary>
    /// <param name="partyName">The saved party, or none for the loaded party.</param>
    [Command("view")]
    [Summary("View your loaded party or another party you have saved.")]
    public async Task PartyView([Remainder] string? partyName = null)
    {
        var prefix = await GetPrefixAsync();
        if (partyName is not null)
        {
            partyName = await FindPartyAsync(partyName);
            if (partyName is null)
                return;
        }

        var embed = await Service.GetPartyViewEmbed(ctx.User.Id, prefix, partyName);
        if (embed == null)
        {
            await ErrorAsync($"You have not started!\nStart with `{prefix}start` first!");
            return;
        }

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Shows the slots of a saved party and how to change them.
    /// </summary>
    /// <param name="partyName">The saved party.</param>
    [Command("setup")]
    [Summary("See a saved party's slots and how to set them.")]
    public async Task PartySetup(string partyName)
    {
        partyName = partyName.ToLower();
        var (_, party, _, pokemonIndices, pokemonNames) = await Service.GetPartySetupData(ctx.User.Id, partyName);
        if (party == null)
        {
            await ErrorAsync("No party found with the name specified.");
            return;
        }

        await SendAsync(embed: await SetupEmbedAsync("Party Configuration", partyName, pokemonIndices, pokemonNames));
    }

    /// <summary>
    ///     Puts a Pokémon in a slot of a saved party.
    /// </summary>
    /// <param name="partyName">The saved party.</param>
    /// <param name="slot">The slot, 1 to 6.</param>
    /// <param name="pokemon">The Pokémon's number in your list.</param>
    [Command("setup")]
    [Summary("Put a Pokémon in a slot of a saved party.")]
    public async Task PartySetup(string partyName, int slot, int pokemon)
    {
        partyName = partyName.ToLower();
        if (slot is < 1 or > 6)
        {
            await ErrorAsync("Slot must be between 1 and 6!");
            return;
        }

        var result = await Service.AddPokemonToPartySlot(ctx.User.Id, slot, pokemon, partyName);
        if (!result.Success)
        {
            await ErrorAsync(await WithPrefixAsync(result.Message));
            return;
        }

        var (_, _, _, pokemonIndices, pokemonNames) = await Service.GetPartySetupData(ctx.User.Id, partyName);
        await SendAsync(embed: await SetupEmbedAsync($"Slot {slot} Updated!", partyName, pokemonIndices,
            pokemonNames));
    }

    /// <summary>
    ///     Adds a Pokémon to a slot in the loaded party.
    /// </summary>
    /// <param name="slot">The slot, 1 to 6.</param>
    /// <param name="poke">The Pokémon's number, or none for your selected Pokémon.</param>
    [Command("add")]
    [Summary("Add a Pokémon to a slot in your party.")]
    public async Task PartyAdd(int slot, int poke = 0)
    {
        if (slot is < 1 or > 6)
        {
            await ErrorAsync("You only add a Pokemon to a slot between 1 and 6!");
            return;
        }

        var result = await Service.AddPokemonToParty(ctx.User.Id, slot, poke);
        await ReplyResultAsync(result);
    }

    /// <summary>
    ///     Empties a slot in the loaded party.
    /// </summary>
    /// <param name="slot">The slot, 1 to 6.</param>
    [Command("remove")]
    [Summary("Remove a Pokémon from a slot in your party.")]
    public async Task PartyRemove(int slot)
    {
        if (slot is < 1 or > 6)
        {
            await ErrorAsync("Slot must be between 1 and 6!");
            return;
        }

        var result = await Service.RemovePokemonFromParty(ctx.User.Id, slot);
        await ReplyResultAsync(result);
    }

    /// <summary>
    ///     Saves the loaded party under a name.
    /// </summary>
    /// <param name="partyName">The name, up to 20 characters.</param>
    [Command("register")]
    [Summary("Register your current party with a name.")]
    public async Task PartyRegister(string partyName)
    {
        partyName = partyName.ToLower();
        if (partyName.Length > 20)
        {
            await ErrorAsync("That party name is too long. Please choose a shorter one.");
            return;
        }

        var result = await Service.RegisterParty(ctx.User.Id, partyName);
        await ReplyResultAsync(result);
    }

    /// <summary>
    ///     Deletes a saved party after the user confirms.
    /// </summary>
    /// <param name="partyName">The saved party.</param>
    [Command("deregister")]
    [Summary("Deregister a party from your saved parties.")]
    public async Task PartyDeregister(string partyName)
    {
        var name = await FindPartyAsync(partyName);
        if (name is null)
            return;

        if (!await PromptConfirmAsync($"Are you sure you want to deregister party `{name}`?"))
        {
            await ErrorAsync("Party deletion canceled.");
            return;
        }

        var result = await Service.DeregisterParty(ctx.User.Id, name);
        if (result.Success)
            await ConfirmAsync(result.Message);
        else
            await ErrorAsync($"Error: {result.Message}");
    }

    /// <summary>
    ///     Loads a saved party as the active party.
    /// </summary>
    /// <param name="partyName">The saved party.</param>
    [Command("load")]
    [Summary("Load a registered party save by name.")]
    public async Task PartyLoad(string partyName)
    {
        var name = await FindPartyAsync(partyName);
        if (name is null)
            return;

        var result = await Service.LoadParty(ctx.User.Id, name);
        await ReplyResultAsync(result);
    }

    /// <summary>
    ///     Lists the saved parties.
    /// </summary>
    [Command("list")]
    [Summary("List your saved parties.")]
    public async Task PartyList()
    {
        var parties = await Service.GetUserParties(ctx.User.Id);
        if (parties.Count == 0)
        {
            await ErrorAsync(
                $"You do not have any saved parties. Register one with `{await GetPrefixAsync()}party register` first.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("Your Saved Parties")
            .WithColor(new Color(0xDD, 0x00, 0xDD))
            .WithDescription(string.Join("\n", parties));

        await SendAsync(embed: embed);
    }

    private async Task<EmbedBuilder> SetupEmbedAsync(string title, string partyName, int[] pokemonIndices,
        string[] pokemonNames)
    {
        var lines = pokemonNames.Select((name, i) => pokemonIndices[i] > 0
            ? $"{Emojis.Numbers[i]} **{name}** `#{pokemonIndices[i]}`"
            : $"{Emojis.Numbers[i]} None");
        var prefix = await GetPrefixAsync();

        return new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(string.Join("\n", lines) +
                             $"\n\nUse `{prefix}party setup {partyName} <slot> <pokemon number>` " +
                             "to set a Pokémon to a slot in this party.")
            .WithColor(new Color(0xDD, 0x00, 0xDD))
            .WithFooter($"Party: {partyName}");
    }

    private async Task<string?> FindPartyAsync(string partyName)
    {
        partyName = partyName.ToLower();
        var parties = await Service.GetUserParties(ctx.User.Id);
        if (parties.Contains(partyName))
            return partyName;

        var closest = parties
            .Select(p => (Name: p, Distance: p.LevenshteinDistance(partyName)))
            .Where(p => p.Distance <= 3)
            .OrderBy(p => p.Distance)
            .Select(p => p.Name)
            .FirstOrDefault();

        await ErrorAsync(closest is null
            ? $"You do not have a party with the name `{partyName}`."
            : $"You do not have a party with the name `{partyName}`. Did you mean `{closest}`?");
        return null;
    }

    private async Task ReplyResultAsync((bool Success, string Message) result)
    {
        var message = await WithPrefixAsync(result.Message);
        if (result.Success)
            await ConfirmAsync(message);
        else
            await ErrorAsync(message);
    }

    private async Task<string> WithPrefixAsync(string text) => text.Replace("`/", $"`{await GetPrefixAsync()}");
}
