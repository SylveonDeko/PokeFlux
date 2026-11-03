using EeveeCore.Database.Linq.Models.Game;
using LinqToDB.Async;

namespace EeveeCore.Services.Network;

/// <summary>
///     Service for building and analyzing trade network graphs for fraud detection.
///     Maintains a graph representation of all trades to detect suspicious patterns.
/// </summary>
public class TradeNetworkGraphService : INService
{
    private readonly LinqToDbConnectionProvider _dbProvider;
    private readonly IDataCache _cache;
    private readonly ILogger<TradeNetworkGraphService> _logger;

    // Cache expiry times
    private const int NetworkCacheHours = 6;
    private const int NodeCacheHours = 1;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TradeNetworkGraphService" /> class.
    /// </summary>
    /// <param name="dbProvider">The LinqToDB connection provider.</param>
    /// <param name="cache">The cache service.</param>
    /// <param name="logger">The logger.</param>
    public TradeNetworkGraphService(LinqToDbConnectionProvider dbProvider, IDataCache cache, ILogger<TradeNetworkGraphService> logger)
    {
        _dbProvider = dbProvider;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    ///     Gets the complete trade network for analysis.
    /// </summary>
    /// <param name="timeWindowDays">Number of days to include in the network (default: 30).</param>
    /// <returns>The trade network graph.</returns>
    public async Task<TradeNetworkGraph> GetTradeNetworkAsync(int timeWindowDays = 30)
    {
        var cacheKey = $"trade_network:days_{timeWindowDays}";
        var database = _cache.Redis.GetDatabase();
        
        var cachedNetwork = await database.StringGetAsync(cacheKey);
        if (cachedNetwork.HasValue)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<TradeNetworkGraph>((string)cachedNetwork!)!;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize cached trade network, rebuilding");
            }
        }

        var network = await BuildTradeNetworkFromDatabaseAsync(timeWindowDays);
        
        // Cache the network
        var serialized = System.Text.Json.JsonSerializer.Serialize(network);
        await database.StringSetAsync(cacheKey, serialized, TimeSpan.FromHours(NetworkCacheHours));

