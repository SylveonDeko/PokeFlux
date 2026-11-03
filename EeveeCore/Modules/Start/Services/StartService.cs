using EeveeCore.Database.Linq.Models.Bot;
using EeveeCore.Database.Linq.Models.Pokemon;
using EeveeCore.Modules.Spawn.Services;
using LinqToDB;
using LinqToDB.Async;
using User = EeveeCore.Database.Linq.Models.Bot.User;

namespace EeveeCore.Modules.Start.Services;

/// <summary>
///     Provides functionality for user registration and starter Pokémon creation.
///     Handles the initial setup process for new users.
/// </summary>
/// <param name="dbContext">The database context for data access.</param>
/// <param name="botLog">The bot event log.</param>
/// <param name="svc">The spawn service for creating Pokémon.</param>
public class StartService(LinqToDbConnectionProvider dbContext, BotLog botLog, SpawnService svc) : INService
{
    /// <summary>
    ///     The starters players can pick from, by type.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Starters = new Dictionary<string, string[]>
    {
        ["grass"] =
            ["Bulbasaur", "Chikorita", "Treecko", "Turtwig", "Snivy", "Chespin", "Rowlet", "Grookey", "Sprigatito"],
        ["water"] = ["Squirtle", "Totodile", "Mudkip", "Piplup", "Oshawott", "Froakie", "Popplio", "Sobble", "Quaxly"],
        ["fire"] =
            ["Charmander", "Cyndaquil", "Torchic", "Chimchar", "Tepig", "Fennekin", "Litten", "Scorbunny", "Fuecoco"]
    };

    /// <summary>
    ///     Finds a starter by name, ignoring case.
    /// </summary>
    /// <param name="name">The name to look up.</param>
    /// <returns>The starter's name as stored, or null when it is not a starter.</returns>
    public static string? FindStarter(string name) =>
        Starters.Values.SelectMany(s => s).FirstOrDefault(s => s.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Handles creating a new user account
    /// </summary>
    /// <param name="userId">The ID of the user to register.</param>
    /// <returns>A tuple containing a success indicator and a message.</returns>
    public async Task<(bool Success, string Message)> RegisterNewUser(ulong userId)
    {
        await using var db = await dbContext.GetConnectionAsync();

        // Check if user already exists
        var exists = await db.Users.AnyAsync(u => u.UserId == userId);
        if (exists) return (false, "You have already registered");

        // Create new user
        var newUser = new User
        {
            UserId = userId,
            Redeems = 0,
            EvPoints = 0,
            TrainerNickname = null,
            UpvotePoints = 0,
            MewCoins = 0,
            UserOrder = "kek",
            Visible = true,
            Inventory = "{\"nature-capsules\" : 5, \"honey\" : 1, \"battle-multiplier\": 1, \"shiny-multiplier\": 0 }",
            Comp = true
        };

        await db.InsertAsync(newUser);

        // Create achievement record (ON CONFLICT DO NOTHING equivalent)
        var existingAchievement = await db.Achievements.AnyAsync(a => a.UserId == userId);
        if (!existingAchievement)
        {
            await db.InsertAsync(new Achievement
            {
                UserId = userId
            });
        }

        // Create egg hatchery entries for user (3 groups) - check for existing first
        for (short group = 1; group <= 3; group++)
        {
            var existingHatchery = await db.EggHatcheries.AnyAsync(h => h.UserId == userId && h.Group == group);
            if (!existingHatchery)
            {
                await db.InsertAsync(new EggHatchery
                {
                    UserId = userId,
                    Group = group
                });
            }
        }
        return (true, "Successfully registered new user");
    }

    /// <summary>
    ///     Creates a starter Pokémon for a user
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="starterName">The name of the selected starter Pokémon.</param>
    /// <returns>A tuple containing a success indicator and a message.</returns>
    public async Task<(bool Success, string Message)> CreateStarterPokemon(ulong userId, string? starterName)
    {
        try
        {
            await svc.CreatePokemon(userId, starterName, boosted: true);

            return (true, $"Successfully created starter Pokémon {starterName}");
        }
        catch (Exception ex)
        {
            await botLog.SendAsync($"{userId} failed to create starter: {ex.Message}");

            return (false, "Failed to create starter Pokémon. Please try again.");
        }
    }
}
