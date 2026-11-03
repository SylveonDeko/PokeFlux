using System.IO;
using EeveeCore.Common.Interactive;
using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Market.Services;
using EeveeCore.Modules.Pokemon.Services;
using LinqToDB.Async;

namespace EeveeCore.Modules.Market;

/// <summary>
///     Pokémon market commands: listing, browsing, buying and removing.
/// </summary>
/// <param name="tradeLockService">Keeps players in a trade out of the market.</param>
/// <param name="pokemonService">Finds Pokémon sprites.</param>
/// <param name="dbProvider">The database connection provider.</param>
[Group("market")]
[Name("Market")]
public class MarketModule(
    ITradeLockService tradeLockService,
    PokemonService pokemonService,
    LinqToDbConnectionProvider dbProvider) : EeveeModule<MarketService>
{
    private const int ListingsPerPage = 5;

    private static readonly Dictionary<string, string> Sorts = new()
    {
        ["price_asc"] = "Price (Low to High)",
        ["price_desc"] = "Price (High to Low)",
        ["level_desc"] = "Level (High to Low)",
        ["level_asc"] = "Level (Low to High)",
        ["iv_desc"] = "IV Total (High to Low)",
        ["recent"] = "Recently Listed",
        ["name"] = "Pokemon Name"
    };

    private static readonly string[] Filters = ["all", "shiny", "radiant", "shadow", "legendary"];

    /// <summary>
    ///     Lists a Pokémon on the market.
    /// </summary>
    /// <param name="pokemonPosition">The Pokémon's number in your list.</param>
    /// <param name="price">The price in coins.</param>
    [Command("add")]
    [Summary("Add a Pokemon to the market.")]
    public async Task AddPokemonToMarket(int pokemonPosition, int price)
    {
        if (await IsMarketBannedAsync())
            return;

        var success = await tradeLockService.ExecuteWithTradeLockAsync(ctx.User, async () =>
        {
            var result = await Service.AddPokemonToMarketAsync(ctx.User.Id, (ulong)pokemonPosition, price);
            if (!result.Success)
            {
                await ErrorAsync(await WithPrefixAsync(result.Message));
                return;
            }

            await SendAsync(embed: new EmbedBuilder()
                .WithTitle("🏪 Pokemon Listed on Market")
                .WithDescription(result.Message)
                .WithColor(Color.Green)
                .WithFooter($"Listing ID: {result.Data}"));
        });

        if (!success)
            await ErrorAsync($"{ctx.User.Username} is currently in a trade!");
    }

    /// <summary>
    ///     Buys a Pokémon from the market after the user confirms.
    /// </summary>
    /// <param name="listingId">The listing ID.</param>
    [Command("buy")]
    [Summary("Buy a Pokemon from the market.")]
    public async Task BuyPokemonFromMarket(ulong listingId)
    {
        if (await IsMarketBannedAsync())
            return;

        if (await tradeLockService.IsUserTradeLockedAsync(ctx.User.Id))
        {
            await ErrorAsync($"{ctx.User.Username} is currently in a trade!");
            return;
        }

        if (await Service.IsListingLockedAsync(listingId))
        {
            await ErrorAsync("Someone is already in the process of buying that pokemon. You can try again later.");
            return;
        }

        var listing = await Service.GetPokemonAsync(listingId);
        if (listing == null)
        {
            await ErrorAsync("That listing does not exist or has already ended.");
            return;
        }

        var confirmed = await PromptConfirmAsync(new EmbedBuilder()
            .WithTitle("Confirm Purchase")
            .WithDescription(
                $"Are you sure you want to buy a level {listing.Level} {listing.PokemonName} for {listing.Price} credits?")
            .WithColor(Color.Orange));

        if (!confirmed)
        {
            await ErrorAsync("Purchase cancelled.");
            return;
        }

        var success = await tradeLockService.ExecuteWithTradeLockAsync(ctx.User, async () =>
        {
            var lockSuccess = await Service.ExecuteWithMarketLockAsync(listingId, async () =>
            {
                var result = await Service.BuyPokemonFromMarketAsync(ctx.User.Id, listingId);
                if (result.Success)
                    await ConfirmAsync(result.Message);
                else
                    await ErrorAsync(await WithPrefixAsync(result.Message));
            });

            if (!lockSuccess)
                await ErrorAsync("Someone is already in the process of buying that pokemon. You can try again later.");
        });

        if (!success)
            await ErrorAsync($"{ctx.User.Username} is currently in a trade!");
    }

    /// <summary>
    ///     Takes one of the user's listings off the market.
    /// </summary>
    /// <param name="listingId">The listing ID.</param>
    [Command("remove")]
    [Summary("Remove your Pokemon from the market.")]
    public async Task RemovePokemonFromMarket(ulong listingId)
    {
        if (await IsMarketBannedAsync())
            return;

        var result = await Service.RemovePokemonFromMarketAsync(ctx.User.Id, listingId);
        if (!result.Success)
        {
            await ErrorAsync(await WithPrefixAsync(result.Message));
            return;
        }

        await SendAsync(embed: new EmbedBuilder()
            .WithTitle("🏪 Pokemon Removed from Market")
            .WithDescription(result.Message)
            .WithColor(Color.Orange));
    }

    /// <summary>
    ///     Shows a listing's details.
    /// </summary>
    /// <param name="listingId">The listing ID.</param>
    [Command("info")]
    [Summary("View information about a Pokemon listing.")]
    public async Task ViewMarketListing(ulong listingId)
    {
        var pokemon = await Service.GetPokemonAsync(listingId);
        if (pokemon == null)
        {
            await ErrorAsync("That listing does not exist or has already ended.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"Market Listing #{listingId}")
            .WithDescription($"{Badge(pokemon.Shiny, pokemon.Radiant, pokemon.Skin)}**{pokemon.PokemonName}** (Level {pokemon.Level})")
            .AddField("Price", $"{pokemon.Price:N0} coins", true)
            .AddField("Gender", pokemon.Gender ?? "Unknown", true)
            .AddField("Nature", pokemon.Nature ?? "Unknown", true)
            .AddField("IVs", $"HP: {pokemon.HpIv} | ATK: {pokemon.AttackIv} | DEF: {pokemon.DefenseIv}\n" +
                             $"SPATK: {pokemon.SpecialAttackIv} | SPDEF: {pokemon.SpecialDefenseIv} | SPD: {pokemon.SpeedIv}")
            .AddField("Shiny", pokemon.Shiny == true ? "Yes" : "No", true)
            .AddField("Radiant", pokemon.Radiant == true ? "Yes" : "No", true)
            .AddField("Moves", pokemon.Moves is { Length: > 0 } ? string.Join(", ", pokemon.Moves) : "None")
            .WithColor(pokemon.Shiny == true ? Color.Gold : Color.Blue)
            .WithFooter($"Use {await GetPrefixAsync()}market buy {listingId} to purchase this Pokemon");

        var attachments = new List<AttachmentRequest>();
        SetImage(embed, await SpritePathAsync(pokemon.PokemonName, pokemon.Shiny, pokemon.Radiant, pokemon.Skin),
            "pokemon.png", attachments);

        await SendAsync(embed: embed, attachments: attachments);
    }

    /// <summary>
    ///     Browses the market, with optional sort, filter, search and page.
    /// </summary>
    /// <param name="options">Any of a sort, a filter, a page number and a name to search for.</param>
    [Command("list")]
    [Alias("browse", "search")]
    [Summary("Browse all Pokemon currently for sale on the market. Add a sort (price_asc, price_desc, level_desc, " +
             "level_asc, iv_desc, recent, name), a filter (all, shiny, radiant, shadow, legendary), a page number " +
             "or a name to search for, in any order.")]
    public async Task ListMarketPokemon([Remainder] string? options = null)
    {
        var sortBy = "recent";
        var filter = "all";
        var page = 1;
        var searchWords = new List<string>();

        foreach (var word in (options ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var lower = word.ToLowerInvariant();
            if (Sorts.ContainsKey(lower))
                sortBy = lower;
            else if (Filters.Contains(lower))
                filter = lower;
            else if (int.TryParse(lower, out var number) && number > 0)
                page = number;
            else
                searchWords.Add(word);
        }

        var search = searchWords.Count > 0 ? string.Join(" ", searchWords) : null;
        var result = await Service.GetMarketListingsAsync(sortBy, filter, search);

        if (result.Listings.Count == 0)
        {
            await ErrorAsync(result.HasFilters
                ? "No Pokemon are currently listed on the market that match your criteria."
                : "The market is currently empty.");
            return;
        }

        var totalPages = (result.Listings.Count - 1) / ListingsPerPage + 1;
        var header = $"🏪 **Pokemon Market** • Sort: {Sorts[sortBy]} • Filter: {filter}" +
                     (search is null ? "" : $" • Search: {search}");
        var prefix = await GetPrefixAsync();

        await Reactions.PaginateAsync(ctx.Channel, ctx.User.Id, totalPages, async index =>
        {
            var embeds = new List<EmbedRequest>();
            var attachments = new List<AttachmentRequest>();

            foreach (var listing in result.Listings.Skip(index * ListingsPerPage).Take(ListingsPerPage))
            {
                var ivTotal = listing.HpIv + listing.AttackIv + listing.DefenseIv +
                              listing.SpecialAttackIv + listing.SpecialDefenseIv + listing.SpeedIv;
                var ivPercentage = (int)Math.Round((double)ivTotal / 186 * 100);

                var embed = new EmbedBuilder()
                    .WithTitle(
                        $"{Badge(listing.Shiny, listing.Radiant, listing.Skin)}{listing.PokemonName} {GenderEmoji(listing.Gender)}"
                            .Trim())
                    .WithDescription($"Level: {listing.Level}\n" +
                                     $"IV: {GenerateProgressBar(ivPercentage)} {ivPercentage}%\n" +
                                     $"💰 **{listing.Price:N0}** coins\n" +
                                     $"ID: `{listing.ListingId}`")
                    .WithColor(Color.Green);

                SetImage(embed, await SpritePathAsync(listing.PokemonName, listing.Shiny, listing.Radiant, listing.Skin),
                    $"pokemon_{attachments.Count}.png", attachments, true);

                embeds.Add(embed.Build());
            }

            return new Page(
                $"{header}\nPage {index + 1}/{totalPages} • {result.Listings.Count} Pokemon listed • " +
                $"Use `{prefix}market info <id>` for details",
                embeds, attachments);
        }, TimeSpan.FromMinutes(10), page - 1);
    }

    private async Task<bool> IsMarketBannedAsync()
    {
        await using var db = await dbProvider.GetConnectionAsync();
        var user = await db.Users
            .Where(u => u.UserId == ctx.User.Id)
            .Select(u => new { u.MarketBanned, u.MarketBanReason })
            .FirstOrDefaultAsync();

        if (user?.MarketBanned != true)
            return false;

        await ErrorAsync($"You are banned from using the market. Reason: {user.MarketBanReason ?? "Fraud detection"}");
        return true;
    }

    private async Task<string?> SpritePathAsync(string pokemonName, bool? shiny, bool? radiant, string? skin)
    {
        var (_, imagePath) = await pokemonService.GetPokemonFormInfo(pokemonName, shiny == true, radiant == true,
            skin ?? "");
        return imagePath;
    }

    private static string Badge(bool? shiny, bool? radiant, string? skin)
    {
        if (radiant == true) return Emojis.Radiant + " ";
        if (shiny == true) return Emojis.Shiny + " ";
        return skin switch
        {
            "glitch" => "👾 ",
            "shadow" => Emojis.Shadow + " ",
            _ => ""
        };
    }

    private static string GenderEmoji(string? gender) =>
        gender switch
        {
            "-m" => "♂️",
            "-f" => "♀️",
            _ => ""
        };

    private static string GenerateProgressBar(int percentage)
    {
        const int barLength = 30;
        var filledLength = (int)Math.Round((double)percentage / 100 * barLength);
        return $"`{new string('█', filledLength)}{new string('░', barLength - filledLength)}`";
    }

    private async Task<string> WithPrefixAsync(string text) => text.Replace("`/", $"`{await GetPrefixAsync()}");
}
