namespace EeveeCore.Services.Network;

/// <summary>
///     Service for advanced network analysis and pattern detection in trade networks.
///     Implements sophisticated algorithms to detect coordinated fraud schemes.
/// </summary>
public class NetworkAnalysisService : INService
{
    private readonly LinqToDbConnectionProvider _dbProvider;
    private readonly TradeNetworkGraphService _graphService;
    private readonly IDataCache _cache;
    private readonly ILogger<NetworkAnalysisService> _logger;

    // Analysis thresholds
    private const double FunnelSuspicionThreshold = 0.7;
    private const double ClusterSuspicionThreshold = 0.6;
    private const int MinClusterSize = 3;
    private const double CircularFlowThreshold = 0.8;

    /// <summary>
    ///     Initializes a new instance of the <see cref="NetworkAnalysisService" /> class.
    /// </summary>
    /// <param name="dbProvider">The database context provider.</param>
    /// <param name="graphService">The trade network graph service.</param>
    /// <param name="cache">The cache service.</param>
    /// <param name="logger">The logger.</param>
    public NetworkAnalysisService(
        LinqToDbConnectionProvider dbProvider,
        TradeNetworkGraphService graphService,
        IDataCache cache,
        ILogger<NetworkAnalysisService> logger)
    {
        _dbProvider = dbProvider;
        _graphService = graphService;
        _cache = cache;
        _logger = logger;
    }


