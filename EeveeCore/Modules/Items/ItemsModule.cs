using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Items.Services;

namespace EeveeCore.Modules.Items;

/// <summary>
///     Equipping, unequipping, transferring and using items on Pokémon.
/// </summary>
[Name("Items")]
public class ItemsModule : EeveeModule<ItemsService>
{
    /// <summary>
    ///     Unequips the selected Pokémon's held item and returns it to the bag.
    /// </summary>
    [Command("items unequip")]
    [Summary("Unequip an item from your selected pokemon.")]
    public async Task Unequip() => await SendResultAsync(await Service.Unequip(ctx.User.Id));

    /// <summary>
    ///     Makes the selected Pokémon drop its held item, discarding it.
    /// </summary>
    [Command("items drop")]
    [Summary("Have your selected pokemon drop their held item.")]
    public async Task Drop() => await SendResultAsync(await Service.Drop(ctx.User.Id));

    /// <summary>
    ///     Moves the selected Pokémon's held item to another Pokémon.
    /// </summary>
    /// <param name="pokemonNumber">The number of the Pokémon to give it to.</param>
    [Command("items transfer")]
    [Summary("Transfer an item from your selected pokemon to another.")]
    public async Task Transfer(ulong pokemonNumber) =>
        await SendResultAsync(await Service.Transfer(ctx.User.Id, pokemonNumber));

    /// <summary>
    ///     Equips an item from the bag to the selected Pokémon.
    /// </summary>
    /// <param name="itemName">The item's name.</param>
    [Command("items equip")]
    [Alias("equip")]
    [Summary("Equip an item from your bag to your selected pokemon.")]
    public async Task Equip([Remainder] string itemName) =>
        await SendResultAsync(await Service.Equip(ctx.User.Id, itemName));

    /// <summary>
    ///     Uses an active item, such as an evolution stone, on the selected Pokémon.
    /// </summary>
    /// <param name="itemName">The item's name.</param>
    [Command("items apply")]
    [Alias("apply")]
    [Summary("Use an active item to evolve a poke.")]
    public async Task Apply([Remainder] string itemName) =>
        await SendResultAsync(await Service.Apply(ctx.User.Id, itemName));

    private async Task SendResultAsync(ItemsService.CommandResult result)
    {
        var prefix = await GetPrefixAsync();
        await SendAsync(result.Message.Replace("`/", $"`{prefix}"));
    }
}
