using System.Diagnostics;
using EeveeCore.Common.Collections;
using EeveeCore.Common.ModuleBehaviors;
using Serilog;
using IResult = Fluxer.Net.Commands.IResult;

namespace EeveeCore.Services;

/// <summary>
///     Reads server messages for the server's prefix or a mention of the bot, and runs the text command they name.
/// </summary>
public class CommandHandler : INService
{
    private const int GlobalCommandsCooldown = 750;

    private readonly FluxerClient _client;
    private readonly CommandService _commands;
    private readonly GuildSettingsService _guildSettings;
    private readonly ConcurrentHashSet<ulong> _usersOnShortCooldown = [];
    private readonly Timer _clearUsersOnShortCooldown;

    /// <summary>
    ///     Gets the service provider commands are run with.
    /// </summary>
    public readonly IServiceProvider Services;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CommandHandler" /> class.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="commands">The text command service.</param>
    /// <param name="guildSettings">The guild settings service, for prefixes.</param>
    /// <param name="services">The service provider commands are run with.</param>
    public CommandHandler(FluxerClient client, CommandService commands, GuildSettingsService guildSettings,
        IServiceProvider services)
    {
        _client = client;
        _commands = commands;
        _guildSettings = guildSettings;
        Services = services;

        _clearUsersOnShortCooldown = new Timer(_ => _usersOnShortCooldown.Clear(),
            null, GlobalCommandsCooldown, GlobalCommandsCooldown);

        _client.Gateway.MessageCreated += HandleMessageAsync;
        _commands.CommandExecuted += OnCommandFinishedAsync;
    }

    private static async Task OnCommandFinishedAsync(CommandInfo? command, ICommandContext context, IResult result)
    {
        if (result is not ExecuteResult { Exception: { } exception }
            || exception is FluxerApiException { ErrorCode: FluxerErrorCodes.MissingPermissions })
            return;

        Log.Warning(exception, "Command {Command} failed", command?.Name);
        if (context is not CommandContext { Channel: { } channel })
            return;

        try
        {
            await channel.SendErrorAsync("Something went wrong running that command. Please try again.");
        }
        catch (FluxerApiException)
        {
        }
    }

    /// <summary>
    ///     Raised after a command runs successfully.
    /// </summary>
    public event Func<SocketMessage, CommandInfo, Task> CommandExecuted = delegate { return Task.CompletedTask; };

    /// <summary>
    ///     Raised when a command fails, with the reason.
    /// </summary>
    public event Func<CommandInfo?, Channel, string, User?, Task> CommandErrored = delegate
    {
        return Task.CompletedTask;
    };

    /// <summary>
    ///     Raised for messages that do not start with the prefix or a mention of the bot.
    /// </summary>
    public event Func<SocketMessage, Task> OnMessageNoTrigger = delegate { return Task.CompletedTask; };

    private async Task HandleMessageAsync(SocketMessage msg)
    {
        if (msg.Author is null || msg.Author.IsBot || msg.WebhookId is not null || msg.Guild is null)
            return;

        try
        {
            await TryRunCommandAsync(msg.Guild, msg);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error in CommandHandler");
        }
    }

