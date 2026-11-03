using EeveeCore.Database.Linq.Models.Game;
using EeveeCore.Modules.Trade.Models;
using EeveeCore.Modules.Trade.Models.FraudDetection;
using LinqToDB;
using LinqToDB.Async;
using Serilog;
using TokenType = EeveeCore.Modules.Trade.Models.TokenType;

namespace EeveeCore.Modules.Trade.Services;

/// <summary>
/// Fraud detection service that combines all fraud detection capabilities
/// into a single, manageable service for detecting and preventing trading fraud. Unlike DOGE.
/// </summary>
public class FraudDetectionService : INService
{
    private readonly LinqToDbConnectionProvider _dbProvider;
    private readonly IDataCache _cache;

    #region Constants and Thresholds

    // Risk thresholds - aggressive blocking for fraud prevention
    private const double LogOnlyThreshold = 0.15;
    private const double ReviewThreshold = 0.25;
    private const double BlockThreshold = 0.4;    // Lower threshold for faster blocking
    private const double TempBanThreshold = 0.6;  // More aggressive banning
    private const double CriticalThreshold = 0.75; // Immediate ban + investigation

    // Value analysis constants
    private const decimal MinValueForAnalysis = 1000m;
    private const double ExtremeImbalanceThreshold = 0.85; // Force block for extreme imbalances
    private const double SuspiciousImbalanceThreshold = 50.0; // 50:1 ratio threshold

    // Pattern detection constants
    private const int ChainDepthLimit = 5;
    private const int BurstTradeWindow = 300; // 5 minutes
    private const int BurstTradeCount = 5;
    private const int NetworkSizeThreshold = 3;

    // Account age constants (in days)
    private const int NewAccountThreshold = 7;
    private const int SuspiciousAccountThreshold = 30;

    #endregion

    #region In-Memory Caches

    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, UserRiskProfile> _userRiskProfiles = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MarketManipulationIndicator> _marketIndicators = new();

    #endregion

    /// <summary>
    /// Initializes a new instance of the comprehensive fraud detection service.
    /// </summary>
    public FraudDetectionService(
        LinqToDbConnectionProvider dbProvider,
        IDataCache cache)
    {
        _dbProvider = dbProvider;
        _cache = cache;
    }

    #region Main Analysis Entry Point

    /// <summary>
    /// Performs comprehensive fraud analysis on a trade session.
    /// This is the main entry point called by the trade confirmation system.
    /// </summary>
    public async Task<FraudDetectionResult> AnalyzeTradeAsync(TradeSession session)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            Log.Information("🔍 Starting comprehensive fraud analysis for trade {TradeId} between {User1} and {User2}", 
                session.SessionId, session.Player1Id, session.Player2Id);

            // Step 1: Calculate trade values with detailed logging
            var senderValue = await CalculateUserTradeValueAsync(session, session.Player1Id);
            var receiverValue = await CalculateUserTradeValueAsync(session, session.Player2Id);
            
            Log.Information("💰 Trade values calculated: Sender={SenderValue:C}, Receiver={ReceiverValue:C}, Ratio={Ratio:F2}x", 
                senderValue, receiverValue, CalculateValueRatio(senderValue, receiverValue));

            // Step 2: Immediate blocking checks for extreme cases
            var immediateBlock = await CheckImmediateBlockConditionsAsync(session, senderValue, receiverValue);
            if (immediateBlock != null)
            {
                Log.Warning("🚫 IMMEDIATE BLOCK triggered for trade {TradeId}: {Reason}", 
                    session.SessionId, immediateBlock.Message);
                return immediateBlock;
            }

            // Step 3: Comprehensive fraud analysis
            var analysis = await PerformComprehensiveAnalysisAsync(session, senderValue, receiverValue);
            
            Log.Information("📊 Comprehensive analysis completed: Risk={RiskScore:P2}, Level={RiskLevel}", 
                analysis.ComprehensiveRiskScore, GetRiskLevel(analysis.ComprehensiveRiskScore));

            // Step 4: Determine and execute action
            var action = DetermineAutomatedAction(analysis.ComprehensiveRiskScore);
            var result = await ExecuteAutomatedActionAsync(session, analysis, action);

            // Step 5: ALWAYS log fraud detection to database for stats
            await LogFraudDetectionToDatabase(session, analysis, action, result);

            stopwatch.Stop();
            Log.Information("⏱️ Fraud analysis completed in {ElapsedMs}ms for trade {TradeId}", 
                stopwatch.ElapsedMilliseconds, session.SessionId);

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Critical error during fraud analysis for trade {TradeId}", session.SessionId);
            
