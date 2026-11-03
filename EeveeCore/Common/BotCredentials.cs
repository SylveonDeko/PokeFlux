using System.Collections.Immutable;

namespace EeveeCore.Common;

/// <summary>
///     Provides configuration and credentials for the bot application.
/// </summary>
public class BotCredentials : IBotCredentials
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="BotCredentials" /> class.
    ///     Loads configuration from config.json and environment variables.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    ///     Thrown when required configuration values are missing.
    /// </exception>
    public BotCredentials()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("config.json", false, true)
            .AddEnvironmentVariables()
            .Build();

        Token = config[nameof(Token)]
                ?? throw new ArgumentNullException(nameof(Token), "Bot token must be provided in config");
        DefaultPrefix = config[nameof(DefaultPrefix)] ?? "";
        FluxerInstanceUrl = config[nameof(FluxerInstanceUrl)];

        DebugGuildId = ulong.TryParse(config[nameof(DebugGuildId)], out var devGuild)
            ? devGuild
            : 0;

        GuildJoinsChannelId = ulong.TryParse(config[nameof(GuildJoinsChannelId)], out var joinLog)
            ? joinLog
            : 0;

        LogChannelId = ulong.TryParse(config[nameof(LogChannelId)], out var botLog)
            ? botLog
            : 0;

        CdnBaseUrl = config[nameof(CdnBaseUrl)]?.TrimEnd('/');
        CdnKey = config[nameof(CdnKey)];

        StartLogChannelId = ulong.TryParse(config[nameof(StartLogChannelId)], out var startLog)
            ? startLog
            : 0;

        VoucherChannelId = ulong.TryParse(config[nameof(VoucherChannelId)], out var vouchers)
            ? vouchers
            : 0;

        OwnerIds =
        [
            ..config.GetSection(nameof(OwnerIds)).GetChildren()
                .Select(c => ulong.Parse(c.Value))
        ];

        VoucherManagerIds =
        [
            ..config.GetSection(nameof(VoucherManagerIds)).GetChildren()
                .Select(c => ulong.Parse(c.Value))
        ];

        IsDebug = bool.Parse(config[nameof(IsDebug)] ?? "false");

        PostgresConfig = new DbConfig
        {
            ConnectionString = config["PostgresConnectionString"]
                               ?? throw new ArgumentNullException("PostgresConnectionString",
                                   "Postgres connection string must be provided"),
        };

        MongoConfig = new DbConfig
        {
            ConnectionString = config["MongoConnectionString"]
                               ?? throw new ArgumentNullException("MongoConnectionString",
                                   "MongoDB connection string must be provided"),
            Name = "MongoDB"
        };

        RedisConfig = new DbConfig
        {
            ConnectionString = config["RedisConnectionString"]
                               ?? throw new ArgumentNullException("RedisConnectionString",
                                   "Redis connection string must be provided"),
            Name = "Redis"
        };

        // Web API Configuration
        IsApiEnabled = bool.Parse(config[nameof(IsApiEnabled)] ?? "false");
        ApiPort = int.Parse(config[nameof(ApiPort)] ?? "5000");
        JwtSecret = config[nameof(JwtSecret)];
        ApiKey = config[nameof(ApiKey)];
    }

    /// <summary>
    ///     Gets the secret the dashboard signs user tokens with, or null to reject every user token.
    /// </summary>
    public string? JwtSecret { get; }

    /// <summary>
    ///     Gets the address of a self-hosted Fluxer instance to connect to, or null for fluxer.app.
    /// </summary>
    public string? FluxerInstanceUrl { get; }

    /// <summary>
    ///     Gets the Discord bot token used for authentication.
    /// </summary>
    public string Token { get; }

    /// <summary>
    ///     Gets the guild ID used for testing and debugging purposes.
    /// </summary>
    public ulong DebugGuildId { get; }

    /// <summary>
    ///     Gets the channel ID where guild join notifications are sent.
    /// </summary>
    public ulong GuildJoinsChannelId { get; }

    /// <summary>
    ///     Gets the channel ID where registrations, trades, errors and other bot events are logged, or 0 to only
    ///     write them to the console log.
    /// </summary>
    public ulong LogChannelId { get; }

    /// <summary>
    ///     Gets the base URL the images in <c>data/</c> are hosted at, or null to upload them with each message.
    /// </summary>
    public string? CdnBaseUrl { get; }

    /// <summary>
    ///     Gets the secret that hosted image names are derived from, so names do not reveal the image.
    /// </summary>
    public string? CdnKey { get; }

    /// <summary>
    ///     Gets the channel ID where new player registrations are logged, or 0 to use the log channel.
    /// </summary>
    public ulong StartLogChannelId { get; }

    /// <summary>
    ///     Gets the channel ID where voucher requests are posted, or 0 when vouchers are off.
    /// </summary>
    public ulong VoucherChannelId { get; }

    /// <summary>
    ///     Gets the IDs of the users who can update voucher requests, besides the owners.
    /// </summary>
    public ImmutableArray<ulong> VoucherManagerIds { get; }

    /// <summary>
    ///     Gets a value indicating whether the bot is running in debug mode.
    /// </summary>
    public bool IsDebug { get; }

    /// <summary>
    ///     Gets the default command prefix for the bot.
    /// </summary>
    public string DefaultPrefix { get; }

    /// <summary>
    /// </summary>
    public DbConfig PostgresConfig { get; }

    /// <summary>
    ///     Gets the configuration for MongoDB database connection.
    /// </summary>
    public DbConfig MongoConfig { get; }

    /// <summary>
    ///     Gets the configuration for Redis database connection.
    /// </summary>
    public DbConfig RedisConfig { get; }

    /// <summary>
    ///     Gets a value indicating whether the web API is enabled.
    /// </summary>
    public bool IsApiEnabled { get; }

    /// <summary>
    ///     Gets the port for the web API server.
    /// </summary>
    public int ApiPort { get; }

    /// <summary>
    ///     Gets the key the dashboard sends in the X-API-Key header, or null to reject every request.
    /// </summary>
    public string? ApiKey { get; }

    /// <summary>
    ///     Gets or sets the collection of user IDs that have owner privileges.
    /// </summary>
    public ImmutableArray<ulong> OwnerIds { get; set; }

    /// <summary>
    ///     Determines whether a user has owner privileges.
    /// </summary>
    /// <param name="u">The user to check.</param>
    /// <returns>True if the user is an owner; otherwise, false.</returns>
    public bool IsOwner(IUser u)
    {
        return OwnerIds.Contains(u.Id);
    }
}

