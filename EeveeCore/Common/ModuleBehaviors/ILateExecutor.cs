namespace EeveeCore.Common.ModuleBehaviors;

/// <summary>
///     Interface to be implemented by modules that execute last and do not block further executions.
/// </summary>
public interface ILateExecutor
{
    /// <summary>
    ///     Executes the module's behavior.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="guild">The guild in which the message was sent.</param>
    /// <param name="msg">The message that triggered the module.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task LateExecute(FluxerClient client, SocketGuild? guild, Message msg);
}