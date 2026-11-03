using DbUp;
using System.Reflection;

namespace EeveeCore.Database;

/// <summary>
/// </summary>
public class DatabaseMigrator : INService
{
    private readonly ILogger<DatabaseMigrator> _logger;
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the DatabaseMigrator class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="credentials">The bot credentials containing the connection string.</param>
    public DatabaseMigrator(ILogger<DatabaseMigrator> logger, BotCredentials credentials)
    {
        _logger = logger;
        _connectionString = credentials.PostgresConfig.ConnectionString;
    }

    /// <summary>
    /// Runs database migrations using DbUp.
    /// </summary>
    /// <returns>True if migrations were successful, false otherwise.</returns>
    public async Task<bool> MigrateAsync()
    {
        try
        {
            _logger.LogInformation("Starting database migration...");

            // Run synchronously to ensure proper transaction handling
            return await Task.Run(() =>
            {
                var upgrader = DeployChanges.To
                    .PostgresqlDatabase(_connectionString)
                    .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly(), script => script.StartsWith("EeveeCore.Database.Migrations."))
                    .WithTransaction()
                    .WithVariablesDisabled()
                    .LogToConsole()
                    .Build();

                if (!upgrader.IsUpgradeRequired())
                {
                    _logger.LogInformation("Database is already up to date.");
                    return true;
                }

                var result = upgrader.PerformUpgrade();

                if (!result.Successful)
                {
                    _logger.LogError(result.Error, "Database migration failed");
                    return false;
                }

                _logger.LogInformation("Database migration completed successfully");
                
                // Force garbage collection to ensure all database connections are properly disposed
                GC.Collect();
                GC.WaitForPendingFinalizers();
                
                return true;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during database migration");
            return false;
        }
    }
}