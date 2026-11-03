using EeveeCore.Common.ModuleBases;

namespace EeveeCore.Modules.Server;

/// <summary>
///     Server settings for the bot itself.
/// </summary>
[Name("Server")]
public class ServerModule : EeveeModule
{
    /// <summary>
    ///     Shows the prefix in this server.
    /// </summary>
    [Command("prefix")]
    [Summary("See the prefix in this server.")]
    public async Task Prefix() => await ConfirmAsync($"The prefix here is `{await GetPrefixAsync()}`.");

    /// <summary>
    ///     Changes the prefix in this server.
    /// </summary>
    /// <param name="prefix">The new prefix.</param>
    [Command("prefix")]
    [Summary("Change the prefix in this server.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task Prefix(string prefix)
    {
        if (prefix.Length > 10)
        {
            await ErrorAsync("The prefix can be at most 10 characters.");
            return;
        }

        await GuildSettings.SetPrefix(ctx.Guild!, prefix);
        await ConfirmAsync($"The prefix here is now `{prefix}`.");
    }
}
