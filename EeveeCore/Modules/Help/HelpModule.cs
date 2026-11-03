using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Help.Services;

namespace EeveeCore.Modules.Help;

/// <summary>
///     Shows the command categories, a category's commands, or how to use a command.
/// </summary>
[Name("Help")]
public class HelpModule : EeveeModule<HelpService>
{
    /// <summary>
    ///     Shows every category.
    /// </summary>
    [Command("help")]
    [Alias("h", "commands", "cmds")]
    [Summary("See every command category.")]
    public async Task Help() => await SendAsync(embed: Service.GetHelpEmbed(await GetPrefixAsync()));

    /// <summary>
    ///     Shows a category's commands, or how to use a command.
    /// </summary>
    /// <param name="name">A category or command name.</param>
    [Command("help")]
    [Alias("h", "commands", "cmds")]
    [Summary("See a category's commands, or how to use a command.")]
    public async Task Help([Remainder] string name)
    {
        var prefix = await GetPrefixAsync();
        if (Service.FindCategory(name) is { } category)
        {
            await SendAsync(embed: Service.GetCategoryEmbed(category, prefix));
            return;
        }

        var commands = Service.FindCommands(name);
        if (commands.Count == 0)
        {
            await ErrorAsync($"There is no category or command called `{name}`. Use `{prefix}help` to see them all.");
            return;
        }

        await SendAsync(embed: Service.GetCommandHelp(commands, prefix));
    }
}