        return network;
    }

    /// <summary>
    ///     Gets the trade network centered around a specific user.
    /// </summary>
    /// <param name="userId">The user ID to center the network around.</param>
    /// <param name="hops">Number of hops to include (default: 2).</param>
    /// <param name="timeWindowDays">Number of days to include (default: 30).</param>
    /// <returns>The user-centered trade network.</returns>
    public async Task<TradeNetworkGraph> GetUserNetworkAsync(ulong userId, int hops = 2, int timeWindowDays = 30)
    {
        var cacheKey = $"user_network:{userId}:hops_{hops}:days_{timeWindowDays}";
        var database = _cache.Redis.GetDatabase();
        
        var cachedNetwork = await database.StringGetAsync(cacheKey);
        if (cachedNetwork.HasValue)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<TradeNetworkGraph>((string)cachedNetwork!)!;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize cached user network for {UserId}, rebuilding", userId);
            }
        }

        var network = await BuildUserNetworkFromDatabaseAsync(userId, hops, timeWindowDays);
        
        // Cache the network
        var serialized = System.Text.Json.JsonSerializer.Serialize(network);
        await database.StringSetAsync(cacheKey, serialized, TimeSpan.FromHours(NodeCacheHours));

        return network;
    }

    /// <summary>
    ///     Builds the complete trade network from database records.
    /// </summary>
    /// <param name="timeWindowDays">Number of days to include.</param>
    /// <returns>The built trade network.</returns>
    private async Task<TradeNetworkGraph> BuildTradeNetworkFromDatabaseAsync(int timeWindowDays)
    {
        var cutoffTime = DateTime.UtcNow.AddDays(-timeWindowDays);
        
        await using var db = await _dbProvider.GetConnectionAsync();
        
        // Get all trade relationships within the time window
        var relationships = await db.UserTradeRelationships
            .Where(r => r.LastTradeTimestamp >= cutoffTime)
            .ToListAsync();

        // Get all users involved in trades
        var userIds = relationships
            .SelectMany(r => new ulong[] { r.User1Id, r.User2Id })
            .Distinct()
            .ToList();

        // Get user account ages
        var userAccountAges = new Dictionary<ulong, double>();
        foreach (var userId in userIds)
        {
            userAccountAges[userId] = CalculateAccountAgeFromUserId(userId, DateTime.UtcNow);
        }

        // Build nodes
        var nodes = userIds.Select(userId => new TradeNetworkNode
        {
            UserId = userId,
            AccountAgeDays = userAccountAges[userId],
            TotalTrades = relationships.Where(r => r.User1Id == userId || r.User2Id == userId).Sum(r => r.TotalTrades),
            TotalValueGiven = relationships.Where(r => r.User1Id == userId).Sum(r => (int)r.User1TotalGivenValue) +
                             relationships.Where(r => r.User2Id == userId).Sum(r => (int)r.User2TotalGivenValue),
            TotalValueReceived = relationships.Where(r => r.User1Id == userId).Sum(r => (int)r.User2TotalGivenValue) +
                                relationships.Where(r => r.User2Id == userId).Sum(r => (int)r.User1TotalGivenValue),
            RiskScore = relationships.Where(r => r.User1Id == userId || r.User2Id == userId)
                .Max(r => r.RelationshipRiskScore)
        }).ToDictionary(n => n.UserId);

        // Build edges
        var edges = relationships.Select(r => new TradeNetworkEdge
        {
            FromUserId = r.User1Id,
            ToUserId = r.User2Id,
            TradeCount = r.TotalTrades,
            TotalValue = r.User1TotalGivenValue + r.User2TotalGivenValue,
            ValueImbalanceRatio = r.ValueImbalanceRatio,
            RiskScore = r.RelationshipRiskScore,
            FirstTradeTime = r.FirstTradeTimestamp,
            LastTradeTime = r.LastTradeTimestamp,
            IsSuspicious = r.FlaggedPotentialAlts || r.FlaggedPotentialRmt || r.FlaggedNewbieExploitation
        }).ToList();

        return new TradeNetworkGraph
        {
            Nodes = nodes,
            Edges = edges,
            TimeWindowDays = timeWindowDays,
            GeneratedAt = DateTime.UtcNow,
            TotalUsers = nodes.Count,
            TotalRelationships = edges.Count
        };
    }

    /// <summary>
    ///     Builds a user-centered trade network from database records.
    /// </summary>
    /// <param name="centralUserId">The central user ID.</param>
    /// <param name="hops">Number of hops to include.</param>
    /// <param name="timeWindowDays">Number of days to include.</param>
    /// <returns>The built user network.</returns>
    private async Task<TradeNetworkGraph> BuildUserNetworkFromDatabaseAsync(ulong centralUserId, int hops, int timeWindowDays)
    {
        await using var db = await _dbProvider.GetConnectionAsync();
        
        var cutoffTime = DateTime.UtcNow.AddDays(-timeWindowDays);
        var includedUsers = new HashSet<ulong> { centralUserId };
        var currentHopUsers = new HashSet<ulong> { centralUserId };

        // Expand network by hops
        for (var hop = 0; hop < hops; hop++)
        {
            var nextHopUsers = new HashSet<ulong>();
            
            foreach (var userId in currentHopUsers)
            {
                var connections = await db.UserTradeRelationships
                    .Where(r => (r.User1Id == userId || r.User2Id == userId) && 
                               r.LastTradeTimestamp >= cutoffTime)
                    .ToListAsync();

                foreach (var connection in connections)
                {
                    var otherUserId = connection.User1Id == userId ? connection.User2Id : connection.User1Id;
                    if (includedUsers.Add(otherUserId))
                    {
                        nextHopUsers.Add(otherUserId);
                    }
                }
            }

            currentHopUsers = nextHopUsers;
            if (!currentHopUsers.Any()) break;
        }

        // Get relationships between included users
        var relationships = await db.UserTradeRelationships
            .Where(r => includedUsers.Contains(r.User1Id) && includedUsers.Contains(r.User2Id) &&
                       r.LastTradeTimestamp >= cutoffTime)
            .ToListAsync();

        // Build the network using the same logic as the full network
        return await BuildNetworkFromRelationships(relationships, timeWindowDays);
    }

    /// <summary>
    ///     Builds a network graph from a collection of trade relationships.
    /// </summary>
    /// <param name="relationships">The trade relationships.</param>
    /// <param name="timeWindowDays">The time window in days.</param>
    /// <returns>The built network graph.</returns>
    private async Task<TradeNetworkGraph> BuildNetworkFromRelationships(List<UserTradeRelationship> relationships, int timeWindowDays)
    {
        var userIds = relationships
            .SelectMany(r => new ulong[] { r.User1Id, r.User2Id })
            .Distinct()
            .ToList();

        var userAccountAges = new Dictionary<ulong, double>();
        foreach (var userId in userIds)
        {
            userAccountAges[userId] = CalculateAccountAgeFromUserId(userId, DateTime.UtcNow);
        }

        var nodes = userIds.Select(userId => new TradeNetworkNode
        {
            UserId = userId,
            AccountAgeDays = userAccountAges[userId],
            TotalTrades = relationships.Where(r => r.User1Id == userId || r.User2Id == userId).Sum(r => r.TotalTrades),
            TotalValueGiven = relationships.Where(r => r.User1Id == userId).Sum(r => (int)r.User1TotalGivenValue) +
                             relationships.Where(r => r.User2Id == userId).Sum(r => (int)r.User2TotalGivenValue),
            TotalValueReceived = relationships.Where(r => r.User1Id == userId).Sum(r => (int)r.User2TotalGivenValue) +
                                relationships.Where(r => r.User2Id == userId).Sum(r => (int)r.User1TotalGivenValue),
            RiskScore = relationships.Where(r => r.User1Id == userId || r.User2Id == userId)
                .DefaultIfEmpty()
                .Max(r => r?.RelationshipRiskScore ?? 0.0)
        }).ToDictionary(n => n.UserId);

        var edges = relationships.Select(r => new TradeNetworkEdge
        {
            FromUserId = r.User1Id,
            ToUserId = r.User2Id,
            TradeCount = r.TotalTrades,
            TotalValue = r.User1TotalGivenValue + r.User2TotalGivenValue,
            ValueImbalanceRatio = r.ValueImbalanceRatio,
            RiskScore = r.RelationshipRiskScore,
            FirstTradeTime = r.FirstTradeTimestamp,
            LastTradeTime = r.LastTradeTimestamp,
            IsSuspicious = r.FlaggedPotentialAlts || r.FlaggedPotentialRmt || r.FlaggedNewbieExploitation
        }).ToList();

        return new TradeNetworkGraph
        {
            Nodes = nodes,
            Edges = edges,
            TimeWindowDays = timeWindowDays,
            GeneratedAt = DateTime.UtcNow,
            TotalUsers = nodes.Count,
            TotalRelationships = edges.Count
        };
    }

    /// <summary>
    ///     Calculates the age of a Discord account in days from its user ID.
    /// </summary>
    /// <param name="userId">The Discord user ID.</param>
    /// <param name="currentTime">The current time for calculation.</param>
    /// <returns>The account age in days.</returns>
    private static double CalculateAccountAgeFromUserId(ulong userId, DateTime currentTime)
    {
        // Discord snowflake epoch (January 1, 2015 00:00:00 UTC)
        var discordEpoch = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        
        // Extract timestamp from Discord snowflake
        var timestamp = (userId >> 22) + 1420070400000UL; // Discord epoch in milliseconds
        var accountCreated = discordEpoch.AddMilliseconds(timestamp - 1420070400000UL);
        
        return (currentTime - accountCreated).TotalDays;
    }

    /// <summary>
    ///     Clears cached network data for a specific time window.
    /// </summary>
    /// <param name="timeWindowDays">The time window to clear cache for.</param>
    public async Task ClearNetworkCacheAsync(int timeWindowDays)
    {
        var cacheKey = $"trade_network:days_{timeWindowDays}";
        var database = _cache.Redis.GetDatabase();
        await database.KeyDeleteAsync(cacheKey);
    }

    /// <summary>
    ///     Clears cached network data for a specific user.
    /// </summary>
    /// <param name="userId">The user ID to clear cache for.</param>
    public async Task ClearUserNetworkCacheAsync(ulong userId)
    {
        var database = _cache.Redis.GetDatabase();
        
        // Clear user network caches for different configurations
        var patterns = new[]
        {
            $"user_network:{userId}:*",
        };

        foreach (var pattern in patterns)
        {
            var keys = _cache.Redis.GetServer(_cache.Redis.GetEndPoints().First())
                .Keys(pattern: pattern);
            
            foreach (var key in keys)
            {
                await database.KeyDeleteAsync(key);
            }
        }
    }
}

/// <summary>
///     Represents a complete trade network graph.
/// </summary>
public class TradeNetworkGraph
{
    /// <summary>
    ///     Gets or sets the nodes (users) in the network.
    /// </summary>
    public Dictionary<ulong, TradeNetworkNode> Nodes { get; set; } = new();

    /// <summary>
    ///     Gets or sets the edges (trade relationships) in the network.
    /// </summary>
    public List<TradeNetworkEdge> Edges { get; set; } = new();

    /// <summary>
    ///     Gets or sets the time window in days that this network represents.
    /// </summary>
    public int TimeWindowDays { get; set; }

    /// <summary>
    ///     Gets or sets when this network graph was generated.
    /// </summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>
    ///     Gets or sets the total number of users in the network.
    /// </summary>
    public int TotalUsers { get; set; }

    /// <summary>
    ///     Gets or sets the total number of relationships in the network.
    /// </summary>
    public int TotalRelationships { get; set; }
}

/// <summary>
///     Represents a user node in the trade network.
/// </summary>
public class TradeNetworkNode
{
    /// <summary>
    ///     Gets or sets the user ID.
    /// </summary>
    public ulong UserId { get; set; }

    /// <summary>
    ///     Gets or sets the Discord username if available.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    ///     Gets or sets the account age in days.
    /// </summary>
    public double AccountAgeDays { get; set; }

    /// <summary>
    ///     Gets or sets the total number of trades this user has participated in.
    /// </summary>
    public int TotalTrades { get; set; }

    /// <summary>
    ///     Gets or sets the total value this user has given in trades.
    /// </summary>
    public decimal TotalValueGiven { get; set; }

    /// <summary>
    ///     Gets or sets the total value this user has received in trades.
    /// </summary>
    public decimal TotalValueReceived { get; set; }

    /// <summary>
    ///     Gets or sets the highest risk score associated with this user.
    /// </summary>
    public double RiskScore { get; set; }

    /// <summary>
    ///     Gets or sets the number of connections this node has.
    /// </summary>
    public int ConnectionCount { get; set; }

    /// <summary>
    ///     Gets or sets additional flags for this node.
    /// </summary>
    public List<string> Flags { get; set; } = new();

    /// <summary>
    ///     Gets the net value flow for this user (received - given).
    /// </summary>
    public decimal NetValueFlow => TotalValueReceived - TotalValueGiven;

    /// <summary>
    ///     Gets the value imbalance ratio (higher value / lower value).
    /// </summary>
    public double ValueImbalanceRatio => 
        TotalValueGiven == 0 || TotalValueReceived == 0 
            ? (double)Math.Max(TotalValueGiven, TotalValueReceived)
            : (double)(Math.Max(TotalValueGiven, TotalValueReceived) / Math.Min(TotalValueGiven, TotalValueReceived));
}

/// <summary>
///     Represents a trade relationship edge in the network.
/// </summary>
public class TradeNetworkEdge
{
    /// <summary>
    ///     Gets or sets the source user ID.
    /// </summary>
    public ulong FromUserId { get; set; }

    /// <summary>
    ///     Gets or sets the target user ID.
    /// </summary>
    public ulong ToUserId { get; set; }

    /// <summary>
    ///     Gets or sets the total number of trades between these users.
    /// </summary>
    public int TradeCount { get; set; }

    /// <summary>
    ///     Gets or sets the total value traded between these users.
    /// </summary>
    public decimal TotalValue { get; set; }

    /// <summary>
    ///     Gets or sets the value imbalance ratio between these users.
    /// </summary>
    public double ValueImbalanceRatio { get; set; }

    /// <summary>
    ///     Gets or sets the risk score for this relationship.
    /// </summary>
    public double RiskScore { get; set; }

    /// <summary>
    ///     Gets or sets the timestamp of the first trade.
    /// </summary>
    public DateTime FirstTradeTime { get; set; }

    /// <summary>
    ///     Gets or sets the timestamp of the last trade.
    /// </summary>
    public DateTime LastTradeTime { get; set; }

    /// <summary>
    ///     Gets or sets whether this relationship is flagged as suspicious.
    /// </summary>
    public bool IsSuspicious { get; set; }

    /// <summary>
    ///     Gets or sets additional flags for this relationship.
    /// </summary>
    public List<string> Flags { get; set; } = new();

    /// <summary>
    ///     Gets the duration of the trading relationship.
    /// </summary>
    public TimeSpan RelationshipDuration => LastTradeTime - FirstTradeTime;

    /// <summary>
    ///     Gets the trading frequency (trades per day).
    /// </summary>
    public double TradingFrequency => 
        RelationshipDuration.TotalDays > 0 ? TradeCount / RelationshipDuration.TotalDays : TradeCount;
}