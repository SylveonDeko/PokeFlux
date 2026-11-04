using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Start.Services;

namespace EeveeCore.Modules.Start;

/// <summary>
///     The entry point for new players: picking a starter and registering.
/// </summary>
/// <param name="botLog">The bot event log.</param>
[Name("Start")]
public class StartModule(BotLog botLog) : EeveeModule<StartService>
{
    /// <summary>
    ///     Shows the starters to pick from.
    /// </summary>
    [Command("start")]
    [Summary("Start your journey and pick a starter.")]
    public async Task Start()
    {
        var prefix = await GetPrefixAsync();
        var embed = new EmbedBuilder()
            .WithTitle("Pick your starter!")
            .WithDescription($"Use `{prefix}start <name>` to pick one, for example `{prefix}start Bulbasaur`.")
            .AddField("🍃 Grass", string.Join(", ", StartService.Starters["grass"]))
            .AddField("💧 Water", string.Join(", ", StartService.Starters["water"]))
            .AddField("🔥 Fire", string.Join(", ", StartService.Starters["fire"]))
            .WithColor(new Color(0xDD, 0x00, 0xDD));

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Registers the player and gives them the starter they picked.
    /// </summary>
    /// <param name="starter">The starter's name.</param>
    [Command("start")]
    [Summary("Start your journey with the starter you name.")]
    public async Task Start([Remainder] string starter)
    {
        var picked = StartService.FindStarter(starter);
        if (picked is null)
        {
            await ErrorAsync($"{starter} is not a starter. Use `{await GetPrefixAsync()}start` to see them.");
            return;
        }

        var registration = await Service.RegisterNewUser(ctx.User.Id);
        if (!registration.Success)
        {
            await ErrorAsync(registration.Message);
            return;
        }

        var created = await Service.CreateStarterPokemon(ctx.User.Id, picked);
        if (!created.Success)
        {
            await ErrorAsync(created.Message);
            return;
        }

        var prefix = await GetPrefixAsync();
        var welcome = new EmbedBuilder()
            .WithTitle("Welcome to PokeFlux")
            .WithDescription($"You picked **{picked}**!\n" +
                             $"See your Pokémon with `{prefix}pokemon`\n" +
                             $"Select your starter with `{prefix}select 1`\n" +
                             "**Now begin your adventure!**\n\n" +
                             "**Spawns are off by default.** If you are setting the bot up in a new community," +
                             $"use `{prefix}spawn enable` in each channel you want Pokémon to spawn in.")
            .AddField("How to get Redeems and Credits",
                "Get 1 Redeem and 15,000 Credits for 5 Upvote Points!\n" +
                "Check out fishing for a new user bonus, and fish up mystery boxes for a battle multiplier!")
            .WithColor(new Color(0xDD, 0x00, 0xDD));

        await SendAsync(embed: welcome);
        await botLog.SendStartAsync($"{ctx.User.Username} (`{ctx.User.Id}`) has started **PokeFlux** (**{picked}**)");
    }
}
