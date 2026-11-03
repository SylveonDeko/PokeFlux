using EeveeCore.Common.Enums;
using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Market.Services;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     Market oversight for staff.
/// </summary>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="marketService">The market service, for removing listings.</param>
/// <param name="views">Builds the admin shapes.</param>
/// <param name="audit">Records staff actions.</param>
[Route("api/admin/market")]
public class AdminMarketController(
    LinqToDbConnectionProvider dbProvider,
    MarketService marketService,
    AdminViews views,
    StaffAuditService audit) : AdminControllerBase
{
    private static readonly string[] Statuses = ["all", "active", "sold", "removed"];

    /// <summary>
    ///     Lists listings newest first, with filters.
    /// </summary>
    /// <param name="status">all, active, sold or removed.</param>
    /// <param name="seller">Only listings by this user.</param>
    /// <param name="buyer">Only listings bought by this user.</param>
    /// <param name="minPrice">The lowest price.</param>
    /// <param name="maxPrice">The highest price.</param>
    /// <param name="page">The page, from 1.</param>
    /// <param name="pageSize">Listings per page, up to 50.</param>
    [HttpGet]
    [DashStaff(StaffRank.Mod)]
    public async Task<IActionResult> List(string status = "all", ulong? seller = null, ulong? buyer = null,
        int? minPrice = null, int? maxPrice = null, int page = 1, int pageSize = DefaultPageSize)
    {
        status = status.ToLowerInvariant();
        if (!Statuses.Contains(status))
            return Error(400, $"Unknown status. Use one of: {string.Join(", ", Statuses)}.");
        if (minPrice > maxPrice)
            return Error(400, "The lowest price is above the highest price.");
        (page, pageSize) = Paging(page, pageSize);

        await using var db = await dbProvider.GetConnectionAsync();
        var query = db.Market.AsQueryable();
        query = status switch
        {
            "active" => query.Where(m => m.BuyerId == null),
            "sold" => query.Where(m => m.BuyerId > 0),
            "removed" => query.Where(m => m.BuyerId == 0),
            _ => query
        };
        if (seller is { } sellerId)
            query = query.Where(m => m.OwnerId == sellerId);
        if (buyer is { } buyerId)
            query = query.Where(m => m.BuyerId == buyerId);
        if (minPrice is { } min)
            query = query.Where(m => m.Price >= min);
        if (maxPrice is { } max)
            query = query.Where(m => m.Price <= max);

        var total = await query.CountAsync();
        var items = await views.ListingsAsync(db,
            query.OrderByDescending(m => m.Id).Skip((page - 1) * pageSize).Take(pageSize));
        return Ok(new AdminPage<AdminListing>(items, total, page, pageSize));
    }

    /// <summary>
    ///     Takes a listing off the market and gives the Pokémon back to the seller, the same way the seller's own
    ///     removal does.
    /// </summary>
    /// <param name="listingId">The listing's ID.</param>
    /// <param name="request">The reason.</param>
    [HttpPost("{listingId}/remove")]
    [DashStaff(StaffRank.Admin)]
    public async Task<IActionResult> Remove(ulong listingId, [FromBody] AdminReasonRequest request)
    {
        if (ReadReason(request.Reason) is not { } reason)
            return ReasonRequired();

        await using var db = await dbProvider.GetConnectionAsync();
        var listing = await (from m in db.Market
                from p in db.UserPokemon.LeftJoin(p => p.Id == m.PokemonId)
                where m.Id == listingId
                select new { m.OwnerId, p.PokemonName })
            .FirstOrDefaultAsync();
        if (listing is null)
            return Error(404, "That listing does not exist.");

        var result = await marketService.RemovePokemonFromMarketAsync(listing.OwnerId, listingId);
        if (!result.Success)
            return Error(result.Message.StartsWith("Someone is already") ? 409 : 400, result.Message);

        await audit.RecordAsync(UserId, UserName, "market_remove", reason, listing.OwnerId,
            $"Listing #{listingId}, {listing.PokemonName ?? "unknown Pokémon"}");
        return Ok(new ApiMessage($"Listing #{listingId} is removed and the Pokémon is back with its seller."));
    }
}
