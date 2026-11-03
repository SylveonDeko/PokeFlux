using EeveeCore.Common.Interactive;
using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Shop.Common;
using EeveeCore.Modules.Shop.Models;
using EeveeCore.Modules.Shop.Services;
using Serilog;

namespace EeveeCore.Modules.Shop;

/// <summary>
///     The shop: browsing items by category, searching, and buying items and services with credits.
/// </summary>
[Name("Shop")]
public class ShopModule : EeveeModule<ShopService>
{
    private static readonly string[] Vitamins = ["hp-up", "protein", "iron", "calcium", "zinc", "carbos"];
    private static readonly string[] ChestTypes = ["rare", "mythic", "legend"];
    private static readonly string[] Currencies = ["credits", "redeems"];

    /// <summary>
    ///     Shows the shop's categories and the user's credits.
    /// </summary>
    [Command("shop")]
    [Alias("shop browse")]
    [Summary("Open the shop and see its categories.")]
    public async Task Shop()
    {
        var prefix = await GetPrefixAsync();
        if (!await Service.CanAccessRadiantShopAsync(ctx.User.Id))
        {
            await SendAsync(embed: AccessDeniedEmbed());
            return;
        }

        var userCredits = await Service.GetUserCreditsAsync(ctx.User.Id);
        var embed = new EmbedBuilder()
            .WithTitle("🛍️ Welcome to the Unified Shop!")
            .WithDescription("Your one-stop destination for all Pokemon needs!\n\n" +
                             "**Available Categories:**\n" +
                             "• 🛒 **General Items** - Basic items, vitamins, daycare, and everyday supplies\n" +
                             "• ✨ **Radiant Shop** - Exclusive radiant Pokemon and rare collectibles\n" +
                             "• 🔮 **Crystal Slime Exchange** - Exchange crystallized slime for special rewards\n\n" +
                             $"Use `{prefix}shop <general|radiant|crystal> [page]` to start shopping!")
            .WithColor(ShopConstants.CategoryColors["Radiant"])
            .AddField("💰 Your Credits", $"{ShopConstants.Emojis.Credits} {userCredits:N0}", true)
            .AddField("🎯 Access Level", "✅ Radiant Shop Unlocked", true)
            .WithFooter($"Tip: Use {prefix}shop search to find exactly what you're looking for!");

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Lists a category's items, ten per page.
    /// </summary>
    /// <param name="category">General, Radiant or Crystal.</param>
    /// <param name="page">The page to open on.</param>
    [Command("shop")]
    [Alias("shop browse")]
    [Summary("Browse a shop category: general, radiant or crystal.")]
    [Priority(-1)]
    public async Task Shop(string category, int page = 1)
    {
        var prefix = await GetPrefixAsync();
        var key = FindCategory(category);
        if (key is null)
        {
            await ErrorAsync($"`{category}` is not a shop category. Pick one of `general`, `radiant` or `crystal`, " +
                             $"for example `{prefix}shop general`.");
            return;
        }

        if (!await Service.CanAccessRadiantShopAsync(ctx.User.Id))
        {
            await SendAsync(embed: AccessDeniedEmbed());
            return;
        }

        var userCredits = await Service.GetUserCreditsAsync(ctx.User.Id);
        var items = await Service.GetShopItemsByCategoryAsync(key, new ShopFilters(), ShopSortOrder.PriceAscending,
            userCredits);
        var pageCount = Math.Max(1, (int)Math.Ceiling((double)items.Count / ShopConstants.ItemsPerPage));
        var info = ShopConstants.CategoryInfo[key];
        var color = ShopConstants.CategoryColors[key];
        var slime = key == "CrystalSlime";
        var header = $"💰 **Your Credits:** {ShopConstants.Emojis.Credits} {userCredits:N0}";

        Task<Page> Render(int index)
        {
            var lines = items.Skip(index * ShopConstants.ItemsPerPage).Take(ShopConstants.ItemsPerPage)
                .Select(item => FormatItem(item, userCredits, slime));
            var body = items.Count == 0
                ? "**No Items Found**\nThere is nothing in this category right now."
                : string.Join("\n\n", lines);
            var footer = slime
                ? $"Page {index + 1}/{pageCount} | Exchange slime in the missions shop"
                : $"Page {index + 1}/{pageCount} | Buy with {prefix}shop buy <item> [amount]";

            var embed = new EmbedBuilder()
                .WithTitle($"{info.Emoji} {info.DisplayName}")
                .WithDescription($"{info.Description}\n\n{body}")
                .WithColor(color)
                .WithFooter(footer);
            return Task.FromResult(new Page(header, [embed.Build()]));
        }

        await Reactions.PaginateAsync(ctx.Channel, ctx.User.Id, pageCount, Render, startPage: page - 1);
    }

    /// <summary>
    ///     Sends the user their credit balance by direct message.
    /// </summary>
    [Command("shop balance")]
    [Summary("Check your current credit balance.")]
    public async Task Balance()
    {
        var credits = await Service.GetUserCreditsAsync(ctx.User.Id);

        var embed = new EmbedBuilder()
            .WithTitle("💰 Credit Balance")
            .WithDescription($"**Current Balance:** {ShopConstants.Emojis.Credits} {credits:N0} credits")
            .WithColor(Color.Gold);

        embed.AddField("💡 How to Earn Credits",
            "• Complete daily missions\n" +
            "• Win Pokemon battles\n" +
            "• Participate in events\n" +
            "• Trade with other trainers\n" +
            "• Find rare Pokemon");

        if (credits < 1000)
            embed.AddField("🎯 Getting Started",
                "Try completing some missions or catching Pokemon to earn your first credits!");
        else if (credits >= 100000)
            embed.AddField("🏆 High Roller", "You have enough credits to purchase rare items in the radiant shop!");

        embed.WithFooter("Credits are used to purchase items in the shop");

        await SendPrivateOrReplyAsync(embed.Build());
    }

    /// <summary>
    ///     Searches the shop by item name or category.
    /// </summary>
    /// <param name="query">The search text, optionally with <c>type:</c> and <c>max:</c> filters.</param>
    [Command("shop search")]
    [Summary("Search the shop. Add type:<type> or max:<price> to filter, for example `shop search boost type:fire max:20000`.")]
    public async Task Search([Remainder] string query)
    {
        var prefix = await GetPrefixAsync();
        string? type = null;
        int? maxPrice = null;
        var words = new List<string>();
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                var value = word[5..];
                type = ShopConstants.PokemonTypes.FirstOrDefault(t =>
                    t.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (type is null)
                {
                    await ErrorAsync($"`{value}` is not a type. Pick one of " +
                                     $"{string.Join(", ", ShopConstants.PokemonTypes)}.");
                    return;
                }
            }
            else if (word.StartsWith("max:", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(word[4..].Replace(",", ""), out var max) || max < 0)
                {
                    await ErrorAsync("The max price has to be a whole number, for example `max:20000`.");
                    return;
                }

                maxPrice = max;
            }
            else
            {
                words.Add(word);
            }
        }

        var text = string.Join(' ', words);
        if (!await Service.CanAccessRadiantShopAsync(ctx.User.Id))
        {
            await ErrorAsync($"You need at least {ShopConstants.MinimumPokemonRequired} Pokemon to access the shop.");
            return;
        }

        var filters = new ShopFilters
        {
            ItemName = text,
            PokemonType = type,
            MaxPrice = maxPrice,
            ShowAvailableOnly = true
        };

        var userCredits = await Service.GetUserCreditsAsync(ctx.User.Id);
        var items = await Service.GetShopItemsAsync(filters, ShopSortOrder.PriceAscending, userCredits);

        var embed = new EmbedBuilder()
            .WithTitle($"🔍 Search Results for \"{text}\"")
            .WithColor(Color.Blue);

        if (items.Count == 0)
        {
            embed.WithDescription("No items found matching your search criteria.");
            embed.AddField("💡 Search Tips",
                "• Try broader search terms\n" +
                "• Check your spelling\n" +
                "• Remove filters and try again\n" +
                "• Browse the shop to see all available items");
        }
        else
        {
            embed.WithDescription($"Found {items.Count} item(s) matching your search:");

            foreach (var item in items.Take(10))
            {
                var typeEmoji = TypeEmoji(item);
                var affordableIcon = item.Price <= userCredits ? "✅" : "❌";
                var stockInfo = item.Stock == -1 ? "∞" : item.Stock.ToString();

                embed.AddField($"{affordableIcon} {typeEmoji} {item.Name}",
                    $"{item.Description}\n" +
                    $"**Price:** {ShopConstants.Emojis.Credits} {item.Price:N0} | **Stock:** {stockInfo} | **Category:** {item.Category}");
            }

            if (items.Count > 10)
                embed.WithFooter($"... and {items.Count - 10} more items. Use {prefix}shop <category> for full results.");
        }

        var filterInfo = new List<string>();
        if (type is not null) filterInfo.Add($"Type: {type}");
        if (maxPrice.HasValue) filterInfo.Add($"Max Price: {maxPrice:N0}");
        if (filterInfo.Count > 0)
            embed.AddField("🔧 Active Filters", string.Join(", ", filterInfo));

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Explains how the shop works.
    /// </summary>
    [Command("shop info")]
    [Summary("Learn about the shop.")]
    public async Task Info()
    {
        var prefix = await GetPrefixAsync();
        var embed = new EmbedBuilder()
            .WithTitle("🛍️ Unified Shop Information")
            .WithDescription("Your comprehensive shopping destination for all Pokemon needs!")
            .WithColor(ShopConstants.CategoryColors["General"]);

        embed.AddField("🎯 **What is the Unified Shop?**",
            "The unified shop combines all purchasing systems into one place. " +
            "Browse general items, exclusive radiant collectibles, and crystal slime exchanges " +
            "by category.");

        embed.AddField("🔓 **Access Requirements**",
            $"• Minimum {ShopConstants.MinimumPokemonRequired} Pokemon in your collection\n" +
            "• Available credits for purchases\n" +
            "• Active trainer status");

        embed.AddField("📂 **Shop Categories**",
            "• **🛒 General Items** - Basic items, vitamins, daycare spaces, evolution stones, berries\n" +
            "• **✨ Radiant Shop** - Exclusive radiant Pokemon, rare boosts, and legendary collectibles\n" +
            "• **🔮 Crystal Slime Exchange** - Trade crystallized slime for credits, friendship stones, VIP tokens");

        embed.AddField("💰 **Payment & Credits**",
            "All purchases are made with credits earned through:\n" +
            "• Daily missions and achievements\n" +
            "• Pokemon battles and tournaments\n" +
            "• Trading with other trainers\n" +
            "• Special events and competitions");

        embed.AddField("🛍️ **How to Shop**",
            $"1. Use `{prefix}shop` to see the categories\n" +
            $"2. Use `{prefix}shop <category> [page]` to list a category's items\n" +
            $"3. Use `{prefix}shop search <query>` to find specific items\n" +
            $"4. Use `{prefix}shop buy <item> [amount]` to buy an item\n" +
            "5. Items are added to your inventory automatically");

        embed.AddField("🔧 **Commands**",
            $"`{prefix}shop <category> [page]` - Browse a category\n" +
            $"`{prefix}shop search <query>` - Search for specific items\n" +
            $"`{prefix}shop buy <item> [amount]` - Buy an item\n" +
            $"`{prefix}shop balance` - Check your credit balance\n" +
            $"`{prefix}shop stats` - View shop statistics\n" +
            $"`{prefix}shop info` - Show this information");

        embed.AddField("⚠️ **Important Notes**",
            "• All sales are final - no refunds\n" +
            "• Stock is limited for many items\n" +
            "• Prices may change based on availability\n" +
            "• Some items are seasonal or event-exclusive");

        embed.WithFooter("Happy shopping! 🛍️");

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Sends the user shop statistics and the items they can afford by direct message.
    /// </summary>
    [Command("shop stats")]
    [Summary("View shop statistics and the items you can afford.")]
    public async Task Stats()
    {
        var userCredits = await Service.GetUserCreditsAsync(ctx.User.Id);
        var allItems = await Service.GetShopItemsAsync();

        var embed = new EmbedBuilder()
            .WithTitle("📊 Shop Statistics")
            .WithDescription("Current shop status and popular items")
            .WithColor(Color.Purple);

        embed.AddField("🏪 **Shop Overview**",
            $"• Total Items: {allItems.Count}\n" +
            $"• Radiant Pokemon: {allItems.Count(i => i.IsRadiant)}\n" +
            $"• Categories: {allItems.Select(i => i.Category).Distinct().Count()}\n" +
            $"• Average Price: {ShopConstants.Emojis.Credits} {(allItems.Count > 0 ? allItems.Average(i => i.Price) : 0):N0}",
            true);

        if (allItems.Count > 0)
            embed.AddField("💰 **Price Ranges**",
                $"• Cheapest: {ShopConstants.Emojis.Credits} {allItems.Min(i => i.Price):N0}\n" +
                $"• Most Expensive: {ShopConstants.Emojis.Credits} {allItems.Max(i => i.Price):N0}\n" +
                $"• You can afford: {allItems.Count(i => i.Price <= userCredits)} items", true);

        var categoryStats = allItems.GroupBy(i => i.Category)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"• {g.Key}: {g.Count()} items")
            .ToList();
        if (categoryStats.Count > 0)
            embed.AddField("📂 **Top Categories**", string.Join("\n", categoryStats), true);

        var affordable = allItems.Where(i => i.Price <= userCredits).Take(5)
            .Select(i => $"• {i.Name} - {ShopConstants.Emojis.Credits} {i.Price:N0}").ToList();
        if (affordable.Count > 0)
            embed.AddField("✅ **Affordable for You**", string.Join("\n", affordable));

        embed.WithFooter($"Your balance: {ShopConstants.Emojis.Credits} {userCredits:N0} credits");

        await SendPrivateOrReplyAsync(embed.Build());
    }

    /// <summary>
    ///     Buys an item from the shop, or a vitamin, rare candy, daycare space or redeems by name.
    /// </summary>
    /// <param name="input">The item's name, optionally followed by an amount.</param>
    [Command("shop buy")]
    [Alias("buy", "items buy")]
    [Summary("Buy an item from the shop, for example `shop buy fire stone` or `shop buy protein 5`.")]
    [Priority(-1)]
    public async Task Buy([Remainder] string input)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0].Equals("item", StringComparison.OrdinalIgnoreCase))
            words.RemoveAt(0);

        int? amount = null;
        if (words.Count > 1 && int.TryParse(words[^1], out var parsed))
        {
            amount = parsed;
            words.RemoveAt(words.Count - 1);
        }

        var itemName = string.Join('-', words).ToLowerInvariant();
        if (words[0].ToLowerInvariant() is "chest" or "vitamins")
        {
            var prefix = await GetPrefixAsync();
            await ErrorAsync(words[0].Equals("chest", StringComparison.OrdinalIgnoreCase)
                ? $"Use `{prefix}shop buy chest <rare|mythic|legend> <credits|redeems>`."
                : $"Use `{prefix}shop buy vitamins <{string.Join('|', Vitamins)}> <amount>`.");
            return;
        }

        try
        {
            CommandResult result;
            if (Vitamins.Contains(itemName))
                result = await Service.BuyVitaminsAsync(ctx.User.Id, itemName, amount ?? 1);
            else if (itemName is "candy" or "rare-candy")
                result = await Service.BuyCandyAsync(ctx.User.Id, amount ?? 1);
            else if (itemName is "daycare" or "daycare-space")
                result = await Service.BuyDaycareAsync(ctx.User.Id, amount ?? 1);
            else if (itemName is "redeems" or "redeem")
                result = await Service.BuyRedeemsAsync(ctx.User.Id, (ulong?)amount);
            else if (amount is > 1)
                result = CommandResult.Error($"You can only buy one {itemName} at a time.");
            else
                result = await Service.BuyItemAsync(ctx.User.Id, itemName);

            await SendResultAsync(result);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error buying item {ItemName} for user {UserId}", itemName, ctx.User.Id);
            await ErrorAsync("An error occurred while purchasing the item.");
        }
    }

    /// <summary>
    ///     Buys daycare spaces at 10,000 credits each.
    /// </summary>
    /// <param name="amount">How many spaces to buy.</param>
    [Command("shop buy daycare")]
    [Alias("buy daycare", "items buy daycare")]
    [Summary("Buy additional daycare spaces.")]
    public async Task BuyDaycare(int amount = 1)
    {
        try
        {
            await SendResultAsync(await Service.BuyDaycareAsync(ctx.User.Id, amount));
        }
        catch (Exception e)
        {
            Log.Error(e, "Error buying daycare spaces for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while purchasing daycare spaces.");
        }
    }

    /// <summary>
    ///     Buys vitamins for the selected Pokémon, each adding 10 EVs to one stat.
    /// </summary>
    /// <param name="vitaminName">hp-up, protein, iron, calcium, zinc or carbos.</param>
    /// <param name="amount">How many to buy.</param>
    [Command("shop buy vitamins")]
    [Alias("buy vitamins", "items buy vitamins")]
    [Summary("Buy vitamins for your selected pokemon: hp-up, protein, iron, calcium, zinc or carbos.")]
    public async Task BuyVitamins(string vitaminName, int amount)
    {
        var vitamin = vitaminName.ToLowerInvariant().Replace(' ', '-');
        if (vitamin == "hpup")
            vitamin = "hp-up";
        if (!Vitamins.Contains(vitamin))
        {
            await ErrorAsync($"`{vitaminName}` is not a vitamin. Pick one of {string.Join(", ", Vitamins)}.");
            return;
        }

        try
        {
            await SendResultAsync(await Service.BuyVitaminsAsync(ctx.User.Id, vitamin, amount));
        }
        catch (Exception e)
        {
            Log.Error(e, "Error buying vitamins for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while purchasing vitamins.");
        }
    }

    /// <summary>
    ///     Buys rare candy and uses it on the selected Pokémon, one level each, up to level 100.
    /// </summary>
    /// <param name="amount">How many to buy.</param>
    [Command("shop buy candy")]
    [Alias("buy candy", "items buy candy")]
    [Summary("Buy rare candy to level up your selected pokemon.")]
    public async Task BuyCandy(int amount = 1)
    {
        try
        {
            await SendResultAsync(await Service.BuyCandyAsync(ctx.User.Id, amount));
        }
        catch (Exception e)
        {
            Log.Error(e, "Error buying candy for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while purchasing rare candy.");
        }
    }

    /// <summary>
    ///     Buys a radiant chest with credits or redeems, after a confirmation.
    /// </summary>
    /// <param name="chestType">rare, mythic or legend.</param>
    /// <param name="currency">credits or redeems.</param>
    [Command("shop buy chest")]
    [Alias("buy chest", "items buy chest")]
    [Summary("Buy a radiant chest: rare, mythic or legend, with credits or redeems.")]
    public async Task BuyChest(string chestType, string currency)
    {
        var type = chestType.ToLowerInvariant();
        var cur = currency.ToLowerInvariant();
        if (!ChestTypes.Contains(type))
        {
            await ErrorAsync($"`{chestType}` is not a valid chest type! Choose one of Rare, Mythic, or Legend.");
            return;
        }

        if (!Currencies.Contains(cur))
        {
            await ErrorAsync("Specify either \"credits\" or \"redeems\"!");
            return;
        }

        try
        {
            if (!await PromptConfirmAsync($"Are you sure you want to buy a {type} chest with {cur}?"))
            {
                await ConfirmAsync("Purchase cancelled.");
                return;
            }

            await SendResultAsync(await Service.BuyChestAsync(ctx.User.Id, type, cur));
        }
        catch (Exception e)
        {
            Log.Error(e, "Error buying chest for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while purchasing the chest.");
        }
    }

    /// <summary>
    ///     Buys redeems at 60,000 credits each, or shows this week's purchases when no amount is given.
    /// </summary>
    /// <param name="amount">How many to buy.</param>
    [Command("shop buy redeems")]
    [Alias("buy redeems", "items buy redeems")]
    [Summary("Buy redeems for credits, or see how many you bought this week.")]
    public async Task BuyRedeems(int? amount = null)
    {
        try
        {
            await SendResultAsync(await Service.BuyRedeemsAsync(ctx.User.Id, (ulong?)amount));
        }
        catch (Exception e)
        {
            Log.Error(e, "Error buying redeems for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while purchasing redeems.");
        }
    }

    private static EmbedBuilder AccessDeniedEmbed() =>
        new EmbedBuilder()
            .WithTitle("🔒 Radiant Shop")
            .WithDescription("**Access Requirements:**\n" +
                             $"• Minimum {ShopConstants.MinimumPokemonRequired} Pokemon in collection\n\n" +
                             "The radiant shop contains rare and powerful items for experienced trainers. " +
                             "Keep collecting Pokemon to unlock access!")
            .WithColor(Color.Orange);

    private static string? FindCategory(string name) =>
        name.ToLowerInvariant().Replace(" ", "").Replace("-", "") switch
        {
            "general" or "items" => "General",
            "radiant" => "Radiant",
            "crystal" or "crystalslime" or "slime" => "CrystalSlime",
            _ => null
        };

    private static string TypeEmoji(ShopItem item) =>
        !string.IsNullOrEmpty(item.PokemonType) && ShopConstants.TypeEmojis.TryGetValue(item.PokemonType, out var emoji)
            ? emoji
            : "";

    private static string FormatItem(ShopItem item, int userCredits, bool slime)
    {
        var stockInfo = item.Stock == -1 ? "∞" : item.Stock.ToString();
        var buyName = item.Metadata.ContainsKey("MongoId") ? item.Name : item.Id;
        var price = slime
            ? $"{Emojis.CrystallizedSlime} {item.Price:N0}"
            : $"{ShopConstants.Emojis.Credits} {item.Price:N0}";
        var affordableIcon = slime ? "" : item.Price <= userCredits ? "✅ " : "❌ ";

        return $"**{affordableIcon}{TypeEmoji(item)} {item.Name}** `{buyName}`\n" +
               $"{item.Description}\n" +
               $"**Price:** {price} | **Stock:** {stockInfo} | **Rarity:** {item.Rarity}";
    }

    private static string WithPrefix(string text, string prefix) => text.Replace("`/", $"`{prefix}");

    private async Task SendPrivateOrReplyAsync(EmbedRequest embed) =>
        await SendResultAsync(CommandResult.WithEmbed(embed, true));

    private async Task SendResultAsync(CommandResult result)
    {
        var prefix = await GetPrefixAsync();
        var text = string.IsNullOrEmpty(result.Message) ? null : WithPrefix(result.Message, prefix);
        if (result.Embed is { Description: { } description })
            result.Embed.Description = WithPrefix(description, prefix);
        List<EmbedRequest>? embeds = result.Embed is null ? null : [result.Embed];

        if (!result.Ephemeral || embeds is null)
        {
            await ReplyAsync(text, embeds);
            return;
        }

        try
        {
            await ctx.Client.SendDirectMessageAsync(ctx.User.Id, text, embeds);
        }
        catch (FluxerApiException)
        {
            await ReplyAsync($"{ctx.User.Mention}, open your DMs so I can send you this privately next time.\n{text}",
                embeds);
            return;
        }

        try
        {
            await ctx.Message.AddReactionAsync("📬");
        }
        catch (FluxerApiException)
        {
        }
    }
}
