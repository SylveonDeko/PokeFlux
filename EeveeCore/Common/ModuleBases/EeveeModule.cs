using EeveeCore.Common.Interactive;

namespace EeveeCore.Common.ModuleBases;

/// <summary>
///     Base class for EeveeCore's text command modules.
/// </summary>
public abstract class EeveeModule : ModuleBase<CommandContext>
{
    /// <summary>
    ///     Gets or sets the reaction prompts and paging.
    /// </summary>
    public ReactionService Reactions { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the guild settings, for the prefix.
    /// </summary>
    public GuildSettingsService GuildSettings { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the hosted image links.
    /// </summary>
    public MediaUrls Media { get; set; } = null!;

    /// <summary>
    ///     Gets the command context.
    /// </summary>
    protected CommandContext ctx => Context;

    /// <summary>
    ///     Gets the prefix commands use in this server.
    /// </summary>
    protected Task<string> GetPrefixAsync() => GuildSettings.GetPrefix(Context.Guild);

    /// <summary>
    ///     Shows an image from <c>data/</c> in an embed, by its hosted link or else as an attached file.
    /// </summary>
    /// <param name="embed">The embed.</param>
    /// <param name="path">The image's path.</param>
    /// <param name="fileName">The file name to attach it as when it is not hosted.</param>
    /// <param name="attachments">The message's attachments, which an attached file is added to.</param>
    /// <param name="thumbnail">Whether to show it as the thumbnail instead of the main image.</param>
    /// <returns>Whether the image exists.</returns>
    protected bool SetImage(EmbedBuilder embed, string? path, string fileName, List<AttachmentRequest> attachments,
        bool thumbnail = false)
    {
        var url = Media.Get(path);
        if (url is null)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;
            attachments.Add(new AttachmentRequest(File.OpenRead(path), fileName));
            url = $"attachment://{fileName}";
        }

        if (thumbnail)
            embed.WithThumbnailUrl(url);
        else
            embed.WithImageUrl(url);
        return true;
    }

    /// <summary>
    ///     Sends a message in the command's channel.
    /// </summary>
    /// <param name="content">The message text.</param>
    /// <param name="embed">An embed to send.</param>
    /// <param name="attachments">Files to send, including ones the embed shows.</param>
    protected Task<Message> SendAsync(string? content = null, EmbedBuilder? embed = null,
        List<AttachmentRequest>? attachments = null) =>
        ReplyAsync(content, embed is null ? null : [embed.Build()], attachments: attachments);

    /// <summary>
    ///     Sends an embed with the OK color and the given text.
    /// </summary>
    /// <param name="text">The text of the embed.</param>
    protected Task<Message> ConfirmAsync(string text) =>
        SendAsync(embed: new EmbedBuilder().WithOkColor().WithDescription(text));

    /// <summary>
    ///     Sends an embed with the error color and the given text.
    /// </summary>
    /// <param name="text">The text of the embed.</param>
    protected Task<Message> ErrorAsync(string text) =>
        SendAsync(embed: new EmbedBuilder().WithErrorColor().WithDescription(text));

    /// <summary>
    ///     Sends a direct message to the command's user, for things only they should see.
    /// </summary>
    /// <param name="content">The message text.</param>
    /// <param name="embed">An embed to send.</param>
    /// <returns>Whether the message was delivered. It fails when the user does not accept direct messages.</returns>
    protected async Task<bool> SendPrivateAsync(string? content = null, EmbedBuilder? embed = null)
    {
        try
        {
            await Context.Client.SendDirectMessageAsync(Context.User.Id, content,
                embed is null ? null : [embed.Build()]);
            return true;
        }
        catch (FluxerApiException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Asks the command's user to confirm with ✅ or ❌, then deletes the question.
    /// </summary>
    /// <param name="text">The question.</param>
    /// <returns>True when they confirmed, false when they declined or did not answer in time.</returns>
    protected Task<bool> PromptConfirmAsync(string text) =>
        PromptConfirmAsync(new EmbedBuilder().WithOkColor().WithDescription(text));

    /// <summary>
    ///     Asks the command's user to confirm with ✅ or ❌, then deletes the question.
    /// </summary>
    /// <param name="embed">The question.</param>
    /// <returns>True when they confirmed, false when they declined or did not answer in time.</returns>
    protected async Task<bool> PromptConfirmAsync(EmbedBuilder embed)
    {
        var message = await SendAsync(embed: embed);
        var answer = await Reactions.ConfirmAsync(message, Context.User.Id);
        message.DeleteAfter(0);
        return answer == true;
    }
}

/// <summary>
///     Base class for EeveeCore's text command modules that use a service.
/// </summary>
/// <typeparam name="TService">The module's service.</typeparam>
public abstract class EeveeModule<TService> : EeveeModule
{
    /// <summary>
    ///     Gets or sets the module's service.
    /// </summary>
    public TService Service { get; set; } = default!;
}
