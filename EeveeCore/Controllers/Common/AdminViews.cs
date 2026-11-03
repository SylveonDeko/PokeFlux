using EeveeCore.Controllers.Models;
using EeveeCore.Database.Linq.Models.Game;
using LinqToDB;
using LinqToDB.Async;
using MarketRow = EeveeCore.Database.Linq.Models.Game.Market;

namespace EeveeCore.Controllers.Common;

/// <summary>
///     Builds the shapes the admin endpoints share, with trainer nicknames filled in.
/// </summary>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="cards">Builds Pokémon cards.</param>
public sealed class AdminViews(LinqToDbConnectionProvider dbProvider, PokemonCardFactory cards) : INService
{
    /// <summary>
    ///     Gets the trainer nicknames of users, leaving out users without one.
    /// </summary>
    /// <param name="ids">The users' IDs.</param>
    public async Task<Dictionary<ulong, string?>> NamesAsync(IEnumerable<ulong> ids)
    {
        var wanted = ids.Distinct().Select(id => (ulong?)id).ToList();
        if (wanted.Count == 0)
            return [];

        await using var db = await dbProvider.GetConnectionAsync();
        return (await db.Users
                .Where(u => wanted.Contains(u.UserId))
                .Select(u => new { u.UserId, u.TrainerNickname })
                .ToListAsync())
            .ToDictionary(u => u.UserId!.Value,
                u => string.IsNullOrWhiteSpace(u.TrainerNickname) ? null : u.TrainerNickname);
    }

    /// <summary>
    ///     Pairs a user ID with its nickname.
    /// </summary>
    /// <param name="id">The user's ID.</param>
    /// <param name="names">Nicknames from <see cref="NamesAsync" />.</param>
    public static AdminUserRef Ref(ulong id, IReadOnlyDictionary<ulong, string?> names) =>
        new(id, names.GetValueOrDefault(id));

    /// <summary>
    ///     Builds list entries for detections.
    /// </summary>
    /// <param name="detections">The detections.</param>
    public async Task<List<AdminDetectionSummary>> DetectionsAsync(IReadOnlyCollection<TradeFraudDetection> detections)
    {
        var names = await NamesAsync(detections.SelectMany(d =>
            d.SecondaryUserId is { } other ? new[] { d.PrimaryUserId, other } : [d.PrimaryUserId]));
        return detections.Select(d => Summary(d, names)).ToList();
    }

    /// <summary>
    ///     Builds a list entry for a detection.
    /// </summary>
    /// <param name="detection">The detection.</param>
    /// <param name="names">Nicknames from <see cref="NamesAsync" />.</param>
    public static AdminDetectionSummary Summary(TradeFraudDetection detection, IReadOnlyDictionary<ulong, string?> names) =>
        new(detection.Id,
            Ref(detection.PrimaryUserId, names),
            detection.SecondaryUserId is { } other ? Ref(other, names) : null,
            detection.RiskScore,
            detection.FraudType.ToString(),
            detection.InvestigationStatus.ToString(),
            detection.TradeBlocked,
            detection.DetectionTimestamp);

    /// <summary>
    ///     Loads listings with their Pokémon, keeping the query's order.
    /// </summary>
    /// <param name="db">The connection the query belongs to.</param>
    /// <param name="listings">The listings, already ordered and paged.</param>
    public async Task<List<AdminListing>> ListingsAsync(DittoDataConnection db, IQueryable<MarketRow> listings)
    {
        var rows = await (from m in listings
                from p in db.UserPokemon.LeftJoin(p => p.Id == m.PokemonId)
                select new { Market = m, Pokemon = p })
            .ToListAsync();

        var names = await NamesAsync(rows.SelectMany(r =>
            r.Market.BuyerId is > 0 ? new[] { r.Market.OwnerId, r.Market.BuyerId.Value } : [r.Market.OwnerId]));

        var result = new List<AdminListing>();
        foreach (var row in rows)
        {
            var market = row.Market;
            result.Add(new AdminListing(market.Id, market.Price, ListingStatus(market),
                Ref(market.OwnerId, names),
                market.BuyerId is > 0 ? Ref(market.BuyerId.Value, names) : null,
                market.ListedAt,
                row.Pokemon is null ? null : await cards.BuildAsync(row.Pokemon, CardContext.None, true)));
        }

        return result;
    }

    /// <summary>
    ///     Gets a listing's status: <c>active</c> while listed, then <c>sold</c> or <c>removed</c>.
    /// </summary>
    /// <param name="market">The listing.</param>
    public static string ListingStatus(MarketRow market) =>
        market.WasSold ? "sold" : market.WasRemoved ? "removed" : "active";
}
