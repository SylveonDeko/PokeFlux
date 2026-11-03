using System.Text.Json;
using StackExchange.Redis;

namespace EeveeCore.Services.Impl;

/// <inheritdoc />
public class RedisCache : IDataCache
{
    private readonly IDatabase _db;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RedisCache" /> class.
    ///     Attempts to establish a connection to the redis server.
    /// </summary>
    /// <exception cref="RedisConnectionException">
    ///     Thrown when Redis is either not running or cannot be reached.
    /// </exception>
    public RedisCache(BotCredentials creds)
    {
        Redis = ConnectionMultiplexer.Connect(creds.RedisConfig.ConnectionString);
        _db = Redis.GetDatabase();
    }

    /// <inheritdoc />
    public ISubscriber Subscriber => Redis.GetSubscriber();

    /// <inheritdoc />
    public ConnectionMultiplexer Redis { get; }

    /// <inheritdoc />
    public async Task<T> GetOrAddCachedDataAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null)
    {
        var data = await _db.StringGetAsync(key);
        if (data.HasValue)
            return JsonSerializer.Deserialize<T>((string)data!);

        var value = await factory();
        await _db.StringSetAsync(key,
            JsonSerializer.Serialize(value),
            expiry ?? TimeSpan.FromHours(1));

        return value;
    }

    /// <inheritdoc />
    public async Task<bool> TryAddToCache(string key, object value, TimeSpan? expiry = null)
    {
        return await _db.StringSetAsync(key,
            JsonSerializer.Serialize(value),
            expiry ?? TimeSpan.FromHours(1),
            When.NotExists);
    }

    /// <inheritdoc />
    public async Task AddToCache(string key, object value, TimeSpan? expiry = null)
    {
        await _db.StringSetAsync(key,
            JsonSerializer.Serialize(value),
            expiry ?? TimeSpan.FromHours(1));
    }

    /// <inheritdoc />
    public async Task RemoveFromCache(params string[] keys)
    {
        await _db.KeyDeleteAsync(keys.Select(x => new RedisKey(x)).ToArray());
    }

    /// <inheritdoc />
    public async Task<bool> ExistsInCache(string key)
    {
        return await _db.KeyExistsAsync(key);
    }

    /// <inheritdoc />
    public async Task<T> GetFromCache<T>(string key)
    {
        var data = await _db.StringGetAsync(key);
        return data.HasValue ? JsonSerializer.Deserialize<T>((string)data!) : default;
    }

    /// <inheritdoc />
    public async Task PublishAsync(string channel, object data)
    {
        await Subscriber.PublishAsync(channel, JsonSerializer.Serialize(data));
    }

    /// <inheritdoc />
    public async Task SubscribeAsync(string channel, Action<string> handler)
    {
        await Subscriber.SubscribeAsync(channel, (_, message) =>
        {
            if (message.HasValue)
                handler(message);
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Redis?.Dispose();
    }
}