            // Fail-safe: block the trade if analysis fails
            return new FraudDetectionResult
            {
                IsAllowed = false,
                RiskScore = 1.0,
                RiskLevel = RiskLevel.Critical,
                AutomatedAction = AutomatedAction.BlockTrade,
                Message = "Trade blocked due to fraud analysis error. Please contact an administrator.",
                FraudFlags = ["Analysis Error"],
                RequiresAdminReview = true
            };
        }
    }

    #endregion

    #region Immediate Block Conditions

    /// <summary>
    /// Checks for conditions that should immediately block a trade without detailed analysis.
    /// </summary>
    private async Task<FraudDetectionResult?> CheckImmediateBlockConditionsAsync(
        TradeSession session, decimal senderValue, decimal receiverValue)
    {
        // Check 1: User ban status
        var banCheck = await CheckUserBanStatusAsync(session.Player1Id, session.Player2Id);
        if (banCheck != null) return banCheck;

        // Check 2: Extreme value imbalance (like the 14856.64x case)
        var valueRatio = CalculateValueRatio(senderValue, receiverValue);
        if (valueRatio >= SuspiciousImbalanceThreshold)
        {
            Log.Warning("🚨 EXTREME VALUE IMBALANCE detected: {Ratio:F2}x (threshold: {Threshold}x)", 
                valueRatio, SuspiciousImbalanceThreshold);
            
            // Check 2.5: Return Trade Detection - is this a legitimate "give back"?
            var returnTradeCheck = await CheckForReturnTradeAsync(session, senderValue, receiverValue);
            if (returnTradeCheck.IsReturnTrade)
            {
                Log.Information("✅ RETURN TRADE detected: {Reason} - allowing extreme imbalance", returnTradeCheck.Reason);
                
                return new FraudDetectionResult
                {
                    IsAllowed = true,
                    RiskScore = 0.3, // Low risk for legitimate returns
                    RiskLevel = RiskLevel.Low,
                    AutomatedAction = AutomatedAction.LogOnly,
                    Message = $"Return trade detected: {returnTradeCheck.Reason}",
                    FraudFlags = ["Return Trade", "Extreme Imbalance Allowed"],
                    RequiresAdminReview = false
                };
            }
            
            await ApplyImmediateBansAsync(session.Player1Id, session.Player2Id, 
                $"Extreme value imbalance: {valueRatio:F2}x ratio");
            
            return new FraudDetectionResult
            {
                IsAllowed = false,
                RiskScore = 1.0,
                RiskLevel = RiskLevel.Critical,
                AutomatedAction = AutomatedAction.BlockTrade,
                Message = $"Trade blocked: Extreme value imbalance detected ({valueRatio:F2}x ratio). Both users have been banned.",
                FraudFlags = ["Extreme Value Imbalance", "Auto-Ban Applied"],
                RequiresAdminReview = true
            };
        }

        // Check 3: Whitelisted relationship (allow with minimal risk)
        var whitelistCheck = await CheckWhitelistedRelationshipAsync(session.Player1Id, session.Player2Id);
        if (whitelistCheck != null) return whitelistCheck;

        return null; // No immediate block needed
    }

    #endregion

    #region Value Calculation

    /// <summary>
    /// Calculates the total value of items a user is offering in a trade.
    /// </summary>
    public async Task<decimal> CalculateUserTradeValueAsync(TradeSession session, ulong userId)
    {
        decimal totalValue = 0;

        // Calculate Pokemon values
        foreach (var pokemonEntry in session.GetPokemonBy(userId))
        {
            if (pokemonEntry.PokemonId.HasValue)
            {
                var pokemonValue = await CalculatePokemonValueAsync(pokemonEntry.PokemonId.Value);
                totalValue += pokemonValue;
                Log.Debug("Pokemon {PokemonId} value: {Value:C}", pokemonEntry.PokemonId.Value, pokemonValue);
            }
        }

        // Add credits (1:1 value)
        var credits = session.GetCreditsBy(userId);
        totalValue += credits;
        if (credits > 0)
        {
            Log.Debug("Credits value: {Value:C}", credits);
        }

        // Add token values
        var tokens = session.GetTokensBy(userId);
        foreach (var (tokenType, count) in tokens)
        {
            var tokenValue = CalculateTokenValue(tokenType, count);
            totalValue += tokenValue;
            Log.Debug("Tokens {TokenType} x{Count} value: {Value:C}", tokenType, count, tokenValue);
        }

        Log.Debug("User {UserId} total trade value: {TotalValue:C}", userId, totalValue);
        return totalValue;
    }

    /// <summary>
    /// Calculates the estimated market value of a Pokemon.
    /// </summary>
    public async Task<decimal> CalculatePokemonValueAsync(ulong pokemonId)
    {
        await using var db = await _dbProvider.GetConnectionAsync();
        
        var pokemon = await db.UserPokemon
            .Where(p => p.Id == pokemonId)
            .Select(p => new
            {
                p.PokemonName,
                p.Level,
                p.Shiny,
                p.Radiant,
                p.Nature,
                p.HpIv,
                p.AttackIv,
                p.DefenseIv,
                p.SpecialAttackIv,
                p.SpecialDefenseIv,
                p.SpeedIv
            })
            .FirstOrDefaultAsync();

        if (pokemon == null) return 0;

        decimal baseValue = 1000; // Base Pokemon value

        // Level multiplier
        baseValue *= (1 + (pokemon.Level - 1) * 0.02m); // 2% per level above 1

        // Special form multipliers
        if (pokemon.Radiant == true)
        {
            baseValue *= 100m; // Radiant Pokemon are extremely valuable
        }
        else if (pokemon.Shiny == true)
        {
            baseValue *= 10m; // Shiny Pokemon are very valuable
        }

        // IV bonus (perfect IVs add significant value)
        var totalIvs = pokemon.HpIv + pokemon.AttackIv + pokemon.DefenseIv +
                      pokemon.SpecialAttackIv + pokemon.SpecialDefenseIv + pokemon.SpeedIv;
        var ivPercentage = totalIvs / 186.0; // Max possible IVs is 31*6=186
        baseValue *= (decimal)(1.0 + ivPercentage * 0.5); // Up to 50% bonus for perfect IVs

        // Species-specific multipliers (simplified)
        var speciesMultiplier = pokemon.PokemonName?.ToLower() switch
        {
            "arceus" => 5.0m,
            "mew" => 4.0m,
            "mewtwo" => 3.0m,
            "rayquaza" => 3.0m,
            "dialga" => 2.5m,
            "palkia" => 2.5m,
            "giratina" => 2.5m,
            _ => 1.0m
        };
        baseValue *= speciesMultiplier;

        return Math.Max(baseValue, 100); // Minimum value of 100
    }

    /// <summary>
    /// Calculates the value of tokens.
    /// </summary>
    private static decimal CalculateTokenValue(TokenType tokenType, int count)
    {
        // Base token values (can be adjusted based on market conditions)
        var baseValue = tokenType switch
        {
            TokenType.Dragon => 1500m,   // Dragon tokens are most valuable
            TokenType.Psychic => 1200m,  // Psychic tokens are valuable
            TokenType.Fairy => 1200m,    // Fairy tokens are valuable
            TokenType.Steel => 1000m,    // Steel tokens are solid value
            TokenType.Ghost => 1000m,    // Ghost tokens are solid value
            TokenType.Fire => 800m,      // Starter type tokens
            TokenType.Water => 800m,     // Starter type tokens  
            TokenType.Grass => 800m,     // Starter type tokens
            TokenType.Electric => 900m,  // Pikachu factor
            TokenType.Ice => 700m,       // Ice tokens
            TokenType.Dark => 700m,      // Dark tokens
            TokenType.Fighting => 600m,  // Fighting tokens
            TokenType.Flying => 600m,    // Flying tokens
            TokenType.Rock => 500m,      // Rock tokens
            TokenType.Ground => 500m,    // Ground tokens
            TokenType.Poison => 400m,    // Less valuable
            TokenType.Bug => 300m,       // Least valuable
            TokenType.Normal => 200m,    // Least valuable
            _ => 500m // Default fallback
        };

        return baseValue * count;
    }

    /// <summary>
    /// Calculates the value ratio between two amounts.
    /// </summary>
    private static double CalculateValueRatio(decimal value1, decimal value2)
    {
        if (value1 == 0 && value2 == 0) return 1.0;
        if (value2 == 0) return (double)value1;
        if (value1 == 0) return (double)value2;
        
        var ratio = Math.Max(value1, value2) / Math.Min(value1, value2);
        return (double)ratio;
    }

    /// <summary>
    /// Calculates value imbalance risk score from trade values.
    /// CRITICAL: This fixes the gift logic bug that was capping risk at 0.3 for large gifts.
    /// </summary>
    public static double CalculateValueImbalanceScore(decimal senderValue, decimal receiverValue)
    {
        // Handle zero-value cases (empty sides)
        if (senderValue == 0 && receiverValue == 0)
        {
            return 0.0; // No items being traded
        }

        // Calculate ratio (always >= 1.0)
        var ratio = CalculateValueRatio(senderValue, receiverValue);
        
        Log.Information("💡 Value imbalance calculation: ${Value1} vs ${Value2} = {Ratio:F2}x ratio", 
            senderValue, receiverValue, ratio);

        // AGGRESSIVE scaling for fraud prevention - no gift exceptions!
        var riskScore = ratio switch
        {
            >= 10000 => 1.0,    // 10000:1+ = maximum risk
            >= 5000 => 0.98,    // 5000:1 = near maximum  
            >= 2000 => 0.95,    // 2000:1 = very high (covers user's 1885.95x case)
            >= 1000 => 0.92,    // 1000:1 = very high risk
            >= 500 => 0.9,      // 500:1 = high risk
            >= 200 => 0.85,     // 200:1 = high risk
            >= 100 => 0.8,      // 100:1 = significant risk
            >= 50 => 0.75,      // 50:1 = significant risk
            >= 20 => 0.65,      // 20:1 = moderate-high risk
            >= 10 => 0.5,       // 10:1 = moderate risk
            >= 5 => 0.3,        // 5:1 = low-moderate risk
            >= 3 => 0.15,       // 3:1 = low risk
            >= 2 => 0.05,       // 2:1 = minimal risk
            _ => 0.0             // Balanced trades
        };

        Log.Information("🎯 Risk score calculated: {Ratio:F2}x ratio → {RiskScore:P2} risk", ratio, riskScore);
        
        return riskScore;
    }

    #endregion

    #region Comprehensive Analysis

    /// <summary>
    /// Performs comprehensive fraud analysis combining all detection methods.
    /// </summary>
    private async Task<ComprehensiveFraudAnalysis> PerformComprehensiveAnalysisAsync(
        TradeSession session, decimal senderValue, decimal receiverValue)
    {
        var analysis = new ComprehensiveFraudAnalysis
        {
            TradeSessionId = session.SessionId,
            AnalysisTimestamp = DateTime.UtcNow
        };

        try
        {
            // Basic risk analysis
            analysis.BasicRiskAnalysis = await AnalyzeBasicTradeRiskAsync(session, senderValue, receiverValue);
            
            // Advanced pattern detection (only if worth the computation cost)
            if (analysis.BasicRiskAnalysis.OverallRiskScore >= 0.3 || 
                Math.Max(senderValue, receiverValue) >= 10000)
            {
                // Run parallel fraud detection tasks
                var chainTask1 = DetectChainTradingAsync(session.Player1Id, TimeSpan.FromDays(7));
                var chainTask2 = DetectChainTradingAsync(session.Player2Id, TimeSpan.FromDays(7));
                var burstTask1 = DetectBurstTradingAsync(session.Player1Id);
                var burstTask2 = DetectBurstTradingAsync(session.Player2Id);
                var networkTask = DetectNetworkConnectionsAsync(session);
                var marketTask = DetectMarketManipulationAsync(session);

                await Task.WhenAll(chainTask1, chainTask2, burstTask1, burstTask2, networkTask, marketTask);
                
                // Combine results
                var chain1 = await chainTask1;
                var chain2 = await chainTask2;
                var burst1 = await burstTask1;
                var burst2 = await burstTask2;
                var network = await networkTask;
                var market = await marketTask;
                
                analysis.ChainTradingAnalysis = new[] { chain1, chain2 }.OrderByDescending(c => c.RiskScore).FirstOrDefault();
                analysis.BurstTradingAnalysis = new[] { burst1, burst2 }.OrderByDescending(b => b.RiskScore).FirstOrDefault();
                analysis.NetworkAnalysis = network;
                analysis.MarketManipulation = market;
            }

            // Calculate comprehensive risk score
            analysis.ComprehensiveRiskScore = CalculateComprehensiveRiskScore(analysis);
            
            // Generate actionable insights
            analysis.ActionableInsights = GenerateActionableInsights(analysis);
            
            // Determine recommended action
            analysis.RecommendedAction = DetermineRecommendedAction(analysis);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during comprehensive analysis for trade {TradeId}", session.SessionId);
            analysis.AnalysisErrors.Add($"Analysis error: {ex.Message}");
            
            // Fallback to basic analysis only
            if (analysis.BasicRiskAnalysis != null)
            {
                analysis.ComprehensiveRiskScore = analysis.BasicRiskAnalysis.OverallRiskScore;
            }
        }

        return analysis;
    }

    /// <summary>
    /// Analyzes basic trade risk factors.
    /// </summary>
    private async Task<TradeRiskAnalysis> AnalyzeBasicTradeRiskAsync(
        TradeSession session, decimal senderValue, decimal receiverValue)
    {
        var analysis = new TradeRiskAnalysis
        {
            TradeSessionId = session.SessionId,
            AnalysisTimestamp = DateTime.UtcNow,
            SenderTotalValue = senderValue,
            ReceiverTotalValue = receiverValue,
            ValueDifference = Math.Abs(senderValue - receiverValue),
            ValueRatio = CalculateValueRatio(senderValue, receiverValue)
        };

        // Calculate component risk scores
        analysis.ValueImbalanceScore = CalculateValueImbalanceScore(senderValue, receiverValue);
        analysis.RelationshipRiskScore = await CalculateRelationshipRiskAsync(session.Player1Id, session.Player2Id);
        analysis.BehavioralRiskScore = await CalculateBehavioralRiskAsync(session);
        analysis.AccountAgeRiskScore = CalculateAccountAgeRisk(session.Player1Id, session.Player2Id);

        // Calculate overall risk score with proper weighting
        analysis.OverallRiskScore = CalculateOverallRiskScore(analysis);

        // Set fraud flags
        await SetFraudFlagsAsync(analysis, session);

        Log.Information("🎯 Basic risk scores - Overall: {Overall:P2}, Value: {Value:P2}, Relationship: {Relationship:P2}, Behavioral: {Behavioral:P2}, AccountAge: {AccountAge:P2}",
            analysis.OverallRiskScore, analysis.ValueImbalanceScore, analysis.RelationshipRiskScore, 
            analysis.BehavioralRiskScore, analysis.AccountAgeRiskScore);

        return analysis;
    }

    #endregion

    #region Risk Scoring

    /// <summary>
    /// Calculates the overall risk score from component scores.
    /// </summary>
    private static double CalculateOverallRiskScore(TradeRiskAnalysis analysis)
    {
        // Weighted combination with emphasis on value imbalance
        var weights = new Dictionary<string, double>
        {
            ["ValueImbalance"] = 0.4,    // High weight for value imbalance
            ["Relationship"] = 0.25,     // Relationship patterns
            ["Behavioral"] = 0.2,        // Behavioral indicators
            ["AccountAge"] = 0.15        // Account age factors
        };

        var weightedScore = 
            (analysis.ValueImbalanceScore * weights["ValueImbalance"]) +
            (analysis.RelationshipRiskScore * weights["Relationship"]) +
            (analysis.BehavioralRiskScore * weights["Behavioral"]) +
            (analysis.AccountAgeRiskScore * weights["AccountAge"]);

        return Math.Min(1.0, weightedScore);
    }

    /// <summary>
    /// Calculates comprehensive risk score with extreme case handling.
    /// </summary>
    private static double CalculateComprehensiveRiskScore(ComprehensiveFraudAnalysis analysis)
    {
        var basicAnalysis = analysis.BasicRiskAnalysis;
        if (basicAnalysis == null) return 0.0;

        // CRITICAL: Handle extreme value imbalances
        if (basicAnalysis.ValueImbalanceScore >= ExtremeImbalanceThreshold)
        {
            Log.Warning("🚨 Extreme value imbalance override: {Score:P2} >= {Threshold:P2}", 
                basicAnalysis.ValueImbalanceScore, ExtremeImbalanceThreshold);
            return Math.Max(0.9, basicAnalysis.ValueImbalanceScore); // Force near-maximum risk
        }

        // Handle multiple fraud flags
        var flagCount = GetFraudFlagCount(basicAnalysis);
        if (flagCount >= 3)
        {
            Log.Warning("🚩 Multiple fraud flags detected: {Count} flags", flagCount);
            return Math.Max(0.8, basicAnalysis.OverallRiskScore);
        }

        // Standard comprehensive scoring
        var scores = new List<(double score, double weight)>
        {
            (basicAnalysis.OverallRiskScore, 0.5), // Increased weight for basic analysis
            (analysis.ChainTradingAnalysis?.RiskScore ?? 0, 0.15),
            (analysis.BurstTradingAnalysis?.RiskScore ?? 0, 0.15),
            (analysis.NetworkAnalysis?.NetworkRiskScore ?? 0, 0.1),
            (analysis.MarketManipulation?.RiskScore ?? 0, 0.1)
        };

        var weightedSum = scores.Sum(s => s.score * s.weight);
        var totalWeight = scores.Sum(s => s.weight);
        
        var comprehensiveScore = totalWeight > 0 ? weightedSum / totalWeight : 0;

        // Boost for multiple fraud types detected
        var detectedTypes = CountDetectedFraudTypes(analysis);
        if (detectedTypes >= 2)
        {
            comprehensiveScore = Math.Min(1.0, comprehensiveScore * 1.3);
            Log.Information("📈 Risk score boosted for {Count} detected fraud types", detectedTypes);
        }

        return comprehensiveScore;
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Checks if users are banned.
    /// </summary>
    private async Task<FraudDetectionResult?> CheckUserBanStatusAsync(ulong user1Id, ulong user2Id)
    {
        await using var db = await _dbProvider.GetConnectionAsync();
        
        var users = await db.Users
            .Where(u => u.UserId == user1Id || u.UserId == user2Id)
            .Select(u => new { u.UserId, u.TradeBanned, u.MarketBanned, u.TradeBanReason })
            .ToListAsync();

        var bannedUser = users.FirstOrDefault(u => u.TradeBanned == true || u.MarketBanned == true);
        if (bannedUser != null)
        {
            Log.Warning("🚫 Trade blocked: User {UserId} is banned - {Reason}", 
                bannedUser.UserId, bannedUser.TradeBanReason ?? "No reason specified");
            
            return new FraudDetectionResult
            {
                IsAllowed = false,
                RiskScore = 1.0,
                RiskLevel = RiskLevel.Critical,
                AutomatedAction = AutomatedAction.BlockTrade,
                Message = "Trade blocked: One or both users are banned from trading.",
                FraudFlags = ["User Banned"],
                RequiresAdminReview = false
            };
        }

        return null;
    }

    /// <summary>
    /// Checks if this trade appears to be a legitimate return of previously traded Pokemon.
    /// This prevents legitimate "give back" scenarios from being flagged as extreme fraud.
    /// </summary>
    private async Task<ReturnTradeCheckResult> CheckForReturnTradeAsync(TradeSession session, decimal senderValue, decimal receiverValue)
    {
        try
        {
            await using var db = await _dbProvider.GetConnectionAsync();
            
            // Look back 30 days for previous trades between these users
            var cutoffDate = DateTime.UtcNow.AddDays(-30);
            
            // Find recent trades where roles were reversed (current receiver was previous sender)
            var previousTrades = await db.TradeLogs
                .Where(t => t.Time >= cutoffDate && 
                           ((t.SenderId == session.Player2Id && t.ReceiverId == session.Player1Id) ||
                            (t.SenderId == session.Player1Id && t.ReceiverId == session.Player2Id)))
                .OrderByDescending(t => t.Time)
                .Take(20) // Limit to prevent performance issues
                .ToListAsync();

            if (!previousTrades.Any())
            {
                return new ReturnTradeCheckResult
                {
                    IsReturnTrade = false,
                    Reason = "No recent trade history found",
                    ConfidenceLevel = 0.0
                };
            }

            // Check if any Pokemon being traded match Pokemon from previous trades
            var currentPokemonIds = new List<ulong>();
            
            // Get Pokemon IDs from current trade session by filtering TradeEntries
            var player1Pokemon = session.TradeEntries
                .Where(e => e.OfferedBy == session.Player1Id && e.ItemType == TradeItemType.Pokemon && e.PokemonId.HasValue)
                .Select(e => e.PokemonId!.Value);
            currentPokemonIds.AddRange(player1Pokemon);
            
            var player2Pokemon = session.TradeEntries
                .Where(e => e.OfferedBy == session.Player2Id && e.ItemType == TradeItemType.Pokemon && e.PokemonId.HasValue)
                .Select(e => e.PokemonId!.Value);
            currentPokemonIds.AddRange(player2Pokemon);

            if (!currentPokemonIds.Any())
            {
                // If no Pokemon in current trade, might be credits-only return
                var recentTradesWithCredits = previousTrades
                    .Where(t => (t.SenderCredits ?? 0) > 0 || (t.ReceiverCredits ?? 0) > 0)
                    .ToList();

                if (recentTradesWithCredits.Any())
                {
                    var lastTrade = recentTradesWithCredits.First();
                    var daysSince = lastTrade.Time.HasValue ? (DateTime.UtcNow - lastTrade.Time.Value).Days : 0;
                    return new ReturnTradeCheckResult
                    {
                        IsReturnTrade = true,
                        Reason = $"Credits-only return trade detected (last trade {daysSince} days ago)",
                        DaysSinceOriginalTrade = daysSince,
                        ConfidenceLevel = 0.7 // Medium confidence for credits-only
                    };
                }
            }

            // For Pokemon trades, check if current Pokemon were involved in previous trades
            var matchingTradeCount = 0;
            var mostRecentMatchingTrade = DateTime.MinValue;

            foreach (var trade in previousTrades)
            {
                // This is simplified - in a real implementation, you'd parse the trade data
                // to extract Pokemon IDs from the trade log
                
                // For now, we'll use a heuristic based on trade direction and timing
                var wasRoleReversed = (trade.SenderId == session.Player2Id && trade.ReceiverId == session.Player1Id);
                if (wasRoleReversed && trade.Time.HasValue)
                {
                    matchingTradeCount++;
                    if (trade.Time.Value > mostRecentMatchingTrade)
                        mostRecentMatchingTrade = trade.Time.Value;
                }
            }

            var daysSinceLastTrade = mostRecentMatchingTrade == DateTime.MinValue ? 0 : 
                (DateTime.UtcNow - mostRecentMatchingTrade).Days;

            // Determine if this looks like a return trade
            var isReturnTrade = false;
            var confidence = 0.0;
            var reason = "";

            if (matchingTradeCount > 0 && daysSinceLastTrade <= 7)
            {
                // Recent trade with role reversal - high confidence return
                isReturnTrade = true;
                confidence = 0.9;
                reason = $"Recent role-reversed trade detected ({daysSinceLastTrade} days ago)";
            }
            else if (matchingTradeCount > 0 && daysSinceLastTrade <= 30)
            {
                // Older trade with role reversal - medium confidence return
                isReturnTrade = true;
                confidence = 0.6;
                reason = $"Role-reversed trade detected ({daysSinceLastTrade} days ago)";
            }
            else if (previousTrades.Count >= 3)
            {
                // Multiple recent trades between users - possible legitimate return
                isReturnTrade = true;
                confidence = 0.5;
                reason = $"Frequent trading relationship detected ({previousTrades.Count} recent trades)";
            }

            return new ReturnTradeCheckResult
            {
                IsReturnTrade = isReturnTrade,
                Reason = reason,
                MatchingPokemonCount = matchingTradeCount,
                DaysSinceOriginalTrade = daysSinceLastTrade,
                ConfidenceLevel = confidence
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking for return trade pattern");
            return new ReturnTradeCheckResult
            {
                IsReturnTrade = false,
                Reason = "Error during return trade analysis",
                ConfidenceLevel = 0.0
            };
        }
    }

    /// <summary>
    /// Checks if relationship is whitelisted.
    /// </summary>
    private async Task<FraudDetectionResult?> CheckWhitelistedRelationshipAsync(ulong user1Id, ulong user2Id)
    {
        var (lowerId, higherId) = user1Id < user2Id ? (user1Id, user2Id) : (user2Id, user1Id);
        
        await using var db = await _dbProvider.GetConnectionAsync();
        var relationship = await db.UserTradeRelationships
            .Where(r => r.User1Id == lowerId && r.User2Id == higherId && r.Whitelisted == true)
            .FirstOrDefaultAsync();

        if (relationship != null)
        {
            Log.Information("✅ Whitelisted relationship detected between {User1} and {User2}", user1Id, user2Id);
            
            return new FraudDetectionResult
            {
                IsAllowed = true,
                RiskScore = 0.05,
                RiskLevel = RiskLevel.Minimal,
                AutomatedAction = AutomatedAction.LogOnly,
                Message = null,
                FraudFlags = ["Whitelisted Relationship"],
                RequiresAdminReview = false
            };
        }

        return null;
    }

    /// <summary>
    /// Gets risk level from score.
    /// </summary>
    private static RiskLevel GetRiskLevel(double score)
    {
        return score switch
        {
            >= 0.8 => RiskLevel.Critical,
            >= 0.6 => RiskLevel.High,
            >= 0.4 => RiskLevel.Medium,
            >= 0.2 => RiskLevel.Low,
            _ => RiskLevel.Minimal
        };
    }

    /// <summary>
    /// Counts fraud flags.
    /// </summary>
    private static int GetFraudFlagCount(TradeRiskAnalysis analysis)
    {
        var count = 0;
        if (analysis.FlaggedAltAccount) count++;
        if (analysis.FlaggedRmt) count++;
        if (analysis.FlaggedNewbieExploitation) count++;
        if (analysis.FlaggedUnusualBehavior) count++;
        if (analysis.FlaggedBotActivity) count++;
        return count;
    }

    /// <summary>
    /// Counts detected fraud types.
    /// </summary>
    private static int CountDetectedFraudTypes(ComprehensiveFraudAnalysis analysis)
    {
        var count = 0;
        if ((analysis.ChainTradingAnalysis?.RiskScore ?? 0) > 0.5) count++;
        if ((analysis.BurstTradingAnalysis?.RiskScore ?? 0) > 0.5) count++;
        if ((analysis.NetworkAnalysis?.NetworkRiskScore ?? 0) > 0.5) count++;
        if ((analysis.MarketManipulation?.RiskScore ?? 0) > 0.5) count++;
        return count;
    }

    #endregion

    #region Placeholder Methods (simplified for consolidation)

    /// <summary>
    /// Calculates relationship risk including pool/alt account detection.
    /// This is CRITICAL for detecting single users controlling multiple accounts.
    /// </summary>
    private async Task<double> CalculateRelationshipRiskAsync(ulong user1Id, ulong user2Id)
    {
        try
        {
            await using var db = await _dbProvider.GetConnectionAsync();
            var lookbackTime = DateTime.UtcNow.AddDays(-30);

            var riskFactors = new List<double>();

            // Detection 1: Pool Account Detection - CRITICAL for alt account detection
            var poolRisk = await DetectPoolAccountsAsync(db, user1Id, user2Id, lookbackTime);
            riskFactors.Add(poolRisk * 1.5); // Weight heavily - this is the most important

            // Detection 2: Trading Frequency Correlation
            var frequencyRisk = await DetectTradingFrequencyCorrelationAsync(db, user1Id, user2Id, lookbackTime);
            riskFactors.Add(frequencyRisk);

            // Detection 3: Value Funneling Detection
            var valueRisk = await DetectValueFunnelingAsync(db, user1Id, user2Id, lookbackTime);
            riskFactors.Add(valueRisk * 1.3); // High weight for value funneling

            // Detection 4: Behavioral Coordination
            var coordinationRisk = await DetectBehavioralCoordinationAsync(db, user1Id, user2Id, lookbackTime);
            riskFactors.Add(coordinationRisk);

            Log.Information("🔍 Relationship risk analysis: Pool={Pool:P2}, Frequency={Freq:P2}, Value={Value:P2}, Coordination={Coord:P2}", 
                poolRisk, frequencyRisk, valueRisk, coordinationRisk);

            return Math.Min(riskFactors.Average(), 1.0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error calculating relationship risk for users {User1} and {User2}", user1Id, user2Id);
            return 0.0;
        }
    }

    /// <summary>
    /// CRITICAL: Detects if accounts are controlled by the same person (pool/alt accounts).
    /// This is the most important fraud detection for preventing multi-account abuse.
    /// </summary>
    private async Task<double> DetectPoolAccountsAsync(DittoDataConnection db, ulong user1Id, ulong user2Id, DateTime since)
    {
        var poolIndicators = new List<double>();

        // Indicator 1: One-sided value flow (main account → alt account pattern)
        var trades = await db.TradeLogs
            .Where(t => ((t.SenderId == user1Id && t.ReceiverId == user2Id) ||
                        (t.SenderId == user2Id && t.ReceiverId == user1Id)) &&
                       t.Time >= since)
            .Select(t => new
            {
                FromUser1 = t.SenderId == user1Id,
                SenderValue = (t.SenderCredits ?? 0) + (long)(t.SenderRedeems * 1000), // Estimate token value
                ReceiverValue = (t.ReceiverCredits ?? 0) + (long)(t.ReceiverRedeems * 1000)
            })
            .ToListAsync();

        if (trades.Count > 0)
        {
            var user1ToUser2Value = trades.Where(t => t.FromUser1).Sum(t => t.SenderValue);
            var user2ToUser1Value = trades.Where(t => !t.FromUser1).Sum(t => t.SenderValue);
            var totalValue = user1ToUser2Value + user2ToUser1Value;

            if (totalValue > 0)
            {
                var imbalance = Math.Abs(user1ToUser2Value - user2ToUser1Value) / (double)totalValue;
                poolIndicators.Add(imbalance); // High imbalance = likely alt account funding
            }
        }

        // Indicator 2: Account creation timing correlation
        var user1Creation = ExtractAccountCreationTime(user1Id);
        var user2Creation = ExtractAccountCreationTime(user2Id);
        var creationTimeDiff = Math.Abs((user1Creation - user2Creation).TotalDays);
        var creationCorrelation = creationTimeDiff < 7 ? 0.8 : // Created within 1 week = high risk
                                 creationTimeDiff < 30 ? 0.4 : // Within 1 month = medium risk
                                 0.0; // Older = lower risk
        poolIndicators.Add(creationCorrelation);

        // Indicator 3: Mutual exclusive trading (accounts never active simultaneously)
        var mutualExclusivity = await DetectMutualExclusiveTradingAsync(db, user1Id, user2Id, since);
        poolIndicators.Add(mutualExclusivity);

        // Indicator 4: Resource hoarding pattern (one account accumulates, other provides)
        var hoardingPattern = await DetectResourceHoardingPatternAsync(db, user1Id, user2Id, since);
        poolIndicators.Add(hoardingPattern);

        return poolIndicators.Count > 0 ? poolIndicators.Average() : 0.0;
    }

    /// <summary>
    /// Extracts account creation time from Discord snowflake ID.
    /// </summary>
    private DateTime ExtractAccountCreationTime(ulong userId)
    {
        const long discordEpoch = 1420070400000L; // Discord epoch in milliseconds
        var timestamp = (userId >> 22) + discordEpoch;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp).DateTime;
    }

    /// <summary>
    /// Detects if accounts trade in mutually exclusive time windows (never simultaneously active).
    /// </summary>
    private async Task<double> DetectMutualExclusiveTradingAsync(DittoDataConnection db, ulong user1Id, ulong user2Id, DateTime since)
    {
        // Get trading times for both users
        var user1Times = await db.TradeLogs
            .Where(t => (t.SenderId == user1Id || t.ReceiverId == user1Id) && t.Time >= since)
            .Select(t => t.Time ?? DateTime.MinValue)
            .ToListAsync();

        var user2Times = await db.TradeLogs
            .Where(t => (t.SenderId == user2Id || t.ReceiverId == user2Id) && t.Time >= since)
            .Select(t => t.Time ?? DateTime.MinValue)
            .ToListAsync();

        if (user1Times.Count < 3 || user2Times.Count < 3) return 0.0;

        // Check for overlapping activity windows (30-minute windows)
        var overlaps = 0;
        var totalWindows = 0;

        foreach (var time1 in user1Times)
        {
            totalWindows++;
            var window = TimeSpan.FromMinutes(30);
            var hasOverlap = user2Times.Any(time2 => Math.Abs((time1 - time2).TotalMinutes) <= 30);
            if (hasOverlap) overlaps++;
        }

        var mutualExclusivity = totalWindows > 0 ? 1.0 - ((double)overlaps / totalWindows) : 0.0;
        return mutualExclusivity; // High value = accounts never active together = suspicious
    }

    /// <summary>
    /// Detects resource hoarding patterns (one account accumulates wealth, other provides resources).
    /// </summary>
    private async Task<double> DetectResourceHoardingPatternAsync(DittoDataConnection db, ulong user1Id, ulong user2Id, DateTime since)
    {
        // Get net resource flow between accounts
        var resourceFlows = await db.TradeLogs
            .Where(t => ((t.SenderId == user1Id && t.ReceiverId == user2Id) ||
                        (t.SenderId == user2Id && t.ReceiverId == user1Id)) &&
                       t.Time >= since)
            .Select(t => new
            {
                ToUser2 = t.SenderId == user1Id,
                NetValue = (t.SenderCredits ?? 0) - (t.ReceiverCredits ?? 0)
            })
            .ToListAsync();

        if (resourceFlows.Count == 0) return 0.0;

        // Calculate net flow direction and consistency
        var netFlowToUser2 = resourceFlows.Sum(f => f.ToUser2 ? f.NetValue : -f.NetValue);
        var totalAbsoluteFlow = resourceFlows.Sum(f => Math.Abs(f.NetValue));

        if (totalAbsoluteFlow == 0) return 0.0;

        var flowDirectionality = Math.Abs(netFlowToUser2) / (double)totalAbsoluteFlow;
        
        // High directionality = one account consistently receives resources = hoarding pattern
        return Math.Min(flowDirectionality * 1.5, 1.0);
    }

    /// <summary>
    /// Detects trading frequency correlation between accounts (similar trading patterns).
    /// </summary>
    private async Task<double> DetectTradingFrequencyCorrelationAsync(DittoDataConnection db, ulong user1Id, ulong user2Id, DateTime since)
    {
        // Get trading frequencies for both users by day
        var user1Frequency = await GetDailyTradingFrequencyAsync(db, user1Id, since);
        var user2Frequency = await GetDailyTradingFrequencyAsync(db, user2Id, since);

        if (user1Frequency.Count < 7 || user2Frequency.Count < 7) return 0.0; // Need at least a week

        // Calculate correlation between trading patterns
        var correlation = CalculateFrequencyCorrelation(user1Frequency, user2Frequency);
        return Math.Max(0.0, correlation); // Only positive correlations are suspicious
    }

    /// <summary>
    /// Gets daily trading frequency for a user.
    /// </summary>
    private async Task<List<int>> GetDailyTradingFrequencyAsync(DittoDataConnection db, ulong userId, DateTime since)
    {
        var dailyCounts = await db.TradeLogs
            .Where(t => (t.SenderId == userId || t.ReceiverId == userId) && t.Time >= since)
            .GroupBy(t => (t.Time ?? DateTime.MinValue).Date)
            .Select(g => g.Count())
            .ToListAsync();

        return dailyCounts;
    }

    /// <summary>
    /// Calculates correlation between two frequency patterns.
    /// </summary>
    private double CalculateFrequencyCorrelation(List<int> freq1, List<int> freq2)
    {
        if (freq1.Count != freq2.Count || freq1.Count == 0) return 0.0;

        var mean1 = freq1.Average();
        var mean2 = freq2.Average();

        var numerator = freq1.Zip(freq2, (f1, f2) => (f1 - mean1) * (f2 - mean2)).Sum();
        var denominator1 = Math.Sqrt(freq1.Sum(f => Math.Pow(f - mean1, 2)));
        var denominator2 = Math.Sqrt(freq2.Sum(f => Math.Pow(f - mean2, 2)));

        if (denominator1 == 0 || denominator2 == 0) return 0.0;

        return numerator / (denominator1 * denominator2);
    }

    /// <summary>
    /// Detects value funneling patterns (systematic transfer of wealth).
    /// </summary>
    private async Task<double> DetectValueFunnelingAsync(DittoDataConnection db, ulong user1Id, ulong user2Id, DateTime since)
    {
        // Look for systematic one-way value transfers
        var allTrades = await db.TradeLogs
            .Where(t => ((t.SenderId == user1Id && t.ReceiverId == user2Id) ||
                        (t.SenderId == user2Id && t.ReceiverId == user1Id)) &&
                       t.Time >= since)
            .OrderBy(t => t.Time)
            .Select(t => new {
                Time = t.Time ?? DateTime.MinValue,
                FromUser1 = t.SenderId == user1Id,
                SenderValue = (double)((t.SenderCredits ?? 0) + ((long)t.SenderRedeems * 1000)),
                ReceiverValue = (double)((t.ReceiverCredits ?? 0) + ((long)t.ReceiverRedeems * 1000))
            })
            .ToListAsync();

        if (allTrades.Count < 3) return 0.0;

        // Calculate value flow over time
        var runningBalance = 0.0;
        var balanceChanges = new List<double>();

        foreach (var trade in allTrades)
        {
            var netChange = trade.FromUser1 ? 
                (trade.SenderValue - trade.ReceiverValue) : 
                (trade.ReceiverValue - trade.SenderValue);
            
            runningBalance += netChange;
            balanceChanges.Add(Math.Abs(runningBalance));
        }

        // Increasing balance = systematic funneling
        var trend = CalculateTrend(balanceChanges);
        return Math.Min(trend, 1.0);
    }

    /// <summary>
    /// Calculates trend in a series of values (positive = increasing).
    /// </summary>
    private double CalculateTrend(List<double> values)
    {
        if (values.Count < 3) return 0.0;

        var n = values.Count;
        var sumX = Enumerable.Range(0, n).Sum();
        var sumY = values.Sum();
        var sumXY = values.Select((y, i) => i * y).Sum();
        var sumX2 = Enumerable.Range(0, n).Sum(i => i * i);

        var slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
        return Math.Max(0.0, slope / values.Average()); // Normalize by average value
    }

    /// <summary>
    /// Detects behavioral coordination between accounts (similar timing patterns).
    /// </summary>
    private async Task<double> DetectBehavioralCoordinationAsync(DittoDataConnection db, ulong user1Id, ulong user2Id, DateTime since)
    {
        var coordinationFactors = new List<double>();

        // Factor 1: Similar trading time patterns (hour of day)
        var timeCorrelation = await DetectTimingCorrelationAsync(db, user1Id, user2Id, since);
        coordinationFactors.Add(timeCorrelation);

        // Factor 2: Synchronized activity spikes
        var activitySync = await DetectActivitySynchronizationAsync(db, user1Id, user2Id, since);
        coordinationFactors.Add(activitySync);

        // Factor 3: Sequential trading patterns (one trades, then the other)
        var sequentialPattern = await DetectSequentialTradingAsync(db, user1Id, user2Id, since);
        coordinationFactors.Add(sequentialPattern);

        return coordinationFactors.Average();
    }

    /// <summary>
    /// Detects timing correlation in trading patterns.
    /// </summary>
    private async Task<double> DetectTimingCorrelationAsync(DittoDataConnection db, ulong user1Id, ulong user2Id, DateTime since)
    {
        // Get trading hours for both users
        var user1Hours = await GetTradingHoursAsync(db, user1Id, since);
        var user2Hours = await GetTradingHoursAsync(db, user2Id, since);

        if (user1Hours.Count < 5 || user2Hours.Count < 5) return 0.0;

        // Create hour frequency distributions (0-23)
        var hourFreq1 = new int[24];
        var hourFreq2 = new int[24];

        foreach (var hour in user1Hours) hourFreq1[hour]++;
        foreach (var hour in user2Hours) hourFreq2[hour]++;

        // Calculate correlation between hour patterns
        return CalculateFrequencyCorrelation(hourFreq1.ToList(), hourFreq2.ToList());
    }

    /// <summary>
    /// Gets trading hours for a user.
    /// </summary>
    private async Task<List<int>> GetTradingHoursAsync(DittoDataConnection db, ulong userId, DateTime since)
    {
        var hours = await db.TradeLogs
            .Where(t => (t.SenderId == userId || t.ReceiverId == userId) && t.Time >= since)
            .Select(t => (t.Time ?? DateTime.MinValue).Hour)
            .ToListAsync();

        return hours;
    }

    /// <summary>
    /// Detects activity synchronization between accounts.
    /// </summary>
    private async Task<double> DetectActivitySynchronizationAsync(DittoDataConnection db, ulong user1Id, ulong user2Id, DateTime since)
    {
        // Get daily activity counts
        var user1Activity = await GetDailyTradingFrequencyAsync(db, user1Id, since);
        var user2Activity = await GetDailyTradingFrequencyAsync(db, user2Id, since);

        return Math.Abs(CalculateFrequencyCorrelation(user1Activity, user2Activity));
    }

    /// <summary>
    /// Detects sequential trading patterns (one account trades, then the other).
    /// </summary>
    private async Task<double> DetectSequentialTradingAsync(DittoDataConnection db, ulong user1Id, ulong user2Id, DateTime since)
    {
        // Get all trading times for both users
        var allTrades = await db.TradeLogs
            .Where(t => (t.SenderId == user1Id || t.ReceiverId == user1Id ||
                        t.SenderId == user2Id || t.ReceiverId == user2Id) && 
                       t.Time >= since)
            .OrderBy(t => t.Time)
            .Select(t => new {
                Time = t.Time ?? DateTime.MinValue,
                IsUser1 = t.SenderId == user1Id || t.ReceiverId == user1Id
            })
            .ToListAsync();

        if (allTrades.Count < 10) return 0.0;

        // Look for alternating patterns
        var sequences = 0;
        var alternations = 0;

        for (int i = 1; i < allTrades.Count; i++)
        {
            sequences++;
            if (allTrades[i].IsUser1 != allTrades[i-1].IsUser1)
            {
                alternations++;
            }
        }

        var alternationRatio = (double)alternations / sequences;
        
        // High alternation = suspicious sequential trading
        return alternationRatio > 0.7 ? Math.Min((alternationRatio - 0.5) * 2.0, 1.0) : 0.0;
    }
    /// <summary>
    /// Calculates behavioral risk based on trading patterns, timing, and automation indicators.
    /// This is CRITICAL for detecting bot activity and unusual behavior patterns.
    /// </summary>
    private async Task<double> CalculateBehavioralRiskAsync(TradeSession session)
    {
        try
        {
            await using var db = await _dbProvider.GetConnectionAsync();
            var lookbackTime = DateTime.UtcNow.AddDays(-14); // Analyze last 2 weeks
            var riskFactors = new List<double>();

            // Analyze both users
            foreach (var userId in new[] { session.Player1Id, session.Player2Id })
            {
                var userRisk = await AnalyzeUserBehavioralRiskAsync(db, userId, lookbackTime);
                riskFactors.Add(userRisk);
            }

            var avgRisk = riskFactors.Average();
            
            Log.Information("🧠 Behavioral risk analysis: User1={User1Risk:P2}, User2={User2Risk:P2}, Average={AvgRisk:P2}",
                riskFactors[0], riskFactors[1], avgRisk);

            return avgRisk;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error calculating behavioral risk for trade {TradeId}", session.SessionId);
            return 0.0;
        }
    }

    /// <summary>
    /// Analyzes behavioral risk for a single user.
    /// </summary>
    private async Task<double> AnalyzeUserBehavioralRiskAsync(DittoDataConnection db, ulong userId, DateTime since)
    {
        var behaviorFactors = new List<double>();

        // Factor 1: Trading frequency analysis (bot detection)
        var tradingTimes = await db.TradeLogs
            .Where(t => (t.SenderId == userId || t.ReceiverId == userId) && t.Time >= since)
            .Select(t => t.Time ?? DateTime.MinValue)
            .OrderBy(t => t)
            .ToListAsync();

        if (tradingTimes.Count >= 3)
        {
            // Sub-factor 1a: Extremely regular intervals (bot indicator)
            var intervals = new List<double>();
            for (int i = 1; i < tradingTimes.Count; i++)
            {
                intervals.Add((tradingTimes[i] - tradingTimes[i-1]).TotalMinutes);
            }

            var avgInterval = intervals.Average();
            var intervalVariance = intervals.Sum(x => Math.Pow(x - avgInterval, 2)) / intervals.Count;
            var regularityRisk = intervalVariance < 1.0 ? 0.8 : // Very regular = bot-like
                                intervalVariance < 5.0 ? 0.4 : // Somewhat regular = suspicious
                                0.0; // Irregular = normal

            behaviorFactors.Add(regularityRisk);

            // Sub-factor 1b: Burst trading (automation indicator)
            var burstCount = 0;
            var rapidTradeThreshold = 60; // 1 minute

            for (int i = 1; i < tradingTimes.Count; i++)
            {
                if ((tradingTimes[i] - tradingTimes[i-1]).TotalMinutes < rapidTradeThreshold)
                {
                    burstCount++;
                }
            }

            var burstRisk = Math.Min((double)burstCount / tradingTimes.Count, 1.0);
            behaviorFactors.Add(burstRisk);

            // Sub-factor 1c: Night trading patterns (suspicious timing)
            var nightTrades = tradingTimes.Count(t => t.Hour >= 2 && t.Hour <= 6); // 2 AM - 6 AM
            var nightTradingRisk = (double)nightTrades / tradingTimes.Count;
            behaviorFactors.Add(nightTradingRisk * 0.7); // Moderate weight for night trading
        }

        // Factor 2: Trade value patterns (exploitation detection)
        var valuePatterns = await AnalyzeTradeValuePatternsAsync(db, userId, since);
        behaviorFactors.Add(valuePatterns);

        // Factor 3: Partner diversity (multi-account detection)
        var partnerDiversity = await AnalyzePartnerDiversityAsync(db, userId, since);
        behaviorFactors.Add(partnerDiversity);

        // Factor 4: Trade completion patterns (automation detection)
        var completionPatterns = await AnalyzeTradeCompletionPatternsAsync(db, userId, since);
        behaviorFactors.Add(completionPatterns);

        return behaviorFactors.Count > 0 ? behaviorFactors.Average() : 0.0;
    }

    /// <summary>
    /// Analyzes trade value patterns for exploitation indicators.
    /// </summary>
    private async Task<double> AnalyzeTradeValuePatternsAsync(DittoDataConnection db, ulong userId, DateTime since)
    {
        var trades = await db.TradeLogs
            .Where(t => (t.SenderId == userId || t.ReceiverId == userId) && t.Time >= since)
            .Select(t => new {
                SenderValue = (double)((t.SenderCredits ?? 0) + (long)(t.SenderRedeems * 1000)),
                ReceiverValue = (double)((t.ReceiverCredits ?? 0) + (long)(t.ReceiverRedeems * 1000)),
                IsGiving = t.SenderId == userId
            })
            .ToListAsync();

        if (trades.Count < 3) return 0.0;

        var riskFactors = new List<double>();

        // Pattern 1: Always receiving high value (exploitation victim pattern)
        var receivingTrades = trades.Where(t => !t.IsGiving && t.SenderValue > t.ReceiverValue * 5).Count();
        var victimRisk = Math.Min((double)receivingTrades / trades.Count, 1.0);
        riskFactors.Add(victimRisk * 0.3); // Lower weight - victim is less risky

        // Pattern 2: Always giving away high value (exploitation/RMT pattern)
        var givingTrades = trades.Where(t => t.IsGiving && t.SenderValue > t.ReceiverValue * 5).Count();
        var exploiterRisk = Math.Min((double)givingTrades / trades.Count, 1.0);
        riskFactors.Add(exploiterRisk * 1.2); // Higher weight - exploiter is very risky

        // Pattern 3: Consistent one-directional value flow (farming pattern)
        var netValueFlow = trades.Sum(t => t.IsGiving ? -(t.SenderValue - t.ReceiverValue) : (t.SenderValue - t.ReceiverValue));
        var totalValueFlow = trades.Sum(t => Math.Abs(t.SenderValue - t.ReceiverValue));
        
        if (totalValueFlow > 0)
        {
            var flowDirectionality = Math.Abs(netValueFlow) / totalValueFlow;
            riskFactors.Add(flowDirectionality * 0.8); // High directionality = farming pattern
        }

        return riskFactors.Average();
    }

    /// <summary>
    /// Analyzes partner diversity for multi-account detection.
    /// </summary>
    private async Task<double> AnalyzePartnerDiversityAsync(DittoDataConnection db, ulong userId, DateTime since)
    {
        var partners = await db.TradeLogs
            .Where(t => (t.SenderId == userId || t.ReceiverId == userId) && t.Time >= since)
            .Select(t => t.SenderId == userId ? t.ReceiverId : t.SenderId)
            .Distinct()
            .CountAsync();

        var totalTrades = await db.TradeLogs
            .Where(t => (t.SenderId == userId || t.ReceiverId == userId) && t.Time >= since)
            .CountAsync();

        if (totalTrades == 0) return 0.0;

        // Low partner diversity = suspicious (trading with same accounts repeatedly)
        var diversityRatio = (double)partners / totalTrades;
        
        return diversityRatio < 0.2 ? 0.9 :    // Very low diversity = very suspicious
               diversityRatio < 0.4 ? 0.6 :    // Low diversity = suspicious
               diversityRatio < 0.6 ? 0.3 :    // Medium diversity = somewhat suspicious
               0.0;                            // High diversity = normal
    }

    /// <summary>
    /// Analyzes trade completion patterns for automation detection.
    /// </summary>
    private async Task<double> AnalyzeTradeCompletionPatternsAsync(DittoDataConnection db, ulong userId, DateTime since)
    {
        // This is a simplified analysis since we don't have detailed trade session timing data
        // In a more complete implementation, we'd analyze:
        // - Time from trade initiation to completion
        // - Consistency in confirmation timing
        // - Response time patterns

        var tradeTimes = await db.TradeLogs
            .Where(t => (t.SenderId == userId || t.ReceiverId == userId) && t.Time >= since)
            .Select(t => t.Time ?? DateTime.MinValue)
            .OrderBy(t => t)
            .ToListAsync();

        if (tradeTimes.Count < 5) return 0.0;

        // Analyze trade timing patterns for automation indicators
        var hourDistribution = new int[24];
        foreach (var time in tradeTimes)
        {
            hourDistribution[time.Hour]++;
        }

        // Very concentrated activity in specific hours = automation
        var maxHourActivity = hourDistribution.Max();
        var concentrationRisk = (double)maxHourActivity / tradeTimes.Count;

        return concentrationRisk > 0.7 ? 0.8 :    // Very concentrated = likely bot
               concentrationRisk > 0.5 ? 0.5 :    // Concentrated = suspicious
               concentrationRisk > 0.3 ? 0.2 :    // Somewhat concentrated = minor risk
               0.0;                               // Well distributed = normal
    }

    /// <summary>
    /// Calculates account age risk based on Discord snowflake IDs.
    /// CRITICAL: Newer accounts are much riskier for fraud.
    /// </summary>
    private static double CalculateAccountAgeRisk(ulong user1Id, ulong user2Id)
    {
        try
        {
            var user1Age = CalculateAccountAge(user1Id);
            var user2Age = CalculateAccountAge(user2Id);

            Log.Information("📅 Account ages: User1={User1Age} days, User2={User2Age} days", 
                user1Age.TotalDays, user2Age.TotalDays);

            var riskFactors = new List<double>();

            // Individual account age risks
            riskFactors.Add(CalculateIndividualAgeRisk(user1Age));
            riskFactors.Add(CalculateIndividualAgeRisk(user2Age));

            // Combined age risk (both accounts new = very suspicious)
            var combinedAgeRisk = CalculateCombinedAgeRisk(user1Age, user2Age);
            riskFactors.Add(combinedAgeRisk * 1.3); // Higher weight for combined risk

            var avgRisk = riskFactors.Average();
            
            Log.Information("🎯 Account age risk: Individual risks={Risk1:P2}, {Risk2:P2}, Combined={CombinedRisk:P2}, Final={FinalRisk:P2}",
                riskFactors[0], riskFactors[1], combinedAgeRisk, avgRisk);

            return Math.Min(avgRisk, 1.0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error calculating account age risk for users {User1} and {User2}", user1Id, user2Id);
            return 0.0;
        }
    }

    /// <summary>
    /// Calculates account age from Discord snowflake ID.
    /// </summary>
    private static TimeSpan CalculateAccountAge(ulong userId)
    {
        const long discordEpoch = 1420070400000L; // Discord epoch in milliseconds
        var timestamp = (userId >> 22) + discordEpoch;
        var creationTime = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp).DateTime;
        return DateTime.UtcNow - creationTime;
    }

    /// <summary>
    /// Calculates risk for a single account age.
    /// </summary>
    private static double CalculateIndividualAgeRisk(TimeSpan accountAge)
    {
        var ageDays = accountAge.TotalDays;

        return ageDays switch
        {
            < 1 => 1.0,        // Brand new account = maximum risk
            < 3 => 0.9,        // Very new = very high risk
            < 7 => 0.8,        // New account threshold = high risk
            < 14 => 0.6,       // 2 weeks = moderate-high risk
            < 30 => 0.4,       // 1 month = moderate risk
            < 90 => 0.2,       // 3 months = low-moderate risk
            < 180 => 0.1,      // 6 months = low risk
            _ => 0.0           // Older accounts = minimal risk
        };
    }

    /// <summary>
    /// Calculates combined age risk when both accounts interact.
    /// </summary>
    private static double CalculateCombinedAgeRisk(TimeSpan user1Age, TimeSpan user2Age)
    {
        var age1Days = user1Age.TotalDays;
        var age2Days = user2Age.TotalDays;

        // Both accounts very new = extremely suspicious (alt account creation)
        if (age1Days < NewAccountThreshold && age2Days < NewAccountThreshold)
        {
            var ageDifference = Math.Abs(age1Days - age2Days);
            
            // Created very close together = maximum suspicion
            return ageDifference < 1 ? 1.0 :      // Same day = maximum risk
                   ageDifference < 3 ? 0.9 :      // Within 3 days = very high risk
                   ageDifference < 7 ? 0.8 :      // Within a week = high risk
                   0.6;                           // Both new but different times = moderate-high risk
        }

        // One very new, one established = moderate risk (could be legitimate help)
        if ((age1Days < NewAccountThreshold && age2Days > 90) || 
            (age2Days < NewAccountThreshold && age1Days > 90))
        {
            return 0.3; // Moderate risk - could be legitimate mentoring
        }

        // Both moderately new = some risk
        if (age1Days < SuspiciousAccountThreshold && age2Days < SuspiciousAccountThreshold)
        {
            return 0.4; // Both suspicious age = moderate risk
        }

        return 0.0; // At least one established account = minimal combined risk
    }
    private async Task SetFraudFlagsAsync(TradeRiskAnalysis analysis, TradeSession session) { }
    /// <summary>
    /// Detects chain trading patterns for a user within a specified time window.
    /// Chain trading involves passing items through multiple accounts (A → B → C → A).
    /// </summary>
    private async Task<ChainTradingAnalysis> DetectChainTradingAsync(ulong userId, TimeSpan timeSpan)
    {
        var analysis = new ChainTradingAnalysis
        {
            UserId = userId,
            AnalysisTimestamp = DateTime.UtcNow
        };

        try
        {
            await using var db = await _dbProvider.GetConnectionAsync();
            var cutoffTime = DateTime.UtcNow - timeSpan;

            // Get recent trades involving this user
            var userTrades = await db.TradeLogs
                .Where(t => (t.SenderId == userId || t.ReceiverId == userId) && 
                           t.Time >= cutoffTime)
                .Select(t => new TradeEdge
                {
                    From = t.SenderId,
                    To = t.ReceiverId,
                    Value = (t.SenderCredits ?? 0) + (t.ReceiverCredits ?? 0), 
                    Timestamp = t.Time ?? DateTime.MinValue
                })
                .ToListAsync();

            if (userTrades.Count < 3) // Need at least 3 trades for a chain
            {
                analysis.RiskScore = 0.0;
                return analysis;
            }

            // Build trading network and detect chains
            var chains = FindTradingChains(userTrades, userId);
            analysis.DetectedChains = chains;
            analysis.MaxChainDepth = chains.Count > 0 ? chains.Max(c => c.Length) : 0;
            analysis.TotalValueFlowed = chains.Sum(c => c.TotalValue);

            // Calculate risk score based on chain patterns
            analysis.RiskScore = CalculateChainRiskScore(chains, userTrades.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error detecting chain trading for user {UserId}", userId);
            analysis.RiskScore = 0.0;
        }

        return analysis;
    }

    /// <summary>
    /// Finds trading chains in a set of trade edges.
    /// </summary>
    private List<TradeChain> FindTradingChains(List<TradeEdge> trades, ulong centralUserId)
    {
        var chains = new List<TradeChain>();
        var visited = new HashSet<TradeEdge>();

        foreach (var startTrade in trades.Where(t => !visited.Contains(t)))
        {
            var chain = BuildChainFromTrade(startTrade, trades, visited, ChainDepthLimit);
            if (chain.Length >= 3) // Only consider chains of 3+ trades
            {
                chains.Add(chain);
            }
        }

        return chains;
    }

    /// <summary>
    /// Builds a trading chain starting from a specific trade.
    /// </summary>
    private TradeChain BuildChainFromTrade(TradeEdge startTrade, List<TradeEdge> allTrades, HashSet<TradeEdge> visited, int maxDepth)
    {
        var chain = new TradeChain { Path = [startTrade] };
        visited.Add(startTrade);

        var currentUser = startTrade.To;
        var currentValue = startTrade.Value;

        for (int depth = 1; depth < maxDepth; depth++)
        {
            var nextTrade = allTrades.FirstOrDefault(t => 
                !visited.Contains(t) && 
                t.From == currentUser &&
                t.Timestamp > startTrade.Timestamp);

            if (nextTrade == null) break;

            chain.Path.Add(nextTrade);
            visited.Add(nextTrade);
            currentUser = nextTrade.To;
            currentValue += nextTrade.Value;

            // Check if chain loops back to start (circular trading)
            if (currentUser == startTrade.From && depth >= 2)
            {
                chain.ValueConcentration = 1.0m; // Full circle = maximum concentration
                break;
            }
        }

        chain.Length = chain.Path.Count;
        chain.TotalValue = currentValue;
        
        if (chain.ValueConcentration == 0)
        {
            // Calculate value concentration (how much value stays within the chain)
            var uniqueUsers = chain.Path.SelectMany(t => new[] { t.From, t.To }).Distinct().Count();
            chain.ValueConcentration = uniqueUsers > 1 ? (decimal)(chain.Length) / uniqueUsers : 1.0m;
        }

        return chain;
    }

    /// <summary>
    /// Calculates risk score based on detected chain patterns.
    /// </summary>
    private double CalculateChainRiskScore(List<TradeChain> chains, int totalTrades)
    {
        if (chains.Count == 0) return 0.0;

        var riskFactors = new List<double>();

        // Factor 1: Number of chains relative to total trades
        var chainDensity = (double)chains.Count / totalTrades;
        riskFactors.Add(Math.Min(chainDensity * 2.0, 1.0)); // Cap at 1.0

        // Factor 2: Maximum chain length
        var maxChainLength = chains.Max(c => c.Length);
        var lengthRisk = Math.Min((maxChainLength - 2.0) / 8.0, 1.0); // 3 trades = low risk, 10+ = max risk
        riskFactors.Add(lengthRisk);

        // Factor 3: Value concentration in chains
        var avgConcentration = chains.Average(c => (double)c.ValueConcentration);
        riskFactors.Add(avgConcentration);

        // Factor 4: Circular trading patterns
        var circularChains = chains.Count(c => c.ValueConcentration >= 0.9m);
        var circularRisk = Math.Min((double)circularChains / chains.Count, 1.0);
        riskFactors.Add(circularRisk * 1.5); // Boost circular pattern detection

        // Weighted average of risk factors
        return riskFactors.Average();
    }

    /// <summary>
    /// Detects burst trading patterns indicating potential automation or coordinated fraud.
    /// Burst trading involves rapid successive trades within a short time window.
    /// </summary>
    private async Task<BurstTradingAnalysis> DetectBurstTradingAsync(ulong userId)
    {
        var analysis = new BurstTradingAnalysis
        {
            UserId = userId,
            AnalysisTimestamp = DateTime.UtcNow
        };

        try
        {
            await using var db = await _dbProvider.GetConnectionAsync();
            var lookbackTime = DateTime.UtcNow.AddMinutes(-60); // Look back 1 hour

            // Get all trades by this user in the last hour, ordered by time
            var recentTrades = await db.TradeLogs
                .Where(t => (t.SenderId == userId || t.ReceiverId == userId) && t.Time >= lookbackTime)
                .OrderBy(t => t.Time)
                .Select(t => new { 
                    Time = t.Time ?? DateTime.MinValue,
                    PartnerId = t.SenderId == userId ? t.ReceiverId : t.SenderId
                })
                .ToListAsync();

            if (recentTrades.Count < BurstTradeCount)
            {
                analysis.RiskScore = 0.0;
                return analysis;
            }

            // Detect bursts: groups of trades within the burst window
            var bursts = new List<TradeBurst>();
            var currentBurst = new List<(DateTime Time, ulong PartnerId)>();

            foreach (var trade in recentTrades)
            {
                // Start new burst or continue current one
                if (currentBurst.Count == 0 || 
                    (trade.Time - currentBurst.Last().Time).TotalSeconds <= BurstTradeWindow)
                {
                    currentBurst.Add((trade.Time, trade.PartnerId));
                }
                else
                {
                    // Process completed burst
                    if (currentBurst.Count >= BurstTradeCount)
                    {
                        bursts.Add(CreateTradeBurst(currentBurst));
                    }
                    
                    // Start new burst
                    currentBurst.Clear();
                    currentBurst.Add((trade.Time, trade.PartnerId));
                }
            }

            // Process final burst
            if (currentBurst.Count >= BurstTradeCount)
            {
                bursts.Add(CreateTradeBurst(currentBurst));
            }

            analysis.DetectedBursts = bursts;
            analysis.TotalBurstsDetected = bursts.Count;
            analysis.MaxBurstSize = bursts.Count > 0 ? bursts.Max(b => b.TradeCount) : 0;
            
            // Calculate risk score
            analysis.RiskScore = CalculateBurstRiskScore(bursts, recentTrades.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error detecting burst trading for user {UserId}", userId);
            analysis.RiskScore = 0.0;
        }

        return analysis;
    }

    /// <summary>
    /// Creates a TradeBurst object from a list of trades.
    /// </summary>
    private TradeBurst CreateTradeBurst(List<(DateTime Time, ulong PartnerId)> trades)
    {
        var sortedTrades = trades.OrderBy(t => t.Time).ToList();
        var timeSpans = new List<double>();
        
        for (int i = 1; i < sortedTrades.Count; i++)
        {
            timeSpans.Add((sortedTrades[i].Time - sortedTrades[i-1].Time).TotalSeconds);
        }

        return new TradeBurst
        {
            StartTime = sortedTrades.First().Time,
            EndTime = sortedTrades.Last().Time,
            TradeCount = trades.Count,
            Duration = (sortedTrades.Last().Time - sortedTrades.First().Time).TotalSeconds,
            UniquePartners = trades.Select(t => t.PartnerId).Distinct().Count(),
            AverageInterval = timeSpans.Count > 0 ? timeSpans.Average() : 0
        };
    }

    /// <summary>
    /// Calculates risk score based on detected burst patterns.
    /// </summary>
    private double CalculateBurstRiskScore(List<TradeBurst> bursts, int totalTrades)
    {
        if (bursts.Count == 0) return 0.0;

        var riskFactors = new List<double>();

        // Factor 1: Number of bursts detected
        var burstDensity = Math.Min((double)bursts.Count / 3.0, 1.0); // 3+ bursts = max risk
        riskFactors.Add(burstDensity);

        // Factor 2: Maximum burst size
        var maxBurstSize = bursts.Max(b => b.TradeCount);
        var sizeRisk = Math.Min((maxBurstSize - BurstTradeCount) / 10.0, 1.0); // 5 trades = min, 15+ = max
        riskFactors.Add(sizeRisk);

        // Factor 3: Average time intervals (very short = suspicious)
        var avgInterval = bursts.Average(b => b.AverageInterval);
        var intervalRisk = avgInterval < 30 ? 1.0 : Math.Max(0.0, (120 - avgInterval) / 90.0); // <30s = max risk
        riskFactors.Add(intervalRisk);

        // Factor 4: Unique partners ratio (few partners = suspicious)
        var avgPartnerRatio = bursts.Average(b => (double)b.UniquePartners / b.TradeCount);
        var partnerRisk = avgPartnerRatio < 0.3 ? 1.0 : Math.Max(0.0, (0.7 - avgPartnerRatio) / 0.4);
        riskFactors.Add(partnerRisk);

        // Weighted average with emphasis on intervals and partners
        var weights = new[] { 0.2, 0.2, 0.3, 0.3 };
        return riskFactors.Zip(weights, (factor, weight) => factor * weight).Sum();
    }
    /// <summary>
    /// Detects if the trading parties are part of the same fraud network.
    /// Analyzes shared trading patterns and mutual connections.
    /// </summary>
    private async Task<NetworkConnectionAnalysis> DetectNetworkConnectionsAsync(TradeSession session)
    {
        var analysis = new NetworkConnectionAnalysis();

        try
        {
            await using var db = await _dbProvider.GetConnectionAsync();
            var lookbackTime = DateTime.UtcNow.AddDays(-30); // Analyze last 30 days

            // Get all trading partners for both users
            var user1Partners = await GetTradingPartnersAsync(db, session.Player1Id, lookbackTime);
            var user2Partners = await GetTradingPartnersAsync(db, session.Player2Id, lookbackTime);

            // Find shared trading partners
            var sharedPartners = user1Partners.Keys.Intersect(user2Partners.Keys).ToList();
            
            analysis.UsersInSameNetwork = sharedPartners.Count >= NetworkSizeThreshold;
            analysis.NetworkSize = sharedPartners.Count + 2; // +2 for the two traders themselves

            if (analysis.UsersInSameNetwork)
            {
                // Calculate network risk based on trading patterns
                var connectionStrengths = new List<double>();
                
                foreach (var partnerId in sharedPartners)
                {
                    var user1Trades = user1Partners[partnerId];
                    var user2Trades = user2Partners[partnerId];
                    
                    // Calculate connection strength based on trade frequency and timing
                    var strengthU1 = CalculateConnectionStrength(user1Trades);
                    var strengthU2 = CalculateConnectionStrength(user2Trades);
                    var averageStrength = (strengthU1 + strengthU2) / 2.0;
                    
                    connectionStrengths.Add(averageStrength);
                }

                // Network risk factors
                var riskFactors = new List<double>();

                // Factor 1: Network size (larger networks are more suspicious)
                var sizeRisk = Math.Min((double)(analysis.NetworkSize - NetworkSizeThreshold) / 7.0, 1.0);
                riskFactors.Add(sizeRisk);

                // Factor 2: Average connection strength
                var avgStrength = connectionStrengths.Count > 0 ? connectionStrengths.Average() : 0.0;
                riskFactors.Add(avgStrength);

                // Factor 3: Network density (how interconnected the network is)
                var maxPossibleConnections = analysis.NetworkSize * (analysis.NetworkSize - 1) / 2;
                var actualConnections = sharedPartners.Count + 1; // +1 for current trade
                var density = (double)actualConnections / maxPossibleConnections;
                riskFactors.Add(Math.Min(density * 2.0, 1.0));

                analysis.NetworkRiskScore = riskFactors.Average();
            }
            else
            {
                analysis.NetworkRiskScore = 0.0;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error detecting network connections for trade {TradeId}", session.SessionId);
            analysis.NetworkRiskScore = 0.0;
        }

        return analysis;
    }

    /// <summary>
    /// Gets all trading partners for a user within a time window.
    /// </summary>
    private async Task<Dictionary<ulong, List<DateTime>>> GetTradingPartnersAsync(
        DittoDataConnection db, ulong userId, DateTime since)
    {
        var partners = new Dictionary<ulong, List<DateTime>>();

        var trades = await db.TradeLogs
            .Where(t => (t.SenderId == userId || t.ReceiverId == userId) && t.Time >= since)
            .Select(t => new
            {
                PartnerId = t.SenderId == userId ? t.ReceiverId : t.SenderId,
                Time = t.Time ?? DateTime.MinValue
            })
            .ToListAsync();

        foreach (var trade in trades)
        {
            if (!partners.ContainsKey(trade.PartnerId))
            {
                partners[trade.PartnerId] = [];
            }
            partners[trade.PartnerId].Add(trade.Time);
        }

        return partners;
    }

    /// <summary>
    /// Calculates connection strength between users based on trading frequency and patterns.
    /// </summary>
    private double CalculateConnectionStrength(List<DateTime> tradeTimes)
    {
        if (tradeTimes.Count == 0) return 0.0;

        var factors = new List<double>();

        // Factor 1: Trade frequency
        var tradeFrequency = Math.Min((double)tradeTimes.Count / 10.0, 1.0); // 10+ trades = max
        factors.Add(tradeFrequency);

        // Factor 2: Consistency (regular trading intervals)
        if (tradeTimes.Count > 1)
        {
            var intervals = new List<double>();
            var sortedTimes = tradeTimes.OrderBy(t => t).ToList();
            
            for (int i = 1; i < sortedTimes.Count; i++)
            {
                intervals.Add((sortedTimes[i] - sortedTimes[i-1]).TotalHours);
            }

            var avgInterval = intervals.Average();
            var intervalVariance = intervals.Sum(i => Math.Pow(i - avgInterval, 2)) / intervals.Count;
            var consistency = Math.Max(0.0, 1.0 - (intervalVariance / (24 * 24))); // Normalize to 24h variance
            
            factors.Add(consistency);
        }

        // Factor 3: Recent activity boost
        var recentTrades = tradeTimes.Count(t => t >= DateTime.UtcNow.AddDays(-7));
        var recentActivityBoost = Math.Min((double)recentTrades / 3.0, 0.5); // Up to 50% boost
        
        return factors.Average() + recentActivityBoost;
    }
    
    /// <summary>
    /// Detects market manipulation patterns including wash trading, price fixing, and pump &amp; dump schemes.
    /// </summary>
    private async Task<MarketManipulationAnalysis> DetectMarketManipulationAsync(TradeSession session)
    {
        var analysis = new MarketManipulationAnalysis();

        try
        {
            await using var db = await _dbProvider.GetConnectionAsync();
            var lookbackTime = DateTime.UtcNow.AddDays(-7); // Analyze last week

            // Get Pokemon being traded for market analysis from both users
            var player1Pokemon = session.GetPokemonBy(session.Player1Id).Where(p => p.PokemonId.HasValue).Select(p => p.PokemonId!.Value);
            var player2Pokemon = session.GetPokemonBy(session.Player2Id).Where(p => p.PokemonId.HasValue).Select(p => p.PokemonId!.Value);
            var tradedPokemonIds = player1Pokemon.Concat(player2Pokemon).ToList();
            
            if (!tradedPokemonIds.Any())
            {
                analysis.RiskScore = 0.0;
                return analysis;
            }

            // Get recent market activities for these Pokemon (simplified approach for now)
            var marketActivities = await db.Market
                .Where(m => tradedPokemonIds.Contains(m.PokemonId) && m.ListedAt >= lookbackTime)
                .Join(db.UserPokemon, m => m.PokemonId, p => p.Id, (m, p) => new MarketActivityData
                {
                    PokemonName = p.PokemonName,
                    Price = m.Price,
                    UserId = m.OwnerId,
                    DateListed = m.ListedAt,
                    Sold = m.WasSold
                })
                .ToListAsync();

            var riskFactors = new List<double>();

            // Detection 1: Wash Trading - Same users repeatedly buying/selling same Pokemon types
            var washTradingRisk = await DetectWashTradingAsync(db, session.Player1Id, session.Player2Id, lookbackTime);
            riskFactors.Add(washTradingRisk);
            analysis.WashTradingDetected = washTradingRisk > 0.6;

            // Detection 2: Price Fixing - Coordinated pricing among connected users
            var priceFixingRisk = DetectPriceFixingPatterns(marketActivities);
            riskFactors.Add(priceFixingRisk);
            analysis.PriceFixingDetected = priceFixingRisk > 0.7;

            // Detection 3: Pump & Dump - Rapid price increases followed by sells
            var pumpDumpRisk = DetectPumpAndDumpPatterns(marketActivities);
            riskFactors.Add(pumpDumpRisk);
            analysis.PumpAndDumpDetected = pumpDumpRisk > 0.8;

            // Detection 4: Circular Trading Partners - Users trading in circles
            var circularRisk = await DetectCircularTradingAsync(db, session.Player1Id, session.Player2Id, lookbackTime);
            riskFactors.Add(circularRisk);
            analysis.CircularTradingPartners = circularRisk > 0.5 ? 1 : 0;

            analysis.RiskScore = riskFactors.Average();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error detecting market manipulation for trade {TradeId}", session.SessionId);
            analysis.RiskScore = 0.0;
        }

        return analysis;
    }

    /// <summary>
    /// Detects wash trading between users (back-and-forth trading to inflate activity).
    /// </summary>
    private async Task<double> DetectWashTradingAsync(DittoDataConnection db, ulong user1Id, ulong user2Id, DateTime since)
    {
        // Count direct trades between these two users
        var directTrades = await db.TradeLogs
            .Where(t => ((t.SenderId == user1Id && t.ReceiverId == user2Id) ||
                        (t.SenderId == user2Id && t.ReceiverId == user1Id)) &&
                       t.Time >= since)
            .CountAsync();

        // Get their total individual trade counts
        var user1TotalTrades = await db.TradeLogs
            .Where(t => (t.SenderId == user1Id || t.ReceiverId == user1Id) && t.Time >= since)
            .CountAsync();

        var user2TotalTrades = await db.TradeLogs
            .Where(t => (t.SenderId == user2Id || t.ReceiverId == user2Id) && t.Time >= since)
            .CountAsync();

        if (user1TotalTrades == 0 || user2TotalTrades == 0) return 0.0;

        // Calculate wash trading ratio
        var avgTotalTrades = (user1TotalTrades + user2TotalTrades) / 2.0;
        var washRatio = directTrades / avgTotalTrades;

        // High ratio = suspicious wash trading
        return Math.Min(washRatio * 2.0, 1.0);
    }

    /// <summary>
    /// Detects coordinated price fixing patterns.
    /// </summary>
    private double DetectPriceFixingPatterns(List<MarketActivityData> marketActivities)
    {
        if (marketActivities.Count < 3) return 0.0;

        var riskFactors = new List<double>();

        // Group by Pokemon name to analyze price patterns
        var pokemonGroups = marketActivities.GroupBy(m => m.PokemonName);

        foreach (var group in pokemonGroups)
        {
            var prices = group.Select(g => g.Price).ToList();
            if (prices.Count < 3) continue;

            // Factor 1: Price uniformity (identical prices = suspicious)
            var uniquePrices = prices.Distinct().Count();
            var uniformityRisk = 1.0 - ((double)uniquePrices / prices.Count);
            riskFactors.Add(uniformityRisk);

            // Factor 2: Seller coordination (same sellers repeatedly listing)
            var sellers = group.Select(g => g.UserId).ToList();
            var uniqueSellers = sellers.Distinct().Count();
            var sellerConcentration = 1.0 - ((double)uniqueSellers / sellers.Count);
            riskFactors.Add(sellerConcentration);
        }

        return riskFactors.Count > 0 ? riskFactors.Average() : 0.0;
    }

    /// <summary>
    /// Detects pump and dump patterns (rapid price increases followed by mass selling).
    /// </summary>
    private double DetectPumpAndDumpPatterns(List<MarketActivityData> marketActivities)
    {
        if (marketActivities.Count < 5) return 0.0;

        var riskFactors = new List<double>();
        var pokemonGroups = marketActivities.GroupBy(m => m.PokemonName);

        foreach (var group in pokemonGroups)
        {
            var chronological = group.OrderBy(g => g.DateListed).ToList();
            if (chronological.Count < 5) continue;

            var prices = chronological.Select(c => c.Price).ToList();
            
            // Look for rapid price increases followed by decreases
            var priceChanges = new List<double>();
            for (int i = 1; i < prices.Count; i++)
            {
                var change = ((double)prices[i] - prices[i-1]) / prices[i-1];
                priceChanges.Add(change);
            }

            // Detect pump pattern: rapid increases followed by rapid decreases
            var maxIncrease = priceChanges.Take(priceChanges.Count / 2).Max();
            var maxDecrease = Math.Abs(priceChanges.Skip(priceChanges.Count / 2).Min());

            if (maxIncrease > 0.5 && maxDecrease > 0.3) // 50% pump, 30% dump
            {
                var pumpDumpRisk = Math.Min((maxIncrease + maxDecrease) / 2.0, 1.0);
                riskFactors.Add(pumpDumpRisk);
            }
        }

        return riskFactors.Count > 0 ? riskFactors.Average() : 0.0;
    }

    /// <summary>
    /// Detects circular trading patterns between users.
    /// </summary>
    private async Task<double> DetectCircularTradingAsync(DittoDataConnection db, ulong user1Id, ulong user2Id, DateTime since)
    {
        // Find common trading partners who trade with both users
        var user1Partners = await GetTradingPartnersAsync(db, user1Id, since);
        var user2Partners = await GetTradingPartnersAsync(db, user2Id, since);

        var commonPartners = user1Partners.Keys.Intersect(user2Partners.Keys).ToList();
        
        if (commonPartners.Count < 2) return 0.0;

        // Calculate circular risk based on common partners and trading frequency
        var circularityScore = Math.Min((double)commonPartners.Count / 5.0, 1.0); // 5+ common partners = max risk
        
        return circularityScore;
    }
    private static List<string> GenerateActionableInsights(ComprehensiveFraudAnalysis analysis) => [];
    private static RecommendedAction DetermineRecommendedAction(ComprehensiveFraudAnalysis analysis) => new();
    
    private static AutomatedAction DetermineAutomatedAction(double riskScore)
    {
        return riskScore switch
        {
            >= CriticalThreshold => AutomatedAction.BlockTrade,
            >= TempBanThreshold => AutomatedAction.TempRestriction,
            >= BlockThreshold => AutomatedAction.BlockTrade,
            >= ReviewThreshold => AutomatedAction.FlagForReview,
            >= LogOnlyThreshold => AutomatedAction.LogOnly,
            _ => AutomatedAction.LogOnly
        };
    }

    private async Task<FraudDetectionResult> ExecuteAutomatedActionAsync(
        TradeSession session, ComprehensiveFraudAnalysis analysis, AutomatedAction action)
    {
        var message = action switch
        {
            AutomatedAction.BlockTrade => "Trade blocked due to high fraud risk.",
            AutomatedAction.TempRestriction => "Trade blocked and temporary restrictions applied.",
            _ => null
        };

        var isAllowed = action == AutomatedAction.LogOnly || action == AutomatedAction.FlagForReview;

        if (!isAllowed && analysis.ComprehensiveRiskScore >= TempBanThreshold)
        {
            await ApplyImmediateBansAsync(session.Player1Id, session.Player2Id, 
                $"High fraud risk detected: {analysis.ComprehensiveRiskScore:P2}");
        }

        return new FraudDetectionResult
        {
            IsAllowed = isAllowed,
            RiskScore = analysis.ComprehensiveRiskScore,
            RiskLevel = GetRiskLevel(analysis.ComprehensiveRiskScore),
            AutomatedAction = action,
            Message = message,
            FraudFlags = analysis.ActionableInsights,
            RequiresAdminReview = action == AutomatedAction.FlagForReview || action == AutomatedAction.BlockTrade
        };
    }

    /// <summary>
    /// Applies immediate trade and market bans to users.
    /// </summary>
    private async Task ApplyImmediateBansAsync(ulong user1Id, ulong user2Id, string reason)
    {
        await using var db = await _dbProvider.GetConnectionAsync();
        var banDate = DateTime.UtcNow;
        
        Log.Warning("🔨 Applying immediate bans to users {User1} and {User2}: {Reason}", user1Id, user2Id, reason);

        // Ban both users
        foreach (var userId in new[] { user1Id, user2Id })
        {
            await db.Users
                .Where(u => u.UserId == userId)
                .Set(u => u.TradeBanned, true)
                .Set(u => u.TradeBanReason, reason)
                .Set(u => u.TradeBanDate, banDate)
                .Set(u => u.MarketBanned, true)
                .Set(u => u.MarketBanReason, reason)
                .Set(u => u.MarketBanDate, banDate)
                .UpdateAsync();
        }
    }

    /// <summary>
    /// Logs comprehensive fraud detection results to the database for statistics and investigation.
    /// CRITICAL: This is what makes the stats work - without this, no detection data is saved!
    /// </summary>
    private async Task LogFraudDetectionToDatabase(TradeSession session, ComprehensiveFraudAnalysis analysis, 
        AutomatedAction action, FraudDetectionResult result)
    {
        try
        {
            await using var db = await _dbProvider.GetConnectionAsync();

            // Classify the primary fraud type from comprehensive analysis
            var primaryFraudType = ClassifyPrimaryFraudType(analysis);
            
            // Generate triggered rules and insights
            var triggeredRules = GenerateTriggeredRules(analysis);
            var detectionDetails = GenerateDetectionDetails(analysis);

            // Create fraud detection record
            var detection = new TradeFraudDetection
            {
                TradeId = null, // We don't have trade log ID at this point
                DetectionTimestamp = analysis.AnalysisTimestamp,
                PrimaryUserId = session.Player1Id,
                SecondaryUserId = session.Player2Id,
                AdditionalUserIds = null, // JSON array if needed for network fraud
                
                // Fraud classification
                FraudType = primaryFraudType,
                ConfidenceLevel = CalculateConfidenceLevel(analysis),
                RiskScore = analysis.BasicRiskAnalysis?.OverallRiskScore ?? 0.0,
                ComprehensiveRiskScore = analysis.ComprehensiveRiskScore,
                TriggeredRules = System.Text.Json.JsonSerializer.Serialize(triggeredRules),
                DetectionDetails = System.Text.Json.JsonSerializer.Serialize(detectionDetails),
                
                // Automated actions
                AutomatedAction = action,
                TradeBlocked = !result.IsAllowed,
                UsersNotified = false, // Set based on your notification logic
                AdminAlerted = result.RequiresAdminReview,
                
                // Investigation status
                InvestigationStatus = result.RequiresAdminReview ? InvestigationStatus.Pending : InvestigationStatus.Dismissed,
                AssignedAdminId = null,
                InvestigationStarted = null,
                ResolutionTimestamp = null,
                FinalVerdict = null,
                AdminNotes = null,
                AdminActions = null,
                
                // False positive tracking
                FalsePositive = false,
                FalsePositiveReason = null,
                RequiresRuleAdjustment = false,
                
                // fraud detection flags - THIS IS KEY FOR DETAILED STATS
                ChainTradingDetected = (analysis.ChainTradingAnalysis?.RiskScore ?? 0) > 0.5,
                BurstTradingDetected = (analysis.BurstTradingAnalysis?.RiskScore ?? 0) > 0.5,
                NetworkFraudDetected = (analysis.NetworkAnalysis?.NetworkRiskScore ?? 0) > 0.5,
                MarketManipulationDetected = (analysis.MarketManipulation?.RiskScore ?? 0) > 0.5,
                PokemonLaunderingDetected = (analysis.PokemonLaundering?.LaunderingDetected ?? false),
                ActionableInsights = System.Text.Json.JsonSerializer.Serialize(analysis.ActionableInsights)
            };

            // Insert the detection record
            await db.InsertAsync(detection);

            Log.Information("📊 Fraud detection logged to database: Trade={TradeId}, Type={FraudType}, Risk={Risk:P2}, Blocked={Blocked}",
                session.SessionId, primaryFraudType, analysis.ComprehensiveRiskScore, !result.IsAllowed);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Failed to log fraud detection to database for trade {TradeId}", session.SessionId);
        }
    }

    /// <summary>
    /// Classifies the primary fraud type based on comprehensive analysis results.
    /// This determines what shows up in the "Fraud Types" section of the stats.
    /// </summary>
    private static FraudType ClassifyPrimaryFraudType(ComprehensiveFraudAnalysis analysis)
    {
        var basicAnalysis = analysis.BasicRiskAnalysis;
        if (basicAnalysis == null) return FraudType.UnusualBehavior;

        // Priority order for fraud type classification:
        
        // 1. Alt Account Trading (highest priority - most damaging)
        if (basicAnalysis.FlaggedAltAccount || 
            (analysis.BasicRiskAnalysis?.RelationshipRiskScore ?? 0) > 0.7)
        {
            return FraudType.AltAccountTrading;
        }

        // 2. Real Money Trading (high priority - involves real money)
        if (basicAnalysis.FlaggedRmt || 
            (analysis.BasicRiskAnalysis?.ValueImbalanceScore ?? 0) > 0.8)
        {
            return FraudType.RealMoneyTrading;
        }

        // 3. Bot Abuse (automation detection)
        if (basicAnalysis.FlaggedBotActivity || 
            (analysis.BurstTradingAnalysis?.RiskScore ?? 0) > 0.7)
        {
            return FraudType.BotAbuse;
        }

        // 4. Newbie Exploitation (protecting new users)
        if (basicAnalysis.FlaggedNewbieExploitation ||
            (analysis.BasicRiskAnalysis?.AccountAgeRiskScore ?? 0) > 0.6)
        {
            return FraudType.NewbieExploitation;
        }

        // 5. Market Manipulation (affects market integrity)
        if ((analysis.MarketManipulation?.RiskScore ?? 0) > 0.6)
        {
            return FraudType.MarketManipulation;
        }

        // 6. Network Coordination (organized fraud)
        if ((analysis.NetworkAnalysis?.NetworkRiskScore ?? 0) > 0.6 ||
            (analysis.ChainTradingAnalysis?.RiskScore ?? 0) > 0.6)
        {
            return FraudType.NetworkCoordination;
        }

        // 7. Unusual Behavior (fallback for other suspicious activity)
        if (basicAnalysis.FlaggedUnusualBehavior || basicAnalysis.OverallRiskScore > 0.4)
        {
            return FraudType.UnusualBehavior;
        }

        // Default fallback
        return FraudType.UnusualBehavior;
    }

    /// <summary>
    /// Calculates confidence level for the fraud detection.
    /// </summary>
    private static double CalculateConfidenceLevel(ComprehensiveFraudAnalysis analysis)
    {
        var confidenceFactors = new List<double>();

        // Factor 1: How many fraud types detected
        var detectedTypes = 0;
        if ((analysis.ChainTradingAnalysis?.RiskScore ?? 0) > 0.5) detectedTypes++;
        if ((analysis.BurstTradingAnalysis?.RiskScore ?? 0) > 0.5) detectedTypes++;
        if ((analysis.NetworkAnalysis?.NetworkRiskScore ?? 0) > 0.5) detectedTypes++;
        if ((analysis.MarketManipulation?.RiskScore ?? 0) > 0.5) detectedTypes++;
        if ((analysis.PokemonLaundering?.LaunderingDetected ?? false)) detectedTypes++;

        var typeConfidence = Math.Min((double)detectedTypes / 2.0, 1.0); // 2+ types = high confidence
        confidenceFactors.Add(typeConfidence);

        // Factor 2: Basic analysis flags
        var basicAnalysis = analysis.BasicRiskAnalysis;
        if (basicAnalysis != null)
        {
            var flagCount = 0;
            if (basicAnalysis.FlaggedAltAccount) flagCount++;
            if (basicAnalysis.FlaggedRmt) flagCount++;
            if (basicAnalysis.FlaggedNewbieExploitation) flagCount++;
            if (basicAnalysis.FlaggedUnusualBehavior) flagCount++;
            if (basicAnalysis.FlaggedBotActivity) flagCount++;

            var flagConfidence = Math.Min((double)flagCount / 3.0, 1.0);
            confidenceFactors.Add(flagConfidence);
        }

        // Factor 3: Overall risk score
        var riskConfidence = Math.Min(analysis.ComprehensiveRiskScore * 1.2, 1.0);
        confidenceFactors.Add(riskConfidence);

        return confidenceFactors.Average();
    }

    /// <summary>
    /// Generates list of triggered detection rules for audit trail.
    /// </summary>
    private static List<string> GenerateTriggeredRules(ComprehensiveFraudAnalysis analysis)
    {
        var rules = new List<string>();
        var basicAnalysis = analysis.BasicRiskAnalysis;

        if (basicAnalysis != null)
        {
            if (basicAnalysis.ValueImbalanceScore > 0.5)
                rules.Add($"VALUE_IMBALANCE_HIGH:{basicAnalysis.ValueImbalanceScore:F2}");
            
            if (basicAnalysis.RelationshipRiskScore > 0.5)
                rules.Add($"RELATIONSHIP_RISK_HIGH:{basicAnalysis.RelationshipRiskScore:F2}");
            
            if (basicAnalysis.BehavioralRiskScore > 0.5)
                rules.Add($"BEHAVIORAL_RISK_HIGH:{basicAnalysis.BehavioralRiskScore:F2}");
            
            if (basicAnalysis.AccountAgeRiskScore > 0.5)
                rules.Add($"ACCOUNT_AGE_RISK_HIGH:{basicAnalysis.AccountAgeRiskScore:F2}");

            if (basicAnalysis.FlaggedAltAccount) rules.Add("ALT_ACCOUNT_DETECTED");
            if (basicAnalysis.FlaggedRmt) rules.Add("RMT_DETECTED");
            if (basicAnalysis.FlaggedNewbieExploitation) rules.Add("NEWBIE_EXPLOITATION_DETECTED");
            if (basicAnalysis.FlaggedUnusualBehavior) rules.Add("UNUSUAL_BEHAVIOR_DETECTED");
            if (basicAnalysis.FlaggedBotActivity) rules.Add("BOT_ACTIVITY_DETECTED");
        }

        if ((analysis.ChainTradingAnalysis?.RiskScore ?? 0) > 0.5)
            rules.Add($"CHAIN_TRADING_DETECTED:{analysis.ChainTradingAnalysis!.RiskScore:F2}");
        
        if ((analysis.BurstTradingAnalysis?.RiskScore ?? 0) > 0.5)
            rules.Add($"BURST_TRADING_DETECTED:{analysis.BurstTradingAnalysis!.RiskScore:F2}");
        
        if ((analysis.NetworkAnalysis?.NetworkRiskScore ?? 0) > 0.5)
            rules.Add($"NETWORK_FRAUD_DETECTED:{analysis.NetworkAnalysis!.NetworkRiskScore:F2}");
        
        if ((analysis.MarketManipulation?.RiskScore ?? 0) > 0.5)
            rules.Add($"MARKET_MANIPULATION_DETECTED:{analysis.MarketManipulation!.RiskScore:F2}");

        if (analysis.PokemonLaundering?.LaunderingDetected == true)
            rules.Add($"POKEMON_LAUNDERING_DETECTED:{analysis.PokemonLaundering.MaxRiskScore:F2}");

        return rules;
    }

    /// <summary>
    /// Generates detailed detection information for investigation purposes.
    /// </summary>
    private static object GenerateDetectionDetails(ComprehensiveFraudAnalysis analysis)
    {
        return new
        {
            TradeSessionId = analysis.TradeSessionId,
            AnalysisTimestamp = analysis.AnalysisTimestamp,
            ComprehensiveRiskScore = analysis.ComprehensiveRiskScore,
            
            BasicRisk = analysis.BasicRiskAnalysis != null ? new
            {
                OverallRiskScore = analysis.BasicRiskAnalysis.OverallRiskScore,
                ValueImbalanceScore = analysis.BasicRiskAnalysis.ValueImbalanceScore,
                RelationshipRiskScore = analysis.BasicRiskAnalysis.RelationshipRiskScore,
                BehavioralRiskScore = analysis.BasicRiskAnalysis.BehavioralRiskScore,
                AccountAgeRiskScore = analysis.BasicRiskAnalysis.AccountAgeRiskScore,
                SenderTotalValue = analysis.BasicRiskAnalysis.SenderTotalValue,
                ReceiverTotalValue = analysis.BasicRiskAnalysis.ReceiverTotalValue,
                ValueRatio = analysis.BasicRiskAnalysis.ValueRatio,
                FraudFlags = new
                {
                    AltAccount = analysis.BasicRiskAnalysis.FlaggedAltAccount,
                    RMT = analysis.BasicRiskAnalysis.FlaggedRmt,
                    NewbieExploitation = analysis.BasicRiskAnalysis.FlaggedNewbieExploitation,
                    UnusualBehavior = analysis.BasicRiskAnalysis.FlaggedUnusualBehavior,
                    BotActivity = analysis.BasicRiskAnalysis.FlaggedBotActivity
                }
            } : null,
            
            ChainTrading = analysis.ChainTradingAnalysis != null ? new
            {
                RiskScore = analysis.ChainTradingAnalysis.RiskScore,
                DetectedChains = analysis.ChainTradingAnalysis.DetectedChains.Count,
                MaxChainDepth = analysis.ChainTradingAnalysis.MaxChainDepth,
                TotalValueFlowed = analysis.ChainTradingAnalysis.TotalValueFlowed
            } : null,
            
            BurstTrading = analysis.BurstTradingAnalysis != null ? new
            {
                RiskScore = analysis.BurstTradingAnalysis.RiskScore,
                TotalBurstsDetected = analysis.BurstTradingAnalysis.TotalBurstsDetected,
                MaxBurstSize = analysis.BurstTradingAnalysis.MaxBurstSize
            } : null,
            
            NetworkAnalysis = analysis.NetworkAnalysis != null ? new
            {
                NetworkRiskScore = analysis.NetworkAnalysis.NetworkRiskScore,
                UsersInSameNetwork = analysis.NetworkAnalysis.UsersInSameNetwork,
                NetworkSize = analysis.NetworkAnalysis.NetworkSize
            } : null,
            
            MarketManipulation = analysis.MarketManipulation != null ? new
            {
                RiskScore = analysis.MarketManipulation.RiskScore,
                PriceFixingDetected = analysis.MarketManipulation.PriceFixingDetected,
                PumpAndDumpDetected = analysis.MarketManipulation.PumpAndDumpDetected,
                WashTradingDetected = analysis.MarketManipulation.WashTradingDetected
            } : null,
            
            PokemonLaundering = analysis.PokemonLaundering != null ? new
            {
                LaunderingDetected = analysis.PokemonLaundering.LaunderingDetected,
                MaxRiskScore = analysis.PokemonLaundering.MaxRiskScore,
                HighRiskPokemonCount = analysis.PokemonLaundering.HighRiskPokemonCount
            } : null,
            
            ActionableInsights = analysis.ActionableInsights,
            AnalysisErrors = analysis.AnalysisErrors
        };
    }

    #endregion
}