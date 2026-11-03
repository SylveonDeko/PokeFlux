namespace EeveeCore.Services;

/// <summary>
///     Service interface for managing trade locks using Redis to prevent concurrent trade operations.
/// </summary>
public interface ITradeLockService
{
    /// <summary>
    ///     Checks if a user is currently trade locked.
    /// </summary>
    /// <param name="userId">The Discord user ID to check.</param>
    /// <returns>True if the user is trade locked, false otherwise.</returns>
    Task<bool> IsUserTradeLockedAsync(ulong userId);

    /// <summary>
    ///     Adds a user to the trade lock list.
    /// </summary>
    /// <param name="userId">The Discord user ID to lock.</param>
    Task AddTradeLockAsync(ulong userId);

    /// <summary>
    ///     Removes a user from the trade lock list.
    /// </summary>
    /// <param name="userId">The Discord user ID to unlock.</param>
    Task RemoveTradeLockAsync(ulong userId);

    /// <summary>
    ///     Executes an action while holding a trade lock for a single user.
    /// </summary>
    /// <param name="user">The user to lock.</param>
    /// <param name="action">The action to execute while locked.</param>
    /// <returns>True if the action was executed successfully, false if the user was already locked.</returns>
    Task<bool> ExecuteWithTradeLockAsync(IUser user, Func<Task> action);

    /// <summary>
    ///     Executes an action while holding a trade lock for a single user.
    /// </summary>
    /// <param name="userId">The ID of the user to lock.</param>
    /// <param name="action">The action to execute while locked.</param>
    /// <returns>True if the action was executed successfully, false if the user was already locked.</returns>
    Task<bool> ExecuteWithTradeLockAsync(ulong userId, Func<Task> action);

    /// <summary>
    ///     Executes an action while holding trade locks for two users.
    /// </summary>
    /// <param name="user1">The first user to lock.</param>
    /// <param name="user2">The second user to lock.</param>
    /// <param name="action">The action to execute while both users are locked.</param>
    /// <returns>True if the action was executed successfully, false if either user was already locked.</returns>
    Task<bool> ExecuteWithTradeLockAsync(IUser user1, IUser user2, Func<Task> action);

    /// <summary>
    ///     Clears all trade locks for a user, used for recovery purposes.
    /// </summary>
    /// <param name="userId">The Discord user ID to clear all locks for.</param>
    Task ClearAllTradeLocksAsync(ulong userId);
}