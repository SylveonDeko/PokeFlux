using EeveeCore.Database.Models.Mongo.Game;
using MongoDB.Driver;
using LinqToDB;
using LinqToDB.Async;

namespace EeveeCore.Services.Impl;

/// <summary>
///     Background service that handles periodic tasks like energy regeneration, mission rotation, and honey expiration.
/// </summary>
public class BackgroundTaskService : BackgroundService, INService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackgroundTaskService> _logger;
    private Timer? _energyTimer;
    private Timer? _missionTimer;
    private Timer? _honeyTimer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BackgroundTaskService"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider for dependency injection.</param>
    /// <param name="logger">The logger for this service.</param>
    public BackgroundTaskService(IServiceProvider serviceProvider, ILogger<BackgroundTaskService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    ///     Starts the background tasks for energy regeneration, mission rotation, and honey expiration.
    /// </summary>
    /// <param name="stoppingToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // Wait for bot to be ready
        
        // Start energy regeneration every 24 minutes
        _energyTimer = new Timer(RegenerateEnergy, null, TimeSpan.Zero, TimeSpan.FromMinutes(24));
        
        // Check missions every 10 minutes
        _missionTimer = new Timer(CheckMissions, null, TimeSpan.Zero, TimeSpan.FromMinutes(10));
        
        // Check honey expiration every 10 minutes
        _honeyTimer = new Timer(CheckHoneyExpiration, null, TimeSpan.Zero, TimeSpan.FromMinutes(10));
        
        _logger.LogInformation("Background tasks started");
        
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Background tasks stopping...");
        }
    }

    private async void RegenerateEnergy(object? state)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbProvider = scope.ServiceProvider.GetRequiredService<LinqToDbConnectionProvider>();
            await using var db = await dbProvider.GetConnectionAsync();
            
            var affectedRows = await db.Users
                .Where(u => (u.Energy ?? 0) < 10)
                .Set(u => u.Energy, u => Math.Min(10, (u.Energy ?? 0) + 1))
                .UpdateAsync();
                
            if (affectedRows > 0)
            {
                _logger.LogInformation("Regenerated energy for {Count} users", affectedRows);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error regenerating energy");
        }
    }

    private async void CheckMissions(object? state)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var mongoService = scope.ServiceProvider.GetRequiredService<IMongoService>();
            
            var missions = mongoService.Missions;
            var userProgress = mongoService.UserProgress;
            
            // Check if missions collection exists and has data
            var totalMissions = await missions.CountDocumentsAsync(Builders<Mission>.Filter.Empty);
            if (totalMissions == 0)
            {
                _logger.LogWarning("No missions found in database, skipping mission rotation");
                return;
            }
            
            // Find the most recent active mission
            var activeMissions = await missions
                .Find(Builders<Mission>.Filter.Eq(m => m.Active, true))
                .ToListAsync();
            
            var mostRecentMission = activeMissions
                .OrderByDescending(m => m.StartedEpoch ?? m.Started ?? DateTime.MinValue)
                .FirstOrDefault();
            
            var currentTime = DateTime.UtcNow;
            
            // Check if 24 hours have passed
            if (mostRecentMission != null)
            {
                var startedTime = mostRecentMission.StartedEpoch ?? mostRecentMission.Started ?? DateTime.MinValue;
                if (startedTime > DateTime.MinValue && currentTime - startedTime < TimeSpan.FromHours(24))
                {
                    return; // Not 24 hours yet
                }
            }
            
            // Deactivate old missions
            await missions.UpdateManyAsync(
                Builders<Mission>.Filter.Eq(m => m.Active, true),
                Builders<Mission>.Update.Set(m => m.Active, false)
            );
            
            // Get potential missions
            var potentialMissions = await missions
                .Find(Builders<Mission>.Filter.Eq(m => m.Active, false))
                .ToListAsync();
            
            if (potentialMissions.Count >= 2)
            {
                var random = new Random();
                var chosenMissions = potentialMissions.OrderBy(x => random.Next()).Take(2);
                
                // Activate chosen missions
                foreach (var mission in chosenMissions)
                {
                    await missions.UpdateOneAsync(
                        Builders<Mission>.Filter.Eq(m => m.Id, mission.Id),
                        Builders<Mission>.Update
                            .Set(m => m.Active, true)
                            .Set(m => m.StartedEpoch, currentTime)
                    );
                }
                
                // Reset user progress
                await userProgress.DeleteManyAsync(Builders<UserProgress>.Filter.Empty);
                
                _logger.LogInformation("Rotated missions and reset user progress. Selected {Count} new missions", chosenMissions.Count());
            }
            else if (potentialMissions.Count == 0)
            {
                _logger.LogWarning("No inactive missions available for rotation");
            }
            else
            {
                _logger.LogWarning("Only {Count} missions available, need at least 2 for rotation", potentialMissions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking missions");
        }
    }

    private async void CheckHoneyExpiration(object? state)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbProvider = scope.ServiceProvider.GetRequiredService<LinqToDbConnectionProvider>();
            await using var db = await dbProvider.GetConnectionAsync();
            
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            // Get expired honey IDs for notification before deletion
            var expiredHoneys = await db.Honey
                .Where(h => h.Expires < currentTime)
                .ToListAsync();
            
            if (!expiredHoneys.Any()) return;
            
            // Remove expired honey
            var deletedCount = await db.Honey
                .Where(h => h.Expires < currentTime)
                .DeleteAsync();
            
            // Try to notify users (fire and forget)
            _ = Task.Run(async () =>
            {
                var client = scope.ServiceProvider.GetRequiredService<FluxerClient>();

                foreach (var honey in expiredHoneys)
                {
                    try
                    {
                        var embed = new EmbedBuilder()
                            .WithTitle($"Your {honey.Type} spread has expired!")
                            .WithDescription($"Your {honey.Type} spread in <#{honey.ChannelId}> has expired.\nSpread some more with the `spread` command.")
                            .WithColor(0xFFB6C1)
                            .Build();

                        await client.SendDirectMessageAsync(honey.OwnerId, embeds: [embed]);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to notify user {UserId} about honey expiration", honey.OwnerId);
                    }
                }
            });
            
            _logger.LogInformation("Processed {Count} expired honey spreads", expiredHoneys.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking honey expiration");
        }
    }

    /// <summary>
    ///     Disposes the background service and stops all timers.
    /// </summary>
    public override void Dispose()
    {
        _energyTimer?.Dispose();
        _missionTimer?.Dispose();
        _honeyTimer?.Dispose();
        base.Dispose();
    }
}