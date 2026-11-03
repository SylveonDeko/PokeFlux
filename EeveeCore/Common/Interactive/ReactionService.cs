using Serilog;

namespace EeveeCore.Common.Interactive;

/// <summary>
///     One page of a paged message: its text, embeds, and the files its embeds show.
/// </summary>
/// <param name="Content">The message text.</param>
/// <param name="Embeds">The embeds.</param>
/// <param name="Attachments">Files referenced by the embeds with <c>attachment://</c>.</param>
public sealed record Page(string? Content, List<EmbedRequest> Embeds, List<AttachmentRequest>? Attachments = null);

/// <summary>
///     Reaction prompts on messages: yes or no confirmations, picking one of several emojis, and paging.
/// </summary>
/// <remarks>
///     A reaction counts whether it is added or removed, so a player can press the same reaction twice even when the
///     bot cannot remove reactions in the channel.
/// </remarks>
public sealed class ReactionService(FluxerClient client) : INService
{
    /// <summary>
    ///     The reaction that confirms a prompt.
    /// </summary>
    public const string Yes = "✅";

    /// <summary>
    ///     The reaction that declines a prompt.
    /// </summary>
    public const string No = "❌";

    private const string First = "⏮️";
    private const string Back = "◀️";
    private const string Next = "▶️";
    private const string Last = "⏭️";
    private const string Stop = "⏹️";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Adds ✅ and ❌ to a message and waits for a user to pick one.
    /// </summary>
    /// <param name="message">The message to react on.</param>
    /// <param name="userId">The user whose answer counts.</param>
    /// <param name="timeout">How long to wait. Defaults to two minutes.</param>
    /// <returns>True for ✅, false for ❌, or null when the user did not answer in time.</returns>
    public async Task<bool?> ConfirmAsync(Message message, ulong userId, TimeSpan? timeout = null)
    {
        var choice = await ChooseAsync(message, userId, [Yes, No], timeout);
        return choice is null ? null : choice == 0;
    }

    /// <summary>
    ///     Adds emojis to a message and waits for a user to pick one.
    /// </summary>
    /// <param name="message">The message to react on.</param>
    /// <param name="userId">The user whose pick counts.</param>
    /// <param name="emojis">The unicode emojis to offer, in order.</param>
    /// <param name="timeout">How long to wait. Defaults to two minutes.</param>
    /// <param name="clear">Whether to remove the reactions afterwards.</param>
    /// <returns>The index of the picked emoji, or null when the user did not pick in time.</returns>
    public async Task<int?> ChooseAsync(Message message, ulong userId, IReadOnlyList<string> emojis,
        TimeSpan? timeout = null, bool clear = true)
    {
        var adding = AddReactionsAsync(message, emojis);
        var picked = await WaitForPressAsync(message.Id, userId, emojis, timeout ?? DefaultTimeout);
        await adding;
        if (clear)
            await TryClearReactionsAsync(message);
        return picked is null ? null : emojis.ToList().IndexOf(picked);
    }

    /// <summary>
    ///     Sends the first page and lets a user page through with reactions until they stop or stop reacting.
    /// </summary>
    /// <param name="channel">The channel to send to.</param>
    /// <param name="userId">The user who can turn the pages.</param>
    /// <param name="pageCount">The number of pages.</param>
    /// <param name="render">Builds the page at an index.</param>
    /// <param name="timeout">How long to wait for each press. Defaults to two minutes.</param>
    /// <param name="startPage">The page to show first.</param>
    public async Task PaginateAsync(Channel channel, ulong userId, int pageCount, Func<int, Task<Page>> render,
        TimeSpan? timeout = null, int startPage = 0)
    {
        var index = Math.Clamp(startPage, 0, Math.Max(0, pageCount - 1));
        var page = await render(index);
        var message = await client.Rest.SendMessageAsync(channel.Id, page.Content, page.Embeds,
            attachments: page.Attachments);
        if (pageCount <= 1)
            return;

        string[] controls = pageCount > 2 ? [First, Back, Next, Last, Stop] : [Back, Next, Stop];
        var adding = AddReactionsAsync(message, controls);

        while (await WaitForPressAsync(message.Id, userId, controls, timeout ?? DefaultTimeout) is { } pressed)
        {
            if (pressed == Stop)
                break;

            var next = pressed switch
            {
                First => 0,
                Back => index == 0 ? pageCount - 1 : index - 1,
                Next => index == pageCount - 1 ? 0 : index + 1,
                Last => pageCount - 1,
                _ => index
            };
            if (next == index)
                continue;

            index = next;
            page = await render(index);
            try
            {
                await message.ModifyAsync(page.Content ?? "", page.Embeds, attachments: page.Attachments ?? []);
            }
            catch (FluxerApiException ex)
            {
                Log.Debug(ex, "Could not turn the page of message {MessageId}", message.Id);
                break;
            }
        }

        await adding;
        await TryClearReactionsAsync(message);
    }

    /// <summary>
    ///     Waits for a user to press, or un-press, one of the given reactions on a message.
    /// </summary>
    /// <param name="messageId">The message.</param>
    /// <param name="userId">The user whose press counts.</param>
    /// <param name="emojis">The unicode emojis that count.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The pressed emoji, or null when the time ran out or the wait was cancelled.</returns>
    public async Task<string?> WaitForPressAsync(ulong messageId, ulong userId, IReadOnlyList<string> emojis,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool Matches(SocketReaction reaction) =>
            reaction.MessageId == messageId && reaction.UserId == userId && emojis.Contains(reaction.Emoji.Name);

        var added = client.Gateway.MessageReactionAdded.WaitAsync((_, _, r) => Matches(r), timeout, cancel.Token);
        var removed = client.Gateway.MessageReactionRemoved.WaitAsync((_, _, r) => Matches(r), timeout, cancel.Token);
        var first = await Task.WhenAny(added, removed);
        await cancel.CancelAsync();

        try
        {
            return (await first)?.Item3.Emoji.Name;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Adds reactions to a message in order, stopping at the first one that fails.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="emojis">The unicode emojis to add.</param>
    public static async Task AddReactionsAsync(Message message, IEnumerable<string> emojis)
    {
        foreach (var emoji in emojis)
        {
            try
            {
                await message.AddReactionAsync(emoji);
            }
            catch (FluxerApiException ex)
            {
                Log.Debug(ex, "Could not add reaction {Emoji} to message {MessageId}", emoji, message.Id);
                return;
            }
        }
    }

    /// <summary>
    ///     Removes all reactions from a message, when the bot is allowed to.
    /// </summary>
    /// <param name="message">The message.</param>
    public static async Task TryClearReactionsAsync(Message message)
    {
        try
        {
            await message.RemoveAllReactionsAsync();
        }
        catch (FluxerApiException)
        {
        }
    }
}
