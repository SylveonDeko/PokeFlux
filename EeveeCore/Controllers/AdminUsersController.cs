using EeveeCore.Common.Enums;
using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Trade.Services;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     User lookup for staff.
/// </summary>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="tradeAdmin">The trade admin tools, for detections.</param>
/// <param name="views">Builds the admin shapes.</param>
[Route("api/admin/users")]
public class AdminUsersController(
    LinqToDbConnectionProvider dbProvider,
    TradeAdminService tradeAdmin,
    AdminViews views) : AdminControllerBase
{
    private const int SearchLimit = 25;
    private const int RecentLimit = 15;

    /// <summary>
    ///     Finds users by ID or by part of their trainer nickname.
    /// </summary>
    /// <param name="search">A user ID or a nickname, up to 50 characters.</param>
    [HttpGet]
    [DashStaff(StaffRank.Mod)]
    public async Task<IActionResult> Search(string? search)
    {
        var term = search?.Trim() ?? "";
        if (term.Length is 0 or > 50)
            return Error(400, "Search for a user ID or a nickname of up to 50 characters.");

        var id = ulong.TryParse(term, out var parsed) ? parsed : (ulong?)null;
        var lower = term.ToLowerInvariant();

        await using var db = await dbProvider.GetConnectionAsync();
        var users = await db.Users
            .Where(u => (id != null && u.UserId == id) ||
                        (u.TrainerNickname != null && u.TrainerNickname.ToLower().Contains(lower)))
            .OrderBy(u => u.UserId == id ? 0 : 1)
            .ThenBy(u => u.TrainerNickname)
            .Take(SearchLimit)
            .Select(u => new { u.UserId, u.TrainerNickname, u.Staff, u.TradeBanned, u.MarketBanned })
            .ToListAsync();

        return Ok(users.Select(u => new AdminUserResult(u.UserId!.Value,
            string.IsNullOrWhiteSpace(u.TrainerNickname) ? null : u.TrainerNickname,
            RankName(u.Staff), u.TradeBanned == true, u.MarketBanned == true)).ToList());
    }

    /// <summary>
    ///     Gets a user's profile, bans, trades, market activity and fraud detections.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    [HttpGet("{userId}")]
    [DashStaff(StaffRank.Mod)]
    public async Task<IActionResult> Get(ulong userId)
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user is null)
            return Error(404, "That user has not started.");

        var pokemonCount = await db.UserPokemonOwnerships.CountAsync(o => o.UserId == userId);

        var trades = await db.TradeLogs
            .Where(t => t.SenderId == userId || t.ReceiverId == userId)
            .OrderByDescending(t => t.TradeId)
            .Take(RecentLimit)
            .ToListAsync();
        var names = await views.NamesAsync(trades.SelectMany(t => new[] { t.SenderId, t.ReceiverId }));
        var recentTrades = trades.Select(t => new AdminTradeLogEntry(t.TradeId, t.Command, t.Time,
                AdminViews.Ref(t.SenderId, names), AdminViews.Ref(t.ReceiverId, names),
                t.SenderCredits ?? 0, t.ReceiverCredits ?? 0, (long)t.SenderRedeems, (long)t.ReceiverRedeems,
                t.SenderPokemon?.Length ?? 0, t.ReceiverPokemon?.Length ?? 0,
                Tokens(t.SenderTokens), Tokens(t.ReceiverTokens)))
            .ToList();

        var listings = await views.ListingsAsync(db,
            db.Market.Where(m => m.OwnerId == userId).OrderByDescending(m => m.Id).Take(RecentLimit));
        var purchases = await views.ListingsAsync(db,
            db.Market.Where(m => m.BuyerId == userId).OrderByDescending(m => m.Id).Take(RecentLimit));
        var detections = await views.DetectionsAsync(await tradeAdmin.GetDetectionsForUserAsync(userId, RecentLimit));

        return Ok(new AdminUserDetail(
            userId,
            string.IsNullOrWhiteSpace(user.TrainerNickname) ? null : user.TrainerNickname,
            (long)(user.MewCoins ?? 0),
            (long)(user.Redeems ?? 0),
            pokemonCount,
            RankName(user.Staff),
            user.Region,
            user.TradeLock == true,
            user.BotBanned == true,
            new AdminBanStatus(user.TradeBanned == true, Blank(user.TradeBanReason), user.TradeBanDate),
            new AdminBanStatus(user.MarketBanned == true, Blank(user.MarketBanReason), user.MarketBanDate),
            recentTrades,
            listings,
            purchases,
            detections));
    }

    private static string RankName(string? staff) =>
        Enum.TryParse<StaffRank>(staff, true, out var rank) ? rank.ToString() : StaffRank.User.ToString();

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static Dictionary<string, int> Tokens(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [])
                .Where(t => t.Value > 0)
                .ToDictionary(t => t.Key, t => t.Value);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
