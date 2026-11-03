using System.Net.Http.Headers;

namespace EeveeCore.Extensions;

/// <summary>
///     General extension methods for messages, channels and embeds.
/// </summary>
public static class Extensions
{
    /// <summary>
    ///     Sends an embed with the OK color and the given description.
    /// </summary>
    /// <param name="channel">The channel to send to.</param>
    /// <param name="message">The description of the embed.</param>
    public static Task<Message> SendConfirmAsync(this Channel channel, string? message) =>
        channel.SendMessageAsync(embeds: [new EmbedBuilder().WithOkColor().WithDescription(message).Build()]);

    /// <summary>
    ///     Sends an embed with the error color and the given description.
    /// </summary>
    /// <param name="channel">The channel to send to.</param>
    /// <param name="message">The description of the embed.</param>
    public static Task<Message> SendErrorAsync(this Channel channel, string? message) =>
        channel.SendMessageAsync(embeds: [new EmbedBuilder().WithErrorColor().WithDescription(message).Build()]);

    /// <summary>
    ///     Sends a direct message to a user.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="userId">The user to message.</param>
    /// <param name="content">The message text.</param>
    /// <param name="embeds">The embeds to send.</param>
    public static async Task<Message> SendDirectMessageAsync(this FluxerClient client, ulong userId,
        string? content = null, List<EmbedRequest>? embeds = null)
    {
        var channel = await client.Rest.CreateDMChannelAsync(new CreatePrivateChannelRequest
        {
            RecipientId = userId
        }).ConfigureAwait(false);
        return await client.Rest.SendMessageAsync(channel.Id, content, embeds).ConfigureAwait(false);
    }

    /// <summary>
    ///     Gets a user's username from the gateway cache, or from the API when it is not cached.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="userId">The user's ID.</param>
    /// <returns>The username, or null when the user cannot be found.</returns>
    public static async Task<string?> GetUsernameAsync(this FluxerClient client, ulong userId)
    {
        if (await client.Gateway.Cache.Users.GetAsync(userId).ConfigureAwait(false) is { } cached)
            return cached.Username;

        try
        {
            return (await client.Rest.GetUserAsync(userId).ConfigureAwait(false))?.Username;
        }
        catch (FluxerApiException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Sets the color of an embed.
    /// </summary>
    /// <param name="eb">The embed builder.</param>
    /// <param name="color">The color to set.</param>
    public static EmbedBuilder WithColor(this EmbedBuilder eb, Color color) => eb.WithColor((int)color.RawValue);

    /// <summary>
    ///     Sets the color of an embed from a 0xRRGGBB value.
    /// </summary>
    /// <param name="eb">The embed builder.</param>
    /// <param name="color">The color to set.</param>
    public static EmbedBuilder WithColor(this EmbedBuilder eb, uint color) => eb.WithColor((int)color);

    /// <summary>
    ///     Sets the color of an embed to the OK color.
    /// </summary>
    /// <param name="eb">The embed builder.</param>
    public static EmbedBuilder WithOkColor(this EmbedBuilder eb) => eb.WithColor(EeveeCore.OkColor);

    /// <summary>
    ///     Sets the color of an embed to the error color.
    /// </summary>
    /// <param name="eb">The embed builder.</param>
    public static EmbedBuilder WithErrorColor(this EmbedBuilder eb) => eb.WithColor(EeveeCore.ErrorColor);

    /// <summary>
    ///     Adds browser-like headers to a request.
    /// </summary>
    /// <param name="dict">The headers to replace.</param>
    public static void AddFakeHeaders(this HttpHeaders dict)
    {
        dict.Clear();
        dict.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        dict.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/535.1 (KHTML, like Gecko) Chrome/14.0.835.202 Safari/535.1");
    }

    /// <summary>
    ///     Deletes a message after a number of seconds, ignoring failures.
    /// </summary>
    /// <param name="msg">The message to delete.</param>
    /// <param name="seconds">How many seconds to wait first.</param>
    public static void DeleteAfter(this Message? msg, int seconds)
    {
        if (msg is null) return;

        Task.Run(async () =>
        {
            await Task.Delay(seconds * 1000).ConfigureAwait(false);
            try
            {
                await msg.DeleteAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        });
    }

    /// <summary>
    ///     Gets the top-level module of a module.
    /// </summary>
    /// <param name="module">The module.</param>
    public static ModuleInfo GetTopLevelModule(this ModuleInfo module)
    {
        while (module.Parent != null) module = module.Parent;
        return module;
    }
}
