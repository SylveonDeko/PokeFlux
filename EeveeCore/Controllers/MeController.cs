using System.Text.Json;
using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Database.Models.Mongo.Game;
using EeveeCore.Database.Models.Mongo.Pokemon;
using EeveeCore.Modules.Achievements.Common;
using EeveeCore.Modules.Achievements.Models;
using EeveeCore.Modules.Achievements.Services;
using EeveeCore.Modules.Missions.Common;
using EeveeCore.Modules.Missions.Services;
using EeveeCore.Modules.Spawn.Constants;
using Humanizer;
using LinqToDB;
using LinqToDB.Async;
using EeveeCore.Services.Impl;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EeveeCore.Controllers;

/// <summary>
///     The user's profile, items, achievements and missions.
/// </summary>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="achievementService">Achievements and loyalty.</param>
/// <param name="missionService">Missions, mission XP and slime.</param>
/// <param name="mongo">The game data, for item categories.</param>
/// <param name="staffRanks">Staff ranks, with bot owners at the top.</param>
[Route("api/me")]
public class MeController(
    LinqToDbConnectionProvider dbProvider,
    AchievementService achievementService,
    MissionService missionService,
    IMongoService mongo,
    StaffRankService staffRanks) : PokeFluxUserControllerBase
{
    /// <summary>
    ///     Gets the user's staff rank, which works before they have started.
    /// </summary>
    [HttpGet("staff")]
    public async Task<IActionResult> Staff() =>
        Ok(new StaffInfo((await staffRanks.GetRankAsync(UserId)).ToString(), staffRanks.IsOwner(UserId)));

    /// <summary>
    ///     Gets the user's trainer profile.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == UserId);
        if (user == null)
            return Error(404, "You have not started! Start with the start command first.");

        var owned = from o in db.UserPokemonOwnerships
            join p in db.UserPokemon on o.PokemonId equals p.Id
            where o.UserId == UserId
            select p;
        var counts = new PokemonCounts(
            await owned.CountAsync(),
            await owned.CountAsync(p => p.Shiny == true),
            await owned.CountAsync(p => p.Radiant == true),
            await owned.CountAsync(p => p.Skin == "shadow"),
            await owned.CountAsync(p => PokemonList.LegendList.Contains(p.PokemonName)));

        return Ok(new MeProfile(
            UserId,
            string.IsNullOrWhiteSpace(user.TrainerNickname) ? null : user.TrainerNickname,
            (long)(user.MewCoins ?? 0),
            (long)(user.Redeems ?? 0),
            Math.Clamp(user.Energy ?? 10, 0, 10),
            (long)(user.FishingLevel ?? 1),
            (long)(user.FishingExp ?? 0),
            (long)(user.FishingLevelCap ?? 50),
            user.Selected is > 0 ? user.Selected : null,
            user.Region,
            string.IsNullOrWhiteSpace(user.Hunt) ? null : user.Hunt,
            user.Chain,
            user.DaycareLimit ?? 1,
            counts,
            true,
            (await staffRanks.GetRankAsync(UserId)).ToString()));
    }

    /// <summary>
    ///     Gets the user's inventory, bag and the items their Pokémon hold.
    /// </summary>
    [HttpGet("inventory")]
    public async Task<IActionResult> Inventory()
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users
            .Where(u => u.UserId == UserId)
            .Select(u => new { u.Inventory, u.Items })
            .FirstOrDefaultAsync();
        if (user == null)
            return Error(404, "You have not started! Start with the start command first.");

        var heldItems = await (from o in db.UserPokemonOwnerships
                join p in db.UserPokemon on o.PokemonId equals p.Id
                where o.UserId == UserId && p.HeldItem != null && p.HeldItem != "None" && p.HeldItem != "none"
                group p by p.HeldItem
                into g
                select new { Item = g.Key, Count = g.Count() })
            .ToListAsync();

        var inventory = ReadCounts(user.Inventory);
        var bag = ReadCounts(user.Items);
        var held = heldItems.OrderByDescending(h => h.Count).Select(h => new ItemCount(h.Item, h.Count)).ToList();
        var categories = await ItemCategoriesAsync(inventory.Concat(bag).Concat(held).Select(i => i.Name));

        List<ItemCount> WithCategories(List<ItemCount> items) =>
            items.Select(i => i with { Category = categories.GetValueOrDefault(i.Name.ToLowerInvariant()) }).ToList();

        return Ok(new InventoryResponse(WithCategories(inventory), WithCategories(bag), WithCategories(held)));
    }

    private async Task<Dictionary<string, string>> ItemCategoriesAsync(IEnumerable<string> names)
    {
        var identifiers = names.Select(n => n.ToLowerInvariant()).Distinct().ToList();
        if (identifiers.Count == 0)
            return [];

        // Read the raw documents, as some items have no category
        var items = await mongo.Items
            .Find(Builders<Item>.Filter.In(i => i.Identifier, identifiers))
            .Project<BsonDocument>(Builders<Item>.Projection.Include("identifier").Include("category_id"))
            .ToListAsync();

        var categories = new Dictionary<string, string>();
        foreach (var item in items)
            if (item.TryGetValue("identifier", out var identifier) && identifier.IsString &&
                item.TryGetValue("category_id", out var categoryId) && categoryId.IsNumeric &&
                Item.CategoryIdentifiers.TryGetValue(categoryId.ToInt32(), out var category))
                categories.TryAdd(identifier.AsString, category);
        return categories;
    }

    /// <summary>
    ///     Gets the user's achievements by category, their milestones and their loyalty.
    /// </summary>
    [HttpGet("achievements")]
    public async Task<IActionResult> Achievements()
    {
        if (!await HasStartedAsync())
            return Error(404, "You have not started! Start with the start command first.");

        var values = await achievementService.GetAchievementValuesAsync(UserId);
        var lastMilestones = await achievementService.GetLastMilestonesAsync(UserId);
        var loyalty = await achievementService.GetLoyaltyAsync(UserId);

        var categories = AchievementService.Categories
            .Select(category => new AchievementCategory(category,
                AchievementService.FilterByCategory(values, category)
                    .Where(a => AchievementConstants.Milestones.ContainsKey(a.Key))
                    .Select(a =>
                    {
                        var milestones = AchievementConstants.Milestones[a.Key];
                        var next = milestones.FirstOrDefault(m => m > a.Value);
                        return new AchievementProgressInfo(a.Key,
                            AchievementConstants.AchievementDisplayNames.GetValueOrDefault(a.Key) ??
                            a.Key.Humanize(LetterCasing.Title), a.Value,
                            lastMilestones.GetValueOrDefault(a.Key), next > 0 ? next : null, milestones);
                    })
                    .ToList()))
            .ToList();

        var nextTier = AchievementService.GetNextLoyaltyTier(loyalty.LoyaltyPoints);
        return Ok(new AchievementsResponse(categories, await achievementService.GetTotalMilestonesAsync(UserId),
            new LoyaltyInfo(loyalty.LoyaltyPoints, loyalty.DailyStreak,
                AchievementService.GetNextStreakBonus(loyalty.DailyStreak),
                TierInfo(AchievementService.GetLoyaltyTier(loyalty.LoyaltyPoints)),
                nextTier is null ? null : TierInfo(nextTier))));
    }

    /// <summary>
    ///     Gets the active missions with the user's progress, their mission level, slime and the slime exchange.
    /// </summary>
    [HttpGet("missions")]
    public async Task<IActionResult> Missions()
    {
        if (!await HasStartedAsync())
            return Error(404, "You have not started! Start with the start command first.");

        await using var db = await dbProvider.GetConnectionAsync();
        var slime = await db.Users.Where(u => u.UserId == UserId).Select(u => u.Slime).FirstOrDefaultAsync();
        var progress = await missionService.GetUserProgressAsync(UserId) ?? new UserProgress { UserId = UserId };
        var (xp, level, crystalSlime) = await missionService.GetUserXpInfoAsync(UserId);

        var missions = (await missionService.GetActiveMissionsAsync())
            .Select(m => new MissionProgressInfo(m.Key, m.Name, m.Description, m.Target, m.Reward,
                MissionService.GetMissionProgress(progress, m.Key)))
            .ToList();
        var store = MissionConstants.StoreItems
            .Select(i => new MissionStoreItem(i.Key, i.Value.Name, i.Value.Description, i.Value.Cost,
                MissionService.IsCommandOnly(i.Value)))
            .ToList();

        // Mission XP counts up from 0 within each level
        return Ok(new MissionsResponse(missions, xp, level, 0, await missionService.GetLevelRequirementAsync(level),
            await missionService.GetUserTitleAsync(UserId), slime ?? 0, crystalSlime, store));
    }

    /// <summary>
    ///     Buys an item from the crystal slime exchange with the same rules as the missions buy command.
    /// </summary>
    /// <param name="request">The item's key or name.</param>
    [HttpPost("missions/buy")]
    public async Task<IActionResult> BuyMissionItem([FromBody] MissionBuyRequest request)
    {
        if (!await HasStartedAsync())
            return Error(404, "You have not started! Start with the start command first.");

        var query = request.Item?.Trim() ?? "";
        var key = MissionConstants.StoreItems
            .FirstOrDefault(i => i.Key.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                                 i.Value.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).Key;
        if (key is null)
            return Error(404, "There is no store item with that name.");

        return FromResult(await missionService.BuyStoreItemAsync(UserId, key));
    }

    private async Task<bool> HasStartedAsync()
    {
        await using var db = await dbProvider.GetConnectionAsync();
        return await db.Users.AnyAsync(u => u.UserId == UserId);
    }

    private static List<ItemCount> ReadCounts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [])
                .Where(i => i.Value.ValueKind == JsonValueKind.Number && i.Value.TryGetInt32(out var count) &&
                            count > 0)
                .Select(i => new ItemCount(i.Key, i.Value.GetInt32()))
                .OrderBy(i => i.Name)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static LoyaltyTierInfo TierInfo(LoyaltyTier tier) =>
        new(tier.TierName, tier.RequiredPoints, tier.XpMultiplier, tier.ShopDiscount, tier.MonthlyBonus);
}
