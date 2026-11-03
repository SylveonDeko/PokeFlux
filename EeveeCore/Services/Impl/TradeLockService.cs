namespace EeveeCore.Services.Impl;

/// <summary>
///     Service for managing trade locks using Redis to prevent concurrent trade operations.
/// </summary>
public class TradeLockService : ITradeLockService, INService
{
    private readonly IDataCache _cache;
    private const string TradeLockKey = "tradelock";

    /// <summary>
    ///     Initializes a new instance of the <see cref="TradeLockService" /> class.
    /// </summary>
    /// <param name="cache">The Redis cache service.</param>
    public TradeLockService(IDataCache cache)
    {
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<bool> IsUserTradeLockedAsync(ulong userId)
    {
        var database = _cache.Redis.GetDatabase();
        var tradeLockedUsers = await database.ListRangeAsync(TradeLockKey);
        
        return tradeLockedUsers.Any(value => 
            value.HasValue && 
            ulong.TryParse((string?)value, out var id) &&
            id == userId);
    }

    /// <inheritdoc />
    public async Task AddTradeLockAsync(ulong userId)
    {
        var database = _cache.Redis.GetDatabase();
        await database.ListLeftPushAsync(TradeLockKey, userId.ToString());
    }

    /// <inheritdoc />
    public async Task RemoveTradeLockAsync(ulong userId)
    {
        var database = _cache.Redis.GetDatabase();
        await database.ListRemoveAsync(TradeLockKey, userId.ToString());
    }

    /// <inheritdoc />
    public Task<bool> ExecuteWithTradeLockAsync(IUser user, Func<Task> action) =>
        ExecuteWithTradeLockAsync(user.Id, action);

    /// <inheritdoc />
    public async Task<bool> ExecuteWithTradeLockAsync(ulong userId, Func<Task> action)
    {
        // Check if user is already trade locked
        if (await IsUserTradeLockedAsync(userId))
            return false;

        try
        {
            // Add trade lock
            await AddTradeLockAsync(userId);

            // Execute the action
            await action();

            return true;
        }
        finally
        {
            // Always remove trade lock
            await RemoveTradeLockAsync(userId);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExecuteWithTradeLockAsync(IUser user1, IUser user2, Func<Task> action)
    {
        // Check if either user is already trade locked
        if (await IsUserTradeLockedAsync(user1.Id) || await IsUserTradeLockedAsync(user2.Id))
            return false;

        try
        {
            // Add trade locks for both users
            await AddTradeLockAsync(user1.Id);
            await AddTradeLockAsync(user2.Id);
            
            // Execute the action
            await action();
            
            return true;
        }
        finally
        {
            // Always remove trade locks for both users
            await RemoveTradeLockAsync(user1.Id);
            await RemoveTradeLockAsync(user2.Id);
        }
    }

    /// <inheritdoc />
    public async Task ClearAllTradeLocksAsync(ulong userId)
    {
        var database = _cache.Redis.GetDatabase();
        
        // Remove all instances of this user from the trade lock list
        // Use -1 to remove all occurrences
        await database.ListRemoveAsync(TradeLockKey, userId.ToString(), -1);
    }
}