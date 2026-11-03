using EeveeCore.Common.Interactive;
using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Parties.Services;

namespace EeveeCore.Modules.Parties;

/// <summary>
///     Commands for managing your eggs.
/// </summary>
[Group("hatchery")]
[Name("Hatchery")]
public class HatcheryModule : EeveeModule<HatcheryService>
{
    private const string SkipSlot = "-";

    /// <summary>
    ///     Shows the hatchery groups, one per page.
    /// </summary>
    /// <param name="group">The group to show first, 1 to 3.</param>
    [Command]
    [Alias("view")]
    [Summary("View your egg hatchery groups.")]
    public async Task ViewHatchery(int group = 1)
    {
        if (group is < 1 or > 3)
        {
            await ErrorAsync("Invalid group number.");
            return;
        }

        var pages = new List<EmbedBuilder>();
        for (short g = 1; g <= 3; g++)
            pages.Add(await Service.GetHatcheryViewEmbed(ctx.User.Id, g) ??
                      new EmbedBuilder().WithErrorColor()
                          .WithDescription($"You have no eggs in your hatchery group {g}."));

        await Reactions.PaginateAsync(ctx.Channel, ctx.User.Id, pages.Count,
            i => Task.FromResult(new Page(null, [pages[i].Build()])), startPage: group - 1);
    }

    /// <summary>
    ///     Puts eggs in the slots of a hatchery group, in slot order.
    /// </summary>
    /// <param name="group">The group, 1 to 3.</param>
    /// <param name="eggs">Egg numbers for slots 1 to 10, or - to skip a slot.</param>
    [Command("set")]
    [Summary("Add multiple eggs to your hatchery group. Use - to skip a slot, for example `1 120 - 135`.")]
    public async Task SetEggs(int group, params string[] eggs)
    {
        if (group is < 1 or > 3)
        {
            await ErrorAsync("Invalid group number.");
            return;
        }

        if (eggs.Length is 0 or > 10)
        {
            await ErrorAsync(
                $"Give one to ten egg numbers, one per slot, for example `{await GetPrefixAsync()}hatchery set 1 120 - 135`. Use `-` to skip a slot.");
            return;
        }

        var slots = new int?[10];
        for (var i = 0; i < eggs.Length; i++)
        {
            if (eggs[i] == SkipSlot)
                continue;

            if (!int.TryParse(eggs[i], out var egg) || egg < 1)
            {
                await ErrorAsync($"`{eggs[i]}` is not an egg number. Use `-` to skip a slot.");
                return;
            }

            slots[i] = egg;
        }

        var addedEggs = await Service.SetMultipleEggs(ctx.User.Id, (short)group, slots[0], slots[1], slots[2],
            slots[3], slots[4], slots[5], slots[6], slots[7], slots[8], slots[9]);

        if (addedEggs.Count == 0)
        {
            await ConfirmAsync("No eggs were added to the hatchery.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("Eggs Added to Hatchery")
            .WithOkColor();

        foreach (var groupEntry in addedEggs)
            embed.AddField($"Group {groupEntry.Key}", $"Eggs added to slots: {string.Join(", ", groupEntry.Value)}");

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Takes an egg out of a hatchery slot.
    /// </summary>
    /// <param name="group">The group, 1 to 3.</param>
    /// <param name="slot">The slot, 1 to 10.</param>
    [Command("remove")]
    [Summary("Remove an egg from your hatchery.")]
    public async Task RemoveEgg(int group, int slot)
    {
        if (group is < 1 or > 3)
        {
            await ErrorAsync("Invalid group number.");
            return;
        }

        if (slot is < 1 or > 10)
        {
            await ErrorAsync("Slot must be between 1 and 10!");
            return;
        }

        var result = await Service.RemoveEggFromHatchery(ctx.User.Id, group, slot);
        if (result.Success)
            await ConfirmAsync(result.Message);
        else
            await ErrorAsync(result.Message);
    }

    /// <summary>
    ///     Swaps the places of two eggs in the hatchery.
    /// </summary>
    /// <param name="egg1">The first egg's number.</param>
    /// <param name="egg2">The second egg's number.</param>
    [Command("swap")]
    [Summary("Swap two eggs in your hatchery.")]
    public async Task SwapEggs(ulong egg1, ulong egg2)
    {
        var result = await Service.SwapEggs(ctx.User.Id, egg1, egg2);
        if (result.Success)
            await ConfirmAsync(result.Message);
        else
            await ErrorAsync(result.Message);
    }
}
