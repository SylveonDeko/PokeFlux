using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Channel = System.Threading.Channels.Channel;
using EeveeCore.Database.Models.Mongo.Discord;
using Guild = EeveeCore.Database.Models.Mongo.Discord.Guild;
using EeveeCore.Services.Impl;
using MongoDB.Driver;
using Serilog;

namespace EeveeCore.Services;

/// <summary>
///     Service responsible for managing Discord guild configurations.
///     Provides methods for retrieving and updating guild settings with proper database context management.
/// </summary>
public class GuildSettingsService : IAsyncDisposable
{
    private const string PrefixCacheKey = "prefix:{0}";
    private const string ConfigCacheKey = "guild_config:{0}";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private readonly IDataCache _cache;
    private readonly ConcurrentDictionary<ulong, GuildConfigChanged> _changeTracker;
    private readonly BotCredentials _creds;
    private readonly IMongoService _mongo;
    private readonly Channel<(ulong GuildId, Guild Config)> _updateChannel;

    /// <summary>
    ///     Initializes a new instance of the GuildSettingsService.
    /// </summary>
    /// <param name="mongo">Provider for mongo access</param>
    /// <param name="creds">Bot credentials.</param>
    /// <param name="cache">Memory cache for storing frequently accessed guild settings.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required dependency is null.</exception>
    public GuildSettingsService(
        IMongoService mongo,
        BotCredentials creds,
        IDataCache cache)
    {
        _mongo = mongo;
        _creds = creds;
        _cache = cache;
        _changeTracker = new ConcurrentDictionary<ulong, GuildConfigChanged>();
        _updateChannel = Channel.CreateUnbounded<(ulong, Guild)>(
            new UnboundedChannelOptions { SingleReader = true });

        _ = ProcessConfigUpdatesAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _updateChannel.Writer.Complete();
        await SaveConfigBatchAsync(
            _changeTracker.Select(x => (x.Key, GetGuildConfigAsync(x.Key).GetAwaiter().GetResult())));
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Retrieves the command prefix for a specified guild with caching.
    /// </summary>
    /// <param name="guild">The Discord guild to get the prefix for. Can be null for default prefix.</param>
    /// <returns>
    ///     The guild's custom prefix if set, otherwise the default bot prefix.
    ///     Returns default prefix if guild is null.
    /// </returns>
    public async Task<string> GetPrefix(SocketGuild? guild)
    {
        if (guild is null)
            return _creds.DefaultPrefix;

        var cacheKey = string.Format(PrefixCacheKey, guild.Id);
        return await _cache.GetOrAddCachedDataAsync(cacheKey,
            async () =>
            {
                var config = await GetGuildConfigAsync(guild.Id);
                return string.IsNullOrWhiteSpace(config.Prefix)
                    ? _creds.DefaultPrefix
                    : config.Prefix;
            },
            CacheDuration);
    }

    /// <summary>
    ///     Sets a new command prefix for a specified guild and updates cache.
    /// </summary>
    /// <param name="guild">The Discord guild to set the prefix for.</param>
    /// <param name="prefix">The new prefix to set.</param>
    /// <returns>The newly set prefix.</returns>
    /// <exception cref="ArgumentNullException">Thrown when either guild or prefix is null.</exception>
    public async Task<string> SetPrefix(SocketGuild guild, string prefix)
    {
        ArgumentNullException.ThrowIfNull(guild);
        ArgumentNullException.ThrowIfNull(prefix);

        var config = await GetGuildConfigAsync(guild.Id);
        config.Prefix = prefix;

        _changeTracker.AddOrUpdate(guild.Id,
            _ => new GuildConfigChanged { LastModified = DateTime.UtcNow },
            (_, existing) =>
            {
                existing.LastModified = DateTime.UtcNow;
                return existing;
            });

        await _updateChannel.Writer.WriteAsync((guild.Id, config));
        await _cache.RemoveFromCache(string.Format(PrefixCacheKey, guild.Id));

        return prefix;
    }

    /// <summary>
    ///     Gets the guild configuration for the specified guild ID with caching.
    /// </summary>
    /// <param name="guildId">The ID of the guild to get configuration for.</param>
    /// <param name="callerName">The name of the calling method for logging purposes.</param>
    /// <param name="filePath">The file path of the calling method for logging purposes.</param>
    /// <returns>The guild configuration.</returns>
    /// <exception cref="Exception">Thrown when failing to get guild config.</exception>
    public async Task<Guild> GetGuildConfigAsync(
        ulong guildId,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string filePath = "")
    {
        var cacheKey = string.Format(ConfigCacheKey, guildId);
        try
        {
            return await _cache.GetOrAddCachedDataAsync(
                cacheKey,
                async () =>
                {
                    var config = await _mongo.Guilds
                        .Find(x => x.GuildId == guildId)
                        .FirstOrDefaultAsync();

                    return config ?? await CreateGuildConfigAsync(guildId);
                },
                CacheDuration);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed getting guild config for {GuildId} from {CallerName} in {FilePath}",
                guildId, callerName, filePath);
            throw;
        }
    }