/// <summary>
///     Defines the interface for bot credentials and configuration.
/// </summary>
public interface IBotCredentials
{
    /// <summary>
    ///     Gets the Discord bot token used for authentication.
    /// </summary>
    string Token { get; }

    /// <summary>
    ///     Gets or sets the collection of user IDs that have owner privileges.
    /// </summary>
    public ImmutableArray<ulong> OwnerIds { get; set; }

    /// <summary>
    ///     Gets the default command prefix for the bot.
    /// </summary>
    string DefaultPrefix { get; }

    /// <summary>
    ///     Gets the guild ID used for testing and debugging purposes.
    /// </summary>
    ulong DebugGuildId { get; }

    /// <summary>
    ///     Gets the channel ID where guild join notifications are sent.
    /// </summary>
    ulong GuildJoinsChannelId { get; }

    /// <summary>
    ///     Gets the channel ID where registrations, trades, errors and other bot events are logged, or 0 to only
    ///     write them to the console log.
    /// </summary>
    ulong LogChannelId { get; }

    /// <summary>
    ///     Gets a value indicating whether the bot is running in debug mode.
    /// </summary>
    bool IsDebug { get; }

    /// <summary>
    /// </summary>
    DbConfig PostgresConfig { get; }

    /// <summary>
    ///     Gets the configuration for MongoDB database connection.
    /// </summary>
    DbConfig MongoConfig { get; }

    /// <summary>
    ///     Gets the configuration for Redis database connection.
    /// </summary>
    DbConfig RedisConfig { get; }

    /// <summary>
    ///     Gets a value indicating whether the web API is enabled.
    /// </summary>
    bool IsApiEnabled { get; }

    /// <summary>
    ///     Gets the port for the web API server.
    /// </summary>
    int ApiPort { get; }

    /// <summary>
    ///     Gets the key the dashboard sends in the X-API-Key header, or null to reject every request.
    /// </summary>
    string? ApiKey { get; }

    /// <summary>
    ///     Determines whether a user has owner privileges.
    /// </summary>
    /// <param name="u">The user to check.</param>
    /// <returns>True if the user is an owner; otherwise, false.</returns>
    public bool IsOwner(IUser u)
    {
        return OwnerIds.Contains(u.Id);
    }
}

/// <summary>
///     Represents database connection configuration.
/// </summary>
public class DbConfig
{
    /// <summary>
    ///     Gets or sets the connection string for the database.
    /// </summary>
    public string ConnectionString { get; set; }

    /// <summary>
    ///     Gets or sets the friendly name of the database.
    /// </summary>
    public string Name { get; set; }
}