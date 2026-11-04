using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Spawn.Services;
using Serilog;

namespace EeveeCore.Modules.Spawn;

/// <summary>
///     Catching the Pokémon that spawn in a channel.
/// </summary>
[Name("Catch")]
public class CatchModule : EeveeModule<SpawnService>
{
    /// <summary>
    ///     Catches the active spawn in this channel when the name is right.
    /// </summary>
    /// <param name="name">The Pokémon's name.</param>
    [Command("catch")]
    [Summary("Catch the Pokémon that spawned in this channel.")]
    [RequireContext(ContextType.Guild)]
    public async Task Catch([Remainder] string name)
    {
        if (!await Service.TryCatchAsync(ctx.Message, name))
            await ErrorAsync("Incorrect name! Try again :(");
    }
}

/// <summary>
///     Spawn settings for the server and its channels.
/// </summary>
/// <param name="credentials">The bot credentials, for the owner list.</param>
[Group("spawn")]
[Name("Spawn")]
[RequireContext(ContextType.Guild)]
public class SpawnModule(BotCredentials credentials) : EeveeModule<SpawnService>
{
    /// <summary>
    ///     Whether to add or remove a redirect channel.
    /// </summary>
    public enum RedirectOption
    {
        /// <summary>Add a channel to the redirect list.</summary>
        Add,

        /// <summary>Remove a channel from the redirect list.</summary>
        Remove
    }

    /// <summary>
    ///     Toggles spawn storm mode, which spawns a Pokémon on every message.
    /// </summary>
    [Command("letsgo")]
    [Summary("Toggle spawn storm mode.")]
    public async Task ToggleSpawnStorm()
    {
        if (!credentials.OwnerIds.Contains(ctx.User.Id))
            return;

        if (Service.ToggleAlwaysSpawn())
            await ReplyAsync("**Code**: *Spawn storm* `INITIATED!`");
        else
            await ReplyAsync(
                $"It seems the **Spawn Storm** has ended. *You can blame {ctx.User.Id} for this crazyness*");
    }

    /// <summary>
    ///     Shows the spawn configuration of this server, for troubleshooting.
    /// </summary>
    [Command("debug")]
    [Summary("Debug spawn issues in this community.")]
    public async Task DebugSpawn()
    {
        if (!credentials.OwnerIds.Contains(ctx.User.Id))
            return;

        try
        {
            await ReplyAsync(await Service.GetSpawnDebugInfo(ctx.Guild!));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in debug spawn command");
            await ErrorAsync("An error occurred while debugging spawn settings. Please try again later.");
        }
    }

    /// <summary>
    ///     Shows this server's spawn settings.
    /// </summary>
    [Command("settings")]
    [Summary("View the spawn settings.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task SpawnSettings() => await SendEmbedAsync(() => Service.GetGuildSettings(ctx.Guild!.Id));

    /// <summary>
    ///     Changes one of this server's spawn settings.
    /// </summary>
    /// <param name="setting">delete, pin, small, modal or enableall.</param>
    /// <param name="value">true or false.</param>
    [Command("settings")]
    [Summary("Change a spawn setting: delete, pin, small, modal or enableall, to true or false.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task SpawnSettings(string setting, string value)
    {
        if (ParseToggle(value) is not { } enabled)
        {
            await ErrorAsync("The value must be true or false.");
            return;
        }

        await SendEmbedAsync(() =>
            Service.UpdateGuildSetting(ctx.Guild!.Id, setting.ToLower(), enabled ? "true" : "false"));
    }

    /// <summary>
    ///     Enables spawns in a channel.
    /// </summary>
    /// <param name="channel">The channel, or this one.</param>
    [Command("enable")]
    [Summary("Enable spawns in a channel.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task Enable(Channel? channel = null) => await SetChannelAsync(channel, true);

    /// <summary>
    ///     Disables spawns in a channel.
    /// </summary>
    /// <param name="channel">The channel, or this one.</param>
    [Command("disable")]
    [Summary("Disable spawns in a channel.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task Disable(Channel? channel = null) => await SetChannelAsync(channel, false);

    /// <summary>
    ///     Adds or removes a channel that spawns are sent to.
    /// </summary>
    /// <param name="option">add or remove.</param>
    /// <param name="channel">The channel, or this one.</param>
    [Command("redirect")]
    [Summary("Add or remove a channel that spawns are redirected to.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task Redirect(RedirectOption option, Channel? channel = null)
    {
        channel ??= ctx.Channel;
        if (channel.GuildId != ctx.Guild!.Id)
        {
            await ErrorAsync("That channel is not in this community.");
            return;
        }

        await SendEmbedAsync(() =>
            Service.UpdateRedirectChannel(ctx.Guild.Id, channel.Id, option == RedirectOption.Add));
    }

    /// <summary>
    ///     Adds a channel to the spawn redirects, or removes it when it is already one.
    /// </summary>
    /// <param name="channel">The channel to add or remove.</param>
    [Command("redirect")]
    [Summary("Add a channel that spawns are redirected to, or remove it when it already is one.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    [Priority(-1)]
    public async Task Redirect(Channel channel)
    {
        if (channel.GuildId != ctx.Guild!.Id)
        {
            await ErrorAsync("That channel is not in this community.");
            return;
        }

        var config = await Service.GetGuildConfig(ctx.Guild.Id);
        var add = config.Redirects?.Contains(channel.Id) != true;
        await SendEmbedAsync(() => Service.UpdateRedirectChannel(ctx.Guild.Id, channel.Id, add));
    }

    /// <summary>
    ///     Sets how often Pokémon spawn in this server.
    /// </summary>
    /// <param name="speed">From 1 to 20.</param>
    [Command("speed")]
    [Summary("Set the spawn speed, from 1 to 20.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task Speed(int speed = 10)
    {
        if (speed is < 1 or > 20)
        {
            await ErrorAsync("The speed must be between 1 and 20.");
            return;
        }

        await SendEmbedAsync(() => Service.UpdateSpawnSpeed(ctx.Guild!.Id, speed));
    }

    /// <summary>
    ///     Turns small spawn images on or off.
    /// </summary>
    /// <param name="value">on or off.</param>
    [Command("smallimages")]
    [Alias("small")]
    [Summary("Show spawns as small thumbnails, on or off.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task SmallImages(string value) => await SpawnSettings("small", value);

    /// <summary>
    ///     Turns modal view on or off. With it on, spawns are only caught with the catch command.
    /// </summary>
    /// <param name="value">on or off.</param>
    [Command("modalview")]
    [Alias("modal")]
    [Summary("Only catch spawns with the catch command, on or off.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task ModalView(string value) => await SpawnSettings("modal", value);

    private async Task SetChannelAsync(Channel? channel, bool enable)
    {
        channel ??= ctx.Channel;
        if (channel.GuildId != ctx.Guild!.Id)
        {
            await ErrorAsync("That channel is not in this community.");
            return;
        }

        await SendEmbedAsync(() => Service.UpdateChannelSetting(ctx.Guild.Id, channel.Id, enable));
    }

    private async Task SendEmbedAsync(Func<Task<EmbedRequest>> update)
    {
        try
        {
            await ReplyAsync(embeds: [await update()]);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error updating spawn settings in guild {GuildId}", ctx.Guild?.Id);
            await ErrorAsync("An error occurred while updating spawn settings. Please try again later.");
        }
    }

    private static bool? ParseToggle(string value) => value.ToLower() switch
    {
        "true" or "on" or "yes" or "enable" or "enabled" => true,
        "false" or "off" or "no" or "disable" or "disabled" => false,
        _ => null
    };
}
