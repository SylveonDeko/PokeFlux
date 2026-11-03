using Serilog;

namespace EeveeCore.Services;

/// <summary>
///     Posts bot events to the log channel set by <see cref="BotCredentials.LogChannelId" />, and to the console log.
/// </summary>
public sealed class BotLog(FluxerClient client, BotCredentials credentials) : INService
{
    /// <summary>
    ///     Logs a message, posting it to the log channel when one is set.
    /// </summary>
    /// <param name="content">The message text.</param>
    /// <param name="embed">An embed to post with it.</param>
    public Task SendAsync(string? content, EmbedBuilder? embed = null) =>
        SendToAsync(credentials.LogChannelId, content, embed);

    /// <summary>
    ///     Logs a new player's registration, in the start log channel when one is set.
    /// </summary>
    /// <param name="content">The message text.</param>
    public Task SendStartAsync(string content) =>
        SendToAsync(credentials.StartLogChannelId is not 0 ? credentials.StartLogChannelId : credentials.LogChannelId,
            content, null);

    private async Task SendToAsync(ulong channelId, string? content, EmbedBuilder? embed)
    {
        Log.Information("[BotLog] {Content}", content ?? embed?.Build().Title);
        if (channelId == 0)
            return;

        try
        {
            await client.Rest.SendMessageAsync(channelId, content, embed is null ? null : [embed.Build()]);
        }
        catch (FluxerApiException ex)
        {
            Log.Warning(ex, "Could not post to the log channel");
        }
    }
}