    private async Task<Guild> CreateGuildConfigAsync(ulong guildId)
    {
        var config = new Guild
        {
            GuildId = guildId,
            Prefix = _creds.DefaultPrefix,
            DeleteSpawns = false,
            SmallImages = false,
            SilenceLevels = false,
            PinSpawns = false,
            DisabledChannels = [],
            EnabledChannels = [],
            DisabledSpawnChannels = [],
            EnableSpawnsAll = false,
            ModalView = false,
            Speed = 10,
            Locale = "en"
        };

        await _mongo.Guilds.InsertOneAsync(config);
        return config;
    }

    /// <summary>
    ///     Updates the guild configuration for a specified guild with proper cache invalidation.
    /// </summary>
    /// <param name="guildId">The ID of the guild to update configuration for.</param>
    /// <param name="config">The updated guild configuration.</param>
    /// <param name="callerName">The name of the calling method for logging purposes.</param>
    /// <param name="filePath">The file path of the calling method for logging purposes.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="Exception">Thrown when the update operation fails.</exception>
    public async Task UpdateGuildConfigAsync(
        ulong guildId,
        Guild config,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string filePath = "")
    {
        try
        {
            _changeTracker.AddOrUpdate(guildId,
                _ => new GuildConfigChanged { LastModified = DateTime.UtcNow },
                (_, existing) =>
                {
                    existing.LastModified = DateTime.UtcNow;
                    return existing;
                });

            await _updateChannel.Writer.WriteAsync((guildId, config));

            var cacheKey = string.Format(ConfigCacheKey, guildId);
            await _cache.AddToCache(cacheKey, config, CacheDuration);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error updating guild config for {GuildId} from {CallerName} in {FilePath}",
                guildId, callerName, filePath);
            throw;
        }
    }

    private async Task ProcessConfigUpdatesAsync()
    {
        var batch = new List<(ulong GuildId, Guild Config)>();

        while (await _updateChannel.Reader.WaitToReadAsync())
        {
            while (batch.Count < 100 && _updateChannel.Reader.TryRead(out var update)) batch.Add(update);

            if (batch.Count > 0)
            {
                await SaveConfigBatchAsync(batch);
                batch.Clear();
            }
        }
    }

    private async Task SaveConfigBatchAsync(
        IEnumerable<(ulong GuildId, Guild Config)> updates)
    {
        try
        {
            var bulkOps = new List<WriteModel<Guild>>();

            foreach (var (_, config) in updates)
            {
                var filter = Builders<Guild>.Filter
                    .Eq(x => x.GuildId, config.GuildId);

                var update = Builders<Guild>.Update
                    .Set(x => x.Prefix, config.Prefix)
                    .Set(x => x.DeleteSpawns, config.DeleteSpawns)
                    .Set(x => x.SmallImages, config.SmallImages)
                    .Set(x => x.SilenceLevels, config.SilenceLevels)
                    .Set(x => x.PinSpawns, config.PinSpawns)
                    .Set(x => x.DisabledChannels, config.DisabledChannels)
                    .Set(x => x.EnabledChannels, config.EnabledChannels)
                    .Set(x => x.DisabledSpawnChannels, config.DisabledSpawnChannels)
                    .Set(x => x.EnableSpawnsAll, config.EnableSpawnsAll)
                    .Set(x => x.ModalView, config.ModalView)
                    .Set(x => x.Speed, config.Speed)
                    .Set(x => x.Locale, config.Locale);

                bulkOps.Add(new UpdateOneModel<Guild>(filter, update)
                {
                    IsUpsert = true
                });
            }

            if (bulkOps.Any()) await _mongo.Guilds.BulkWriteAsync(bulkOps);

            foreach (var (guildId, config) in updates)
            {
                var cacheKey = string.Format(ConfigCacheKey, guildId);
                await _cache.AddToCache(cacheKey, config, CacheDuration);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving guild configs batch");
        }
    }

    private class GuildConfigChanged
    {
        public DateTime LastModified { get; set; }
    }
}