    /// <summary>
    ///     Detects funnel patterns where multiple accounts feed value to a central account.
    /// </summary>
    /// <param name="timeWindowDays">The time window for analysis.</param>
    /// <param name="minSources">Minimum number of source accounts to consider a funnel.</param>
    /// <returns>List of detected funnel patterns.</returns>
    public async Task<List<FunnelPattern>> DetectFunnelPatternsAsync(int timeWindowDays = 30, int minSources = 3)
    {
        var cacheKey = $"funnel_patterns:days_{timeWindowDays}:sources_{minSources}";
        var database = _cache.Redis.GetDatabase();
        
        var cached = await database.StringGetAsync(cacheKey);
        if (cached.HasValue)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<FunnelPattern>>((string)cached!)!;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize cached funnel patterns");
            }
        }

        var network = await _graphService.GetTradeNetworkAsync(timeWindowDays);
        var funnelPatterns = new List<FunnelPattern>();

        foreach (var node in network.Nodes.Values)
        {
            // Find incoming relationships (users giving value to this node)
            var incomingEdges = network.Edges
                .Where(e => e.ToUserId == node.UserId)
                .ToList();

            if (incomingEdges.Count < minSources) continue;

            // Calculate funnel metrics
            var totalIncomingValue = incomingEdges.Sum(e => e.TotalValue);
            var uniqueSources = incomingEdges.Select(e => e.FromUserId).Distinct().Count();
            var averageSourceValue = totalIncomingValue / uniqueSources;
            
            // Check for suspicious patterns
            var suspiciousIndicators = 0;
            var suspicionReasons = new List<string>();

            // High value concentration
            if (totalIncomingValue > 500000) // 500K+ concentrated value
            {
                suspiciousIndicators++;
                suspicionReasons.Add("High value concentration");
            }

            // Many small sources feeding one account
            if (uniqueSources >= 5 && averageSourceValue < 50000)
            {
                suspiciousIndicators++;
                suspicionReasons.Add("Many small sources");
            }

            // New accounts as sources
            var newAccountSources = incomingEdges
                .Count(e => network.Nodes.ContainsKey(e.FromUserId) && 
                            network.Nodes[e.FromUserId].AccountAgeDays < 30);
            
            if (newAccountSources > uniqueSources * 0.5)
            {
                suspiciousIndicators++;
                suspicionReasons.Add("Many new account sources");
            }

            // High imbalance ratio on incoming trades
            var highImbalanceCount = incomingEdges.Count(e => e.ValueImbalanceRatio > 3.0);
            if (highImbalanceCount > incomingEdges.Count * 0.7)
            {
                suspiciousIndicators++;
                suspicionReasons.Add("High value imbalance ratios");
            }

            // Calculate suspicion score
            var suspicionScore = Math.Min(1.0, suspiciousIndicators / 4.0);

            if (suspicionScore >= FunnelSuspicionThreshold)
            {
                funnelPatterns.Add(new FunnelPattern
                {
                    CentralUserId = node.UserId,
                    SourceUserIds = incomingEdges.Select(e => e.FromUserId).ToList(),
                    TotalValue = totalIncomingValue,
                    SourceCount = uniqueSources,
                    SuspicionScore = suspicionScore,
                    SuspicionReasons = suspicionReasons,
                    DetectedAt = DateTime.UtcNow,
                    TimeWindowDays = timeWindowDays
                });
            }
        }

        // Cache the results
        var serialized = System.Text.Json.JsonSerializer.Serialize(funnelPatterns);
        await database.StringSetAsync(cacheKey, serialized, TimeSpan.FromHours(2));

        return funnelPatterns.OrderByDescending(f => f.SuspicionScore).ToList();
    }

    /// <summary>
    ///     Detects clusters of accounts that appear to be coordinated.
    /// </summary>
    /// <param name="timeWindowDays">The time window for analysis.</param>
    /// <returns>List of detected account clusters.</returns>
    public async Task<List<AccountCluster>> DetectAccountClustersAsync(int timeWindowDays = 30)
    {
        var cacheKey = $"account_clusters:days_{timeWindowDays}";
        var database = _cache.Redis.GetDatabase();
        
        var cached = await database.StringGetAsync(cacheKey);
        if (cached.HasValue)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<AccountCluster>>((string)cached!)!;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize cached account clusters");
            }
        }

        var network = await _graphService.GetTradeNetworkAsync(timeWindowDays);
        var clusters = new List<AccountCluster>();

        // Find connected components using depth-first search
        var visited = new HashSet<ulong>();
        
        foreach (var startNode in network.Nodes.Keys)
        {
            if (visited.Contains(startNode)) continue;

            var cluster = new List<ulong>();
            var stack = new Stack<ulong>();
            stack.Push(startNode);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (visited.Contains(current)) continue;

                visited.Add(current);
                cluster.Add(current);

                // Add connected nodes
                var connectedNodes = network.Edges
                    .Where(e => (e.FromUserId == current || e.ToUserId == current) && 
                               e.RiskScore > 0.3) // Only follow suspicious edges
                    .SelectMany(e => new[] { e.FromUserId, e.ToUserId })
                    .Where(id => id != current && !visited.Contains(id));

                foreach (var connected in connectedNodes)
                {
                    stack.Push(connected);
                }
            }

            if (cluster.Count >= MinClusterSize)
            {
                var clusterAnalysis = AnalyzeCluster(cluster, network);
                if (clusterAnalysis.SuspicionScore >= ClusterSuspicionThreshold)
                {
                    clusters.Add(clusterAnalysis);
                }
            }
        }

        // Cache the results
        var serialized = System.Text.Json.JsonSerializer.Serialize(clusters);
        await database.StringSetAsync(cacheKey, serialized, TimeSpan.FromHours(2));

        return clusters.OrderByDescending(c => c.SuspicionScore).ToList();
    }

    /// <summary>
    ///     Detects circular trading patterns that might indicate value laundering.
    /// </summary>
    /// <param name="timeWindowDays">The time window for analysis.</param>
    /// <param name="maxPathLength">Maximum path length to search for cycles.</param>
    /// <returns>List of detected circular flows.</returns>
    public async Task<List<CircularFlow>> DetectCircularFlowsAsync(int timeWindowDays = 30, int maxPathLength = 5)
    {
        var cacheKey = $"circular_flows:days_{timeWindowDays}:length_{maxPathLength}";
        var database = _cache.Redis.GetDatabase();
        
        var cached = await database.StringGetAsync(cacheKey);
        if (cached.HasValue)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<CircularFlow>>((string)cached!)!;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize cached circular flows");
            }
        }

        var network = await _graphService.GetTradeNetworkAsync(timeWindowDays);
        var circularFlows = new List<CircularFlow>();

        // Find cycles using depth-first search with path tracking
        foreach (var startNode in network.Nodes.Keys)
        {
            var cycles = FindCyclesFromNode(startNode, network, maxPathLength);
            
            foreach (var cycle in cycles)
            {
                var flow = AnalyzeCircularFlow(cycle, network);
                if (flow.SuspicionScore >= CircularFlowThreshold)
                {
                    circularFlows.Add(flow);
                }
            }
        }

        // Remove duplicate cycles (same path in different direction)
        var uniqueFlows = RemoveDuplicateCircularFlows(circularFlows);

        // Cache the results
        var serialized = System.Text.Json.JsonSerializer.Serialize(uniqueFlows);
        await database.StringSetAsync(cacheKey, serialized, TimeSpan.FromHours(2));

        return uniqueFlows.OrderByDescending(f => f.SuspicionScore).ToList();
    }

    /// <summary>
    ///     Analyzes a cluster of accounts for suspicious patterns.
    /// </summary>
    /// <param name="userIds">The user IDs in the cluster.</param>
    /// <param name="network">The trade network.</param>
    /// <returns>Cluster analysis result.</returns>
    private AccountCluster AnalyzeCluster(List<ulong> userIds, TradeNetworkGraph network)
    {
        var suspiciousIndicators = 0;
        var suspicionReasons = new List<string>();

        // Check account age similarity
        var accountAges = userIds
            .Where(id => network.Nodes.ContainsKey(id))
            .Select(id => network.Nodes[id].AccountAgeDays)
            .ToList();

        if (accountAges.Any() && accountAges.Max() - accountAges.Min() < 7) // Created within a week
        {
            suspiciousIndicators++;
            suspicionReasons.Add("Similar account creation times");
        }

        // Check for high internal trading
        var internalEdges = network.Edges
            .Where(e => userIds.Contains(e.FromUserId) && userIds.Contains(e.ToUserId))
            .ToList();

        var totalEdges = network.Edges
            .Where(e => userIds.Contains(e.FromUserId) || userIds.Contains(e.ToUserId))
            .Count();

        if (totalEdges > 0 && (double)internalEdges.Count / totalEdges > 0.8)
        {
            suspiciousIndicators++;
            suspicionReasons.Add("High internal trading ratio");
        }

        // Check for coordinated timing
        var tradeTimes = internalEdges
            .SelectMany(e => new[] { e.FirstTradeTime, e.LastTradeTime })
            .OrderBy(t => t)
            .ToList();

        if (tradeTimes.Count >= 4)
        {
            var timeSpans = tradeTimes.Zip(tradeTimes.Skip(1), (a, b) => (b - a).TotalMinutes).ToList();
            var averageInterval = timeSpans.Average();
            var standardDeviation = Math.Sqrt(timeSpans.Select(t => Math.Pow(t - averageInterval, 2)).Average());
            
            if (standardDeviation < averageInterval * 0.2) // Very regular timing
            {
                suspiciousIndicators++;
                suspicionReasons.Add("Coordinated trading timing");
            }
        }

        // Check for new accounts
        var newAccountCount = userIds.Count(id => 
            network.Nodes.ContainsKey(id) && network.Nodes[id].AccountAgeDays < 30);
        
        if (newAccountCount > userIds.Count * 0.7)
        {
            suspiciousIndicators++;
            suspicionReasons.Add("Majority new accounts");
        }

        var suspicionScore = Math.Min(1.0, suspiciousIndicators / 4.0);
        var totalValue = internalEdges.Sum(e => e.TotalValue);

        return new AccountCluster
        {
            UserIds = userIds,
            SuspicionScore = suspicionScore,
            SuspicionReasons = suspicionReasons,
            InternalTradeCount = internalEdges.Count,
            TotalInternalValue = totalValue,
            DetectedAt = DateTime.UtcNow,
            TimeWindowDays = network.TimeWindowDays
        };
    }

    /// <summary>
    ///     Finds cycles starting from a specific node using depth-first search.
    /// </summary>
    /// <param name="startNode">The starting node.</param>
    /// <param name="network">The trade network.</param>
    /// <param name="maxLength">Maximum cycle length.</param>
    /// <returns>List of cycles found.</returns>
    private List<List<ulong>> FindCyclesFromNode(ulong startNode, TradeNetworkGraph network, int maxLength)
    {
        var cycles = new List<List<ulong>>();
        var visited = new HashSet<ulong>();
        var path = new List<ulong>();

        void DfsSearchCycles(ulong current, int depth)
        {
            if (depth > maxLength) return;
            
            path.Add(current);
            visited.Add(current);

            var neighbors = network.Edges
                .Where(e => e.FromUserId == current && e.RiskScore > 0.3)
                .Select(e => e.ToUserId)
                .ToList();

            foreach (var neighbor in neighbors)
            {
                if (neighbor == startNode && path.Count >= 3) // Found cycle back to start
                {
                    cycles.Add([..path]);
                }
                else if (!visited.Contains(neighbor))
                {
                    DfsSearchCycles(neighbor, depth + 1);
                }
            }

            path.RemoveAt(path.Count - 1);
            visited.Remove(current);
        }

        DfsSearchCycles(startNode, 0);
        return cycles;
    }

    /// <summary>
    ///     Analyzes a circular trading flow for suspicious patterns.
    /// </summary>
    /// <param name="cycle">The trading cycle path.</param>
    /// <param name="network">The trade network.</param>
    /// <returns>Circular flow analysis.</returns>
    private CircularFlow AnalyzeCircularFlow(List<ulong> cycle, TradeNetworkGraph network)
    {
        var suspiciousIndicators = 0;
        var suspicionReasons = new List<string>();
        var totalValue = 0m;

        // Analyze each edge in the cycle
        for (var i = 0; i < cycle.Count; i++)
        {
            var from = cycle[i];
            var to = cycle[(i + 1) % cycle.Count];
            
            var edge = network.Edges.FirstOrDefault(e => e.FromUserId == from && e.ToUserId == to);
            if (edge != null)
            {
                totalValue += edge.TotalValue;
                
                if (edge.ValueImbalanceRatio > 3.0)
                {
                    suspiciousIndicators++;
                }
            }
        }

        // Check for rapid cycling (short time between trades)
        var edgeTimes = new List<DateTime>();
        for (var i = 0; i < cycle.Count; i++)
        {
            var from = cycle[i];
            var to = cycle[(i + 1) % cycle.Count];
            
            var edge = network.Edges.FirstOrDefault(e => e.FromUserId == from && e.ToUserId == to);
            if (edge != null)
            {
                edgeTimes.Add(edge.LastTradeTime);
            }
        }

        if (edgeTimes.Count > 1)
        {
            var maxTimeSpan = edgeTimes.Max() - edgeTimes.Min();
            if (maxTimeSpan.TotalHours < 24) // Entire cycle completed within 24 hours
            {
                suspiciousIndicators++;
                suspicionReasons.Add("Rapid cycling within 24 hours");
            }
        }

        // Check for high total value
        if (totalValue > 100000)
        {
            suspiciousIndicators++;
            suspicionReasons.Add("High value circular flow");
        }

        // Check for new accounts in cycle
        var newAccountCount = cycle.Count(id => 
            network.Nodes.ContainsKey(id) && network.Nodes[id].AccountAgeDays < 30);
        
        if (newAccountCount > cycle.Count * 0.5)
        {
            suspiciousIndicators++;
            suspicionReasons.Add("Multiple new accounts involved");
        }

        var suspicionScore = Math.Min(1.0, suspiciousIndicators / 4.0);

        return new CircularFlow
        {
            Path = cycle,
            TotalValue = totalValue,
            SuspicionScore = suspicionScore,
            SuspicionReasons = suspicionReasons,
            DetectedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    ///     Removes duplicate circular flows (same cycle in different directions).
    /// </summary>
    /// <param name="flows">The list of circular flows.</param>
    /// <returns>Unique circular flows.</returns>
    private List<CircularFlow> RemoveDuplicateCircularFlows(List<CircularFlow> flows)
    {
        var unique = new List<CircularFlow>();
        var seen = new HashSet<string>();

        foreach (var flow in flows)
        {
            // Create a normalized representation of the cycle
            var sortedPath = flow.Path.OrderBy(id => id).ToList();
            var key = string.Join(",", sortedPath);

            if (seen.Add(key))
            {
                unique.Add(flow);
            }
        }

        return unique;
    }
}

/// <summary>
///     Represents a detected funnel pattern where multiple accounts feed value to a central account.
/// </summary>
public class FunnelPattern
{
    /// <summary>
    ///     Gets or sets the unique identifier for this flow.
    /// </summary>
    public string FlowId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Gets or sets the central user ID receiving value.
    /// </summary>
    public ulong CentralUserId { get; set; }

    /// <summary>
    ///     Gets or sets the list of source user IDs feeding value.
    /// </summary>
    public List<ulong> SourceUserIds { get; set; } = [];

    /// <summary>
    ///     Gets or sets the path of the flow (sequence of user IDs).
    /// </summary>
    public List<ulong> Path => [..SourceUserIds, CentralUserId];

    /// <summary>
    ///     Gets or sets the total value funneled.
    /// </summary>
    public decimal TotalValue { get; set; }

    /// <summary>
    ///     Gets or sets the number of unique source accounts.
    /// </summary>
    public int SourceCount { get; set; }

    /// <summary>
    ///     Gets the length of the flow path.
    /// </summary>
    public int PathLength => Path.Count;

    /// <summary>
    ///     Gets or sets the flow type.
    /// </summary>
    public string FlowType { get; set; } = "Funnel";

    /// <summary>
    ///     Gets or sets the suspicion score (0.0 to 1.0).
    /// </summary>
    public double SuspicionScore { get; set; }

    /// <summary>
    ///     Gets or sets the suspicion level classification.
    /// </summary>
    public string SuspicionLevel => SuspicionScore switch
    {
        > 0.8 => "Critical",
        > 0.6 => "High",
        > 0.4 => "Medium",
        _ => "Low"
    };

    /// <summary>
    ///     Gets or sets the reasons for suspicion.
    /// </summary>
    public List<string> SuspicionReasons { get; set; } = [];

    /// <summary>
    ///     Gets or sets the timestamp when the flow started.
    /// </summary>
    public DateTime? FlowStartTime { get; set; }

    /// <summary>
    ///     Gets or sets the timestamp when the flow completed.
    /// </summary>
    public DateTime? FlowEndTime { get; set; }

    /// <summary>
    ///     Gets or sets the duration of the complete flow.
    /// </summary>
    public TimeSpan? FlowDuration => FlowEndTime.HasValue && FlowStartTime.HasValue ? FlowEndTime - FlowStartTime : null;

    /// <summary>
    ///     Gets or sets when this pattern was detected.
    /// </summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>
    ///     Gets or sets whether this is a circular flow (returns to start).
    /// </summary>
    public bool IsCircular { get; set; } = false;

    /// <summary>
    ///     Gets or sets the velocity of the flow (value per day).
    /// </summary>
    public decimal FlowVelocity { get; set; }

    /// <summary>
    ///     Gets or sets the time window used for detection.
    /// </summary>
    public int TimeWindowDays { get; set; }

    /// <summary>
    ///     Gets or sets additional metadata about the flow.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
///     Represents a detected cluster of coordinated accounts.
/// </summary>
public class AccountCluster
{
    /// <summary>
    ///     Gets or sets the unique identifier for this cluster.
    /// </summary>
    public string ClusterId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Gets or sets the user IDs in the cluster.
    /// </summary>
    public List<ulong> UserIds { get; set; } = [];

    /// <summary>
    ///     Gets or sets the size of the cluster.
    /// </summary>
    public int Size => UserIds.Count;

    /// <summary>
    ///     Gets or sets the suspicion score (0.0 to 1.0).
    /// </summary>
    public double SuspicionScore { get; set; }

    /// <summary>
    ///     Gets or sets the suspicion level classification.
    /// </summary>
    public string SuspicionLevel => SuspicionScore switch
    {
        > 0.8 => "Critical",
        > 0.6 => "High",
        > 0.4 => "Medium",
        _ => "Low"
    };

    /// <summary>
    ///     Gets or sets the reasons for suspicion.
    /// </summary>
    public List<string> SuspicionReasons { get; set; } = [];

    /// <summary>
    ///     Gets or sets the number of internal trades within the cluster.
    /// </summary>
    public int InternalTradeCount { get; set; }

    /// <summary>
    ///     Gets or sets the total value traded internally.
    /// </summary>
    public decimal TotalInternalValue { get; set; }

    /// <summary>
    ///     Gets or sets the ratio of internal to external trades.
    /// </summary>
    public double InternalTradeRatio { get; set; }

    /// <summary>
    ///     Gets or sets the average account age of cluster members.
    /// </summary>
    public double AverageAccountAge { get; set; }

    /// <summary>
    ///     Gets or sets when this cluster was detected.
    /// </summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>
    ///     Gets or sets the time window used for detection.
    /// </summary>
    public int TimeWindowDays { get; set; }

    /// <summary>
    ///     Gets or sets additional metadata about the cluster.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
///     Represents a detected circular trading flow.
/// </summary>
public class CircularFlow
{
    /// <summary>
    ///     Gets or sets the unique identifier for this flow.
    /// </summary>
    public string FlowId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Gets or sets the path of the circular flow (sequence of user IDs).
    /// </summary>
    public List<ulong> Path { get; set; } = [];

    /// <summary>
    ///     Gets the length of the circular path.
    /// </summary>
    public int PathLength => Path.Count;

    /// <summary>
    ///     Gets or sets the total value in the circular flow.
    /// </summary>
    public decimal TotalValue { get; set; }

    /// <summary>
    ///     Gets or sets the flow type.
    /// </summary>
    public string FlowType { get; set; } = "Circular";

    /// <summary>
    ///     Gets or sets the suspicion score (0.0 to 1.0).
    /// </summary>
    public double SuspicionScore { get; set; }

    /// <summary>
    ///     Gets or sets the suspicion level classification.
    /// </summary>
    public string SuspicionLevel => SuspicionScore switch
    {
        > 0.8 => "Critical",
        > 0.6 => "High",
        > 0.4 => "Medium",
        _ => "Low"
    };

    /// <summary>
    ///     Gets or sets the reasons for suspicion.
    /// </summary>
    public List<string> SuspicionReasons { get; set; } = [];

    /// <summary>
    ///     Gets or sets the timestamp when the flow started.
    /// </summary>
    public DateTime? FlowStartTime { get; set; }

    /// <summary>
    ///     Gets or sets the timestamp when the flow completed.
    /// </summary>
    public DateTime? FlowEndTime { get; set; }

    /// <summary>
    ///     Gets or sets the duration of the complete flow.
    /// </summary>
    public TimeSpan? FlowDuration => FlowEndTime.HasValue && FlowStartTime.HasValue ? FlowEndTime - FlowStartTime : null;

    /// <summary>
    ///     Gets or sets when this flow was detected.
    /// </summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>
    ///     Gets or sets whether this is a circular flow (returns to start).
    /// </summary>
    public bool IsCircular { get; set; } = true;

    /// <summary>
    ///     Gets or sets the velocity of the flow (value per day).
    /// </summary>
    public decimal FlowVelocity { get; set; }

    /// <summary>
    ///     Gets or sets additional metadata about the flow.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}