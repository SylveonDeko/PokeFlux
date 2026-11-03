using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Missions.Services;
using EeveeCore.Modules.Shop.Common;
using EeveeCore.Modules.Shop.Models;
using EeveeCore.Modules.Shop.Services;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     The shop, with the same purchase rule and routing as the shop commands. Anyone can browse it.
/// </summary>
/// <param name="shopService">The shop service.</param>
/// <param name="missionService">Reads the crystallized slime balance.</param>
/// <param name="dbProvider">The database connection provider, for the redeem balance.</param>
[Route("api/shop")]
public class ShopController(
    ShopService shopService,
    MissionService missionService,
    LinqToDbConnectionProvider dbProvider) : PokeFluxUserControllerBase
{
    /// <summary>
    ///     Lists the items of a shop category with the user's credits and crystallized slime.
    /// </summary>
    /// <param name="category">general, radiant or crystal.</param>
    [HttpGet]
    public async Task<IActionResult> Get(string category = "general")
    {
        category = category.ToLowerInvariant();
        var key = category switch
        {
            "general" => "General",
            "radiant" => "Radiant",
            "crystal" => "CrystalSlime",
            _ => null
        };
        if (key is null)
            return Error(400, "Unknown category. Use general, radiant or crystal.");

        var credits = await shopService.GetUserCreditsAsync(UserId);
        var (_, _, crystalSlime) = await missionService.GetUserXpInfoAsync(UserId);
        var slime = key == "CrystalSlime";
        var items = (await shopService.GetShopItemsByCategoryAsync(key, new ShopFilters(),
                ShopSortOrder.PriceAscending, credits))
            .Select(i => new ShopItemInfo(i.Id, i.Name, i.Metadata.ContainsKey("MongoId") ? i.Name : i.Id,
                i.Description, i.Price, slime ? "crystal-slime" : "credits", i.Stock, i.Rarity,
                i.PokemonType?.ToLowerInvariant(), i.Price <= (slime ? crystalSlime : credits)))
            .ToList();

        long redeems;
        await using (var db = await dbProvider.GetConnectionAsync())
            redeems = (long)(await db.Users.Where(u => u.UserId == UserId).Select(u => u.Redeems)
                .FirstOrDefaultAsync() ?? 0);

        var bought = await shopService.GetWeeklyChestPurchasesAsync(UserId);
        var chests = ShopService.ChestTypes
            .Select(t => new ChestInfo(t, ShopService.ChestPrices["credits"][t], ShopService.ChestPrices["redeems"][t],
                bought.GetValueOrDefault(t), ShopService.MaxWeeklyCreditChests))
            .ToList();
        var pokemonCount = await shopService.GetCollectionSizeAsync(UserId);

        return Ok(new ShopResponse(category, credits, crystalSlime, items, redeems,
            ShopConstants.MinimumPokemonRequired, pokemonCount, pokemonCount >= ShopConstants.MinimumPokemonRequired,
            chests));
    }

    /// <summary>
    ///     Buys a chest with credits or redeems, with the same prices and weekly limit as the shop buy chest command.
    /// </summary>
    /// <param name="request">The chest type and currency.</param>
    [HttpPost("chest")]
    public async Task<IActionResult> BuyChest([FromBody] ChestBuyRequest request)
    {
        var result = await shopService.BuyChestAsync(UserId, request.Type ?? "", request.Currency ?? "");
        var message = ResultText(result);
        return result.IsError ? Error(400, message) : Ok(new ApiMessage(message));
    }

    /// <summary>
    ///     Buys an item, or vitamins, rare candy, daycare spaces or redeems by name.
    /// </summary>
    /// <param name="request">The item and amount.</param>
    [HttpPost("buy")]
    public async Task<IActionResult> Buy([FromBody] ShopBuyRequest request)
    {
        var words = (request.Item ?? "").Split(' ', '-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0].Equals("item", StringComparison.OrdinalIgnoreCase))
            words.RemoveAt(0);
        if (words.Count == 0)
            return Error(400, "An item is required.");

        if (!await shopService.CanAccessRadiantShopAsync(UserId))
            return Error(403,
                $"You need at least {ShopConstants.MinimumPokemonRequired} Pokemon in your collection to use the shop.");

        if (words[0].ToLowerInvariant() is "chest" or "vitamins")
            return Error(400, words[0].Equals("chest", StringComparison.OrdinalIgnoreCase)
                ? "Chests are bought with the shop buy chest command."
                : $"Buy a vitamin by its name: {string.Join(", ", ShopService.Vitamins)}.");

        var result = await shopService.BuyByNameAsync(UserId, string.Join('-', words), request.Amount);
        var message = ResultText(result);
        return result.IsError ? Error(400, message) : Ok(new ApiMessage(message));
    }

    private static string ResultText(CommandResult result)
    {
        if (!string.IsNullOrEmpty(result.Message) || result.Embed is null)
            return result.Message;

        var parts = new List<string?> { result.Embed.Title, result.Embed.Description };
        if (result.Embed.Fields is { } fields)
            parts.AddRange(fields.Select(f => $"{f.Name}: {f.Value}"));
        return string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p)));
    }
}
