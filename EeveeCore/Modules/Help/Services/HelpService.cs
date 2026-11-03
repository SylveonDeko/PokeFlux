using Serilog;

namespace EeveeCore.Modules.Help.Services;

/// <summary>
///     Builds help for the text commands, answers a bare mention of the bot, and greets new servers.
/// </summary>
public class HelpService : INService
{
    private readonly FluxerClient client;
    private readonly CommandService commands;
    private readonly GuildSettingsService guildSettings;

    /// <summary>
    ///     Initializes a new instance of the <see cref="HelpService" /> class.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="commands">The text command service.</param>
    /// <param name="guildSettings">The guild settings, for prefixes.</param>
    public HelpService(FluxerClient client, CommandService commands, GuildSettingsService guildSettings)
    {
        this.client = client;
        this.commands = commands;
        this.guildSettings = guildSettings;
        client.Gateway.MessageCreated += HandlePing;
        client.Gateway.GuildJoined += HandleJoin;
    }

    /// <summary>
    ///     Gets the top-level command modules, in name order.
    /// </summary>
    public IEnumerable<ModuleInfo> Categories =>
        commands.Modules.Where(m => m.Parent is null && m.Commands.Count + m.Submodules.Count > 0)
            .OrderBy(m => m.Name);

    /// <summary>
    ///     Finds a category by name, ignoring case.
    /// </summary>
    /// <param name="name">The category name.</param>
    /// <returns>The category, or null when there is none by that name.</returns>
    public ModuleInfo? FindCategory(string name) =>
        Categories.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Finds commands by name or alias, including group prefixes such as <c>spawn channel</c>.
    /// </summary>
    /// <param name="name">The command name.</param>
    /// <returns>The matching commands.</returns>
    public IReadOnlyList<CommandInfo> FindCommands(string name)
    {
        var result = commands.Search(name.Trim());
        return result.IsSuccess ? result.Commands.Select(c => c.Command).Distinct().ToList() : [];
    }

    /// <summary>
    ///     Builds the overview: every category and how to dig deeper.
    /// </summary>
    /// <param name="prefix">The prefix in the server.</param>
    public EmbedBuilder GetHelpEmbed(string prefix)
    {
        var embed = new EmbedBuilder()
            .WithAuthor("PokeFlux Help", client.Gateway.CurrentUser?.GetAvatarUrl())
            .WithDescription($"Use `{prefix}help <category>` to see a category's commands, " +
                             $"or `{prefix}help <command>` for how to use one.")
            .WithOkColor();

        foreach (var category in Categories)
            embed.AddField(category.Name, $"{AllCommands(category).Count()} commands", true);

        return embed;
    }

    /// <summary>
    ///     Builds the list of a category's commands.
    /// </summary>
    /// <param name="category">The category.</param>
    /// <param name="prefix">The prefix in the server.</param>
    public EmbedBuilder GetCategoryEmbed(ModuleInfo category, string prefix)
    {
        var lines = AllCommands(category)
            .GroupBy(c => c.Aliases[0])
            .Select(g => $"`{prefix}{g.Key}`" + (g.First().Summary is { Length: > 0 } s ? $" {s}" : ""));
        var description = string.Join("\n", lines);

        return new EmbedBuilder()
            .WithTitle(category.Name)
            .WithDescription(description.Length > 4000 ? description[..4000] : description)
            .WithFooter($"{prefix}help <command> shows how to use a command")
            .WithOkColor();
    }

    /// <summary>
    ///     Builds the help for a command and its overloads.
    /// </summary>
    /// <param name="overloads">The command's overloads.</param>
    /// <param name="prefix">The prefix in the server.</param>
    public EmbedBuilder GetCommandHelp(IReadOnlyList<CommandInfo> overloads, string prefix)
    {
        var command = overloads[0];
        var embed = new EmbedBuilder()
            .WithTitle($"{prefix}{command.Aliases[0]}")
            .WithDescription(command.Summary is { Length: > 0 } summary ? summary : "No description.")
            .AddField("Usage", string.Join("\n", overloads.Select(o => $"`{Usage(o, prefix)}`")))
            .WithOkColor();

        if (command.Aliases.Count > 1)
            embed.AddField("Aliases", string.Join(", ", command.Aliases.Skip(1).Select(a => $"`{a}`")));
        if (command.Remarks is { Length: > 0 } remarks)
            embed.AddField("Notes", remarks);

        return embed;
    }

    /// <summary>
    ///     Writes a command's usage, with required arguments in angle brackets and optional ones in square brackets.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="prefix">The prefix in the server.</param>
    public static string Usage(CommandInfo command, string prefix) =>
        $"{prefix}{command.Aliases[0]} " + string.Join(" ", command.Parameters.Select(p =>
            p.IsOptional ? $"[{p.Name}]" : $"<{p.Name}>")).TrimEnd();

    private static IEnumerable<CommandInfo> AllCommands(ModuleInfo module) =>
        module.Commands.Concat(module.Submodules.SelectMany(AllCommands));

    private async Task HandlePing(SocketMessage msg)
    {
        if (msg.Author is not { IsBot: false } || client.Gateway.CurrentUser is not { } bot)
            return;
        if (msg.Content.Trim() != $"<@{bot.Id}>" && msg.Content.Trim() != $"<@!{bot.Id}>")
            return;

        var prefix = await guildSettings.GetPrefix(msg.Guild);
        var eb = new EmbedBuilder()
            .WithOkColor()
            .WithDescription($"Hi there! My prefix here is `{prefix}`. Use `{prefix}help` to see what I can do.")
            .WithFooter(bot.Username, bot.GetAvatarUrl());
        await msg.Channel.SendMessageAsync(embeds: [eb.Build()]).ConfigureAwait(false);
    }

    private async Task HandleJoin(SocketGuild guild)
    {
        var channel = guild.SystemChannel
                      ?? guild.Channels.Values.Where(c => c.Type == ChannelType.GuildText)
                          .OrderBy(c => c.Position).FirstOrDefault();
        if (channel is null)
            return;

        var px = await guildSettings.GetPrefix(guild);
        var eb = new EmbedBuilder()
            .WithDescription(
                $"Hi, thanks for inviting PokeFlux! The prefix is `{px}`, and it can be changed with `{px}prefix`.")
            .AddField("Getting started",
                $"1) `{px}start` to pick your starter\n2) `{px}help` to see every category\n" +
                $"3) `{px}help <command>` for how to use a command, for example `{px}help spawn`")
            .AddField("Spawns",
                $"Spawns are off by default. Use `{px}spawn enable` in a channel to turn them on there.")
            .WithOkColor();

        try
        {
            await channel.SendMessageAsync(embeds: [eb.Build()]).ConfigureAwait(false);
        }
        catch (FluxerApiException ex)
        {
            Log.Debug(ex, "Could not greet server {GuildId}", guild.Id);
        }
    }
}