    private async Task TryRunCommandAsync(SocketGuild guild, SocketMessage msg)
    {
        foreach (var behavior in Services.GetServices<IEarlyBehavior>().OrderBy(x => x.Priority))
        {
            if (await behavior.RunBehavior(_client, guild, msg).ConfigureAwait(false))
                return;
        }

        var content = msg.Content ?? "";
        foreach (var transformer in Services.GetServices<IInputTransformer>())
        {
            var transformed = await transformer.TransformInput(guild, msg.Channel, msg.Author!, content)
                .ConfigureAwait(false);
            if (transformed == content) continue;
            content = transformed;
            break;
        }

        var prefix = await _guildSettings.GetPrefix(guild);
        var prefixLength = GetPrefixLength(content, prefix);
        if (prefixLength == 0)
        {
            await OnMessageNoTrigger(msg);
            foreach (var executor in Services.GetServices<ILateExecutor>())
                await executor.LateExecute(_client, guild, msg).ConfigureAwait(false);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var (success, error, command) =
            await ExecuteCommandAsync(new CommandContext(_client, msg), content, prefixLength);
        var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (success)
        {
            LogCommand(msg, command, true, elapsed);
            await CommandExecuted(msg, command!);
            return;
        }

        if (string.IsNullOrEmpty(error))
            return;

        LogCommand(msg, command, false, elapsed, error);
        await CommandErrored(command, msg.Channel, error, msg.Author);
    }

    private async Task<(bool Success, string? Error, CommandInfo? Info)> ExecuteCommandAsync(
        CommandContext context, string input, int argPos)
    {
        var searchResult = _commands.Search(context, input[argPos..]);
        if (!searchResult.IsSuccess)
            return (false, null, null);

        var commands = searchResult.Commands;
        var preconditionResults = await Task.WhenAll(commands.Select(async match =>
            (match, await match.Command.CheckPreconditionsAsync(context, Services).ConfigureAwait(false))));

        var successfulPreconditions = preconditionResults.Where(x => x.Item2.IsSuccess).ToArray();
        if (successfulPreconditions.Length == 0)
        {
            var bestCandidate = preconditionResults
                .OrderByDescending(x => x.match.Command.Priority)
                .First(x => !x.Item2.IsSuccess);
            return (false, bestCandidate.Item2.ErrorReason, commands[0].Command);
        }

        var parseResults = await Task.WhenAll(successfulPreconditions.Select(async x =>
            (x.match, parseResult: await x.match.ParseAsync(context, searchResult, x.Item2, Services)
                .ConfigureAwait(false))));

        var successfulParses = parseResults
            .Where(x => x.parseResult.IsSuccess)
            .OrderByDescending(x => x.match.Command.Priority)
            .ThenByDescending(x => x.parseResult.ArgValues.Sum(y => y.Values.Sum(z => z.Score)))
            .ToArray();

        if (successfulParses.Length == 0)
        {
            var bestMatch = parseResults.First(x => !x.parseResult.IsSuccess);
            return (false, bestMatch.parseResult.ErrorReason, commands[0].Command);
        }

        var chosen = successfulParses[0];
        var command = chosen.match.Command;

        if (!_usersOnShortCooldown.Add(context.User.Id))
            return (false, null, command);

        foreach (var blocker in Services.GetServices<ILateBlocker>().OrderBy(x => x.Priority))
        {
            if (await blocker.TryBlockLate(_client, context, command.Module.GetTopLevelModule().Name, command)
                    .ConfigureAwait(false))
                return (false, null, command);
        }

        var result = await chosen.match.ExecuteAsync(context, chosen.parseResult, Services).ConfigureAwait(false);
        return (result.IsSuccess, result.ErrorReason, command);
    }

    private static void LogCommand(SocketMessage msg, CommandInfo? command, bool success, int elapsedMs,
        string? error = null)
    {
        if (success)
        {
            Log.Information("Command {Module}/{Command} by {UserId} in {GuildId}/{ChannelId} took {Elapsed}ms",
                command?.Module.Name, command?.Name, msg.Author?.Id, msg.Guild?.Id, msg.ChannelId, elapsedMs);
            return;
        }

        Log.Warning("Command {Module}/{Command} by {UserId} in {GuildId}/{ChannelId} failed after {Elapsed}ms: {Error}",
            command?.Module.Name, command?.Name, msg.Author?.Id, msg.Guild?.Id, msg.ChannelId, elapsedMs, error);
    }

    private int GetPrefixLength(string content, string prefix)
    {
        if (!string.IsNullOrEmpty(prefix) && content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return prefix.Length;

        if (_client.Gateway.CurrentUser is not { } me)
            return 0;

        return new[] { $"<@{me.Id}>", $"<@!{me.Id}>" }
            .Where(mention => content.StartsWith(mention + " ", StringComparison.Ordinal))
            .Select(mention => mention.Length + 1)
            .FirstOrDefault();
    }
}
