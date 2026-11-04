using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EeveeCore.Common.Interactive;
using EeveeCore.Common.ModuleBases;
using EeveeCore.Database.Linq.Models.Pokemon;
using EeveeCore.Modules.Extras.Services;
using EeveeCore.Modules.Pokemon.Services;
using EeveeCore.Services.Impl;
using LinqToDB;
using LinqToDB.Async;
using MongoDB.Driver;
using Serilog;
using DbUser = EeveeCore.Database.Linq.Models.Bot.User;

namespace EeveeCore.Modules.Extras;

/// <summary>
///     Trainer settings, balances, the bag, and other extras.
/// </summary>
/// <param name="dbContextProvider">The database context provider.</param>
/// <param name="pokemonService">The Pokémon service, for sprites.</param>
/// <param name="botLog">The bot event log.</param>
[Name("Extras")]
public class ExtrasModule(
    LinqToDbConnectionProvider dbContextProvider,
    PokemonService pokemonService,
    BotLog botLog) : EeveeModule<ExtrasService>
{
    private static readonly string[] Regions = ["original", "alola", "galar", "hisui", "paldea"];

    private static readonly string[] Rods =
        ["old-rod", "new-rod", "good-rod", "super-rod", "ultra-rod", "supreme-rod", "epic-rod", "master-rod"];

    private static readonly string[] BalanceTabs = ["💰", "📦", "📋", "🪙"];

    /// <summary>
    ///     Sets the user's active region, which affects regional Pokémon forms.
    /// </summary>
    /// <param name="location">The region to set.</param>
    [Command("region")]
    [Summary("Set your region, affects your pokemon's regional evolutions.")]
    public async Task Region(string location)
    {
        var region = location.ToLower();
        if (!Regions.Contains(region))
        {
            await ErrorAsync($"That is not a region. Pick one of: {string.Join(", ", Regions.Select(r => r.Capitalize()))}.");
            return;
        }

        await using var db = await dbContextProvider.GetConnectionAsync();

        await db.Users.Where(u => u.UserId == ctx.User.Id)
            .Set(x => x.Region, region)
            .UpdateAsync();

        await ConfirmAsync($"Your region has been set to **{region.Capitalize()}**.");
    }

    /// <summary>
    ///     Sets the trainer nickname for the user.
    /// </summary>
    /// <param name="name">The nickname to set.</param>
    [Command("trainernick")]
    [Summary("Sets your trainer nickname for use in global leaderboards and other users.")]
    public async Task TrainerNick([Remainder] string name)
    {
        if (name.Contains("@here") || name.Contains("@everyone") || name.Contains("http"))
        {
            await ErrorAsync("Nope.");
            return;
        }

        if (name.Length > 18)
        {
            await ErrorAsync("Trainer nick too long!");
            return;
        }

        if (!Regex.IsMatch(name, @"^[ -~]*$"))
        {
            await ErrorAsync("Unicode characters cannot be used in your trainer nick.");
            return;
        }

        if (name.Contains('|'))
        {
            await ErrorAsync("`|` cannot be used in your trainer nick.");
            return;
        }

        await using var db = await dbContextProvider.GetConnectionAsync();

        var userNick = await db.Users
            .Where(u => u.UserId == ctx.User.Id)
            .Select(u => u.TrainerNickname)
            .FirstOrDefaultAsync();

        if (userNick != null)
        {
            await ErrorAsync("You have already set your trainer nick.");
            return;
        }

        var existingUser = await db.Users
            .Where(u => u.TrainerNickname == name)
            .Select(u => u.UserId)
            .FirstOrDefaultAsync();

        if (existingUser != 0)
        {
            await ErrorAsync("That nick is already taken. Try another one.");
            return;
        }

        await db.Users.Where(u => u.UserId == ctx.User.Id)
            .Set(x => x.TrainerNickname, name)
            .UpdateAsync();

        await ConfirmAsync("Successfully Changed Trainer Nick");
    }

    /// <summary>
    ///     Sets the Pokémon the user is hunting for shadow encounters.
    /// </summary>
    /// <param name="pokemon">The Pokémon to hunt.</param>
    [Command("hunt")]
    [Summary("Set a Pokemon to hunt for shadow encounters.")]
    public async Task Hunt([Remainder] string pokemon)
    {
        var identifier = pokemon.Trim().ToLower().Replace(" ", "-");
        var names = await Service.GetPokemonNames();

        if (!names.Contains(identifier))
        {
            var guess = ExtrasService.FindClosest(identifier, names);
            await ErrorAsync("You have chosen an invalid Pokemon." +
                             (guess is null ? "" : $"\nDid you mean **{guess.Capitalize()}**?"));
            return;
        }

        pokemon = identifier.Capitalize()!;

        await using var db = await dbContextProvider.GetConnectionAsync();

        var userData = await db.Users
            .Where(u => u.UserId == ctx.User.Id)
            .Select(u => new { u.Hunt, u.Chain })
            .FirstOrDefaultAsync();

        if (userData == null)
        {
            await ErrorAsync($"You have not started!\nStart with `{await GetPrefixAsync()}start` first.");
            return;
        }

        var hunt = userData.Hunt;
        var chain = userData.Chain;

        if (hunt == pokemon)
        {
            await ErrorAsync("You are already hunting that pokemon!");
            return;
        }

        var addChain = 0;

        if (chain > 0 && !string.IsNullOrEmpty(hunt))
        {
            var confirmed = await PromptConfirmAsync(
                $"Are you sure you want to abandon your hunt for **{hunt}**?\nYou will lose your streak of **{chain}**.");

            if (!confirmed) return;
        }
        else if (chain > 0 && string.IsNullOrEmpty(hunt))
        {
            addChain = 500;
            await SendAsync("Binding loose chain to new Pokemon.");
        }

        await db.Users.Where(u => u.UserId == ctx.User.Id)
            .Set(x => x.Hunt, pokemon)
            .Set(x => x.Chain, addChain)
            .UpdateAsync();

        var embed = new EmbedBuilder()
            .WithTitle("Shadow Hunt")
            .WithDescription($"Successfully changed shadow hunt selection to **{pokemon}**.")
            .WithColor(0xFFB6C1u);

        var attachments = new List<AttachmentRequest>();
        SetImage(embed, await FindSpriteAsync(identifier, "shadow"), "hunt.png", attachments);

        await SendAsync(embed: embed, attachments: attachments);
        await botLog.SendAsync($"`{ctx.User.Id} - {hunt} @ {chain}x -> {pokemon}`");
    }

    /// <summary>
    ///     Equips a fishing rod from the user's bag.
    /// </summary>
    /// <param name="fishingRod">The fishing rod to equip.</param>
    [Command("equip rod")]
    [Alias("rod")]
    [Priority(1)]
    [Summary("Equip a fishing pole from your bag.")]
    public async Task EquipFishingRod([Remainder] string fishingRod)
    {
        var rodName = fishingRod.ToLower().Replace("-", " ").Replace("rod", "").Trim();
        var normalizedItem = rodName + "-rod";

        if (!Rods.Contains(normalizedItem))
        {
            await ErrorAsync("Not a valid Fishing rod, please try again.\nRods: " +
                             string.Join(", ", Rods.Select(r => r.Replace("-rod", "").Capitalize())));
            return;
        }

        await using var db = await dbContextProvider.GetConnectionAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == ctx.User.Id);
        if (user == null)
        {
            await ErrorAsync($"You have not Started!\nStart with `{await GetPrefixAsync()}start` first!");
            return;
        }

        var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Items ?? "{}") ??
                        new Dictionary<string, int>();

        if (!inventory.TryGetValue(normalizedItem, out var count) || count < 1)
        {
            await ErrorAsync($"You don't have {rodName.Capitalize()} rod in your inventory.");
            return;
        }

        var fishLevel = user.FishingLevel ?? 0;

        if (normalizedItem == "supreme-rod" && fishLevel < 105)
        {
            await ErrorAsync("You need to be fishing level 105 to use this item!");
            return;
        }

        if (normalizedItem == "epic-rod" && fishLevel < 150)
        {
            await ErrorAsync("You need to be fishing level 150 to use this item!");
            return;
        }

        if (normalizedItem == "master-rod" && fishLevel < 200)
        {
            await ErrorAsync("You need to be fishing level 200 to use this item!");
            return;
        }

        await db.Users.Where(u => u.UserId == ctx.User.Id)
            .Set(u => u.HeldItem, normalizedItem)
            .UpdateAsync();

        await ConfirmAsync($"You have successfully equipped {rodName.Capitalize()} rod.");
    }

    /// <summary>
    ///     Shows a user's balance, with reactions to switch to their chests, details and tokens.
    /// </summary>
    /// <param name="user">The user to show, or yourself.</param>
    [Command("bal")]
    [Summary("Lists credits, redeems, EV points, upvote points, and selected fishing rod. " +
             "React 💰 for balances, 📦 for chests, 📋 for details and 🪙 for tokens.")]
    public async Task Balance(User? user = null)
    {
        user ??= ctx.User;

        await using var db = await dbContextProvider.GetConnectionAsync();

        var details = await db.Users.FirstOrDefaultAsync(u => u.UserId == user.Id);
        if (details == null)
        {
            await ErrorAsync($"{user.Username} has not started!");
            return;
        }

        if (!details.Visible.GetValueOrDefault() && user.Id != ctx.User.Id)
        {
            await ErrorAsync($"You are not permitted to see the Trainer card of {user.Username}");
            return;
        }

        var trainerNick = details.TrainerNickname ?? user.Username;
        var staffRank = details.Staff;
        var color = AccentColor(staffRank);
        var prefix = await GetPrefixAsync();

        var marketUsed = await db.Market.CountAsync(m => m.OwnerId == user.Id && m.BuyerId == null);
        var daycared = await db.UserPokemonOwnerships
            .Join(db.UserPokemon,
                o => o.PokemonId,
                p => p.Id,
                (o, p) => new { Ownership = o, Pokemon = p })
            .CountAsync(j => j.Ownership.UserId == user.Id && j.Pokemon.PokemonName == "Egg");
        var pokemonCount = await db.UserPokemonOwnerships.CountAsync(o => o.UserId == user.Id);

        var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(details.Inventory ?? "{}") ??
                        new Dictionary<string, int>();

        var tabs = new[]
        {
            BalanceMain(details, trainerNick),
            BalanceChests(inventory, staffRank, trainerNick),
            BalanceMisc(details, inventory, trainerNick, marketUsed, daycared, pokemonCount, prefix),
            BalanceTokens(details, trainerNick, user.Username)
        };
        foreach (var tab in tabs)
            tab.WithColor(color).WithFooter("💰 Main | 📦 Chests | 📋 Details | 🪙 Tokens");

        var message = await SendAsync(embed: tabs[0]);
        var adding = ReactionService.AddReactionsAsync(message, BalanceTabs);

        var current = 0;
        while (await Reactions.WaitForPressAsync(message.Id, ctx.User.Id, BalanceTabs, TimeSpan.FromMinutes(2))
               is { } pressed)
        {
            var index = Array.IndexOf(BalanceTabs, pressed);
            if (index == current)
                continue;

            current = index;
            try
            {
                await message.ModifyAsync("", [tabs[index].Build()]);
            }
            catch (FluxerApiException ex)
            {
                Log.Debug(ex, "Could not switch the balance tab of message {MessageId}", message.Id);
                break;
            }
        }

        await adding;
        await ReactionService.TryClearReactionsAsync(message);
    }

    /// <summary>
    ///     Sends the items in the user's bag to their DMs.
    /// </summary>
    [Command("bag")]
    [Summary("Lists your items. They are sent to your DMs.")]
    public async Task Bag()
    {
        await using var db = await dbContextProvider.GetConnectionAsync();

        var items = await db.Users
            .Where(u => u.UserId == ctx.User.Id)
            .Select(u => u.Items)
            .FirstOrDefaultAsync();

        if (items == null)
        {
            await ErrorAsync($"You have not Started!\nStart with `{await GetPrefixAsync()}start` first!");
            return;
        }

        var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(items) ??
                        new Dictionary<string, int>();

        var lines = inventory.Keys.OrderBy(k => k)
            .Where(item => inventory[item] > 0)
            .Select(item => $"{item.Replace("-", " ").Capitalize()} : {inventory[item]}x")
            .ToList();

        var desc = new StringBuilder();
        foreach (var line in lines)
        {
            if (desc.Length + line.Length > 3900)
            {
                desc.AppendLine($"... and {lines.Count - lines.IndexOf(line)} more");
                break;
            }

            desc.AppendLine(line);
        }

        var embed = new EmbedBuilder()
            .WithTitle("Your Current Bag")
            .WithColor(0x5D3FD3u)
            .WithDescription(desc.Length == 0 ? "Nothin here.." : desc.ToString());

        if (ctx.Guild is null)
        {
            await SendAsync(embed: embed);
            return;
        }

        if (await SendPrivateAsync(embed: embed))
            await ConfirmAsync("I sent your bag to your DMs.");
        else
            await SendAsync("Open your DMs to see your bag privately next time.", embed);
    }

    private async Task<string?> FindSpriteAsync(string identifier, string skin)
    {
        try
        {
            var (_, path) = await pokemonService.GetPokemonFormInfo(identifier, skin: skin);
            if (File.Exists(path))
                return path;

            (_, path) = await pokemonService.GetPokemonFormInfo(identifier);
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Color AccentColor(string staffRank) => staffRank.ToLower() switch
    {
        "gym" => Color.Orange,
        "user" => new Color(0xFFB6C1),
        _ => Color.Gold
    };

    private static string SubTitle(string staffRank, string trainerNick) => staffRank.ToLower() switch
    {
        "gym" => $"{trainerNick} - Official Gym Leader",
        "user" => trainerNick,
        _ => $"{trainerNick} - Official Staff Member"
    };

    private static EmbedBuilder BalanceMain(DbUser details, string trainerNick)
    {
        var voteStreak = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - details.LastVote > 36 * 60 * 60
            ? 0
            : details.VoteStreak;

        var staffRank = details.Staff;
        var title = staffRank.ToLower() switch
        {
            "gym" => $"{trainerNick} - Official Gym Leader",
            "user" => $"{trainerNick} - Trainer Information",
            _ => $"{trainerNick} - Official Staff Member ({staffRank})"
        };

        var balanceText = $"**Credits:** {details.MewCoins:N0}\n" +
                          $"**Redeems:** {details.Redeems}\n" +
                          $"**Mystery Tokens:** {details.MysteryToken}\n" +
                          $"**Dittopia Rep.:** {details.OsRep}";

        var energy = Math.Clamp(details.Energy ?? 10, 0, 10);
        var progressText = $"**Energy:** {ExtrasService.ProgressBar(energy, 10)} ({energy}/10)\n" +
                           $"**EV Points:** {details.EvPoints}\n" +
                           $"**Upvote Points:** {details.UpvotePoints}\n" +
                           $"**Vote Streak:** {voteStreak}\n" +
                           $"**Holding:** {details.HeldItem.Capitalize()?.Replace("-", " ")}\n" +
                           $"**Region:** {details.Region.Capitalize()}";

        if (details.Voucher > 0)
            progressText += $"\n**Unused Vouchers:** {details.Voucher}";

        return new EmbedBuilder()
            .WithTitle(title)
            .AddField("Balances", balanceText)
            .AddField("Game Progress", progressText);
    }

    private static EmbedBuilder BalanceChests(Dictionary<string, int> inventory, string staffRank,
        string trainerNick)
    {
        var chestsText = $"**Legend:** {inventory.GetValueOrDefault("legend chest", 0)}\n" +
                         $"**Mythic:** {inventory.GetValueOrDefault("mythic chest", 0)}\n" +
                         $"**Rare:** {inventory.GetValueOrDefault("rare chest", 0)}\n" +
                         $"**Common:** {inventory.GetValueOrDefault("common chest", 0)}";

        return new EmbedBuilder()
            .WithTitle($"{SubTitle(staffRank, trainerNick)}'s Chests")
            .AddField("Chest Inventory", chestsText);
    }

    private static EmbedBuilder BalanceTokens(DbUser details, string trainerNick,
        string username)
    {
        var embed = new EmbedBuilder().WithTitle($"{SubTitle(details.Staff, trainerNick)}'s Tokens");

        if (string.IsNullOrEmpty(details.Tokens))
            return embed.WithDescription($"{username} has no tokens.");

        var tokens = JsonSerializer.Deserialize<Dictionary<string, int>>(details.Tokens) ??
                     new Dictionary<string, int>();

        var tokensList = tokens.Where(t => t.Value > 0).Select(t => $"**{t.Key}:** {t.Value}").ToList();

        return embed.AddField("Your Tokens",
            tokensList.Count > 0 ? string.Join("\n", tokensList) : "*You don't have any tokens yet.*");
    }

    private EmbedBuilder BalanceMisc(DbUser details, Dictionary<string, int> inventory,
        string trainerNick, int marketUsed, int daycared, int pokemonCount, string prefix)
    {
        var bike = details.Bike ?? false;
        var daycareLimit = details.DaycareLimit ?? 1;
        var hunt = details.Hunt;
        var fishingLevel = details.FishingLevel ?? 1;
        var fishingExp = details.FishingExp ?? 0;
        var fishingLevelCap = details.FishingLevelCap ?? 50;
        var energyValue = Math.Clamp(details.Energy ?? 10, 0, 10);

        var accountText = $"**Market Slots:** {marketUsed}/{details.MarketLimit}\n" +
                          $"**Daycare:** {daycared}/{daycareLimit}\n" +
                          $"**Pokemon Owned:** {pokemonCount:N0}\n" +
                          $"**Bicycle:** {(bike ? "Yes" : "No")}";

        var huntText = !string.IsNullOrEmpty(hunt)
            ? $"**Target:** {hunt}\n**Chain:** {details.Chain}x"
            : $"*No active hunt - use `{prefix}hunt` to start*";

        var statsText = $"**Energy:** {ExtrasService.ProgressBar(energyValue, 10)} ({energyValue}/10)\n" +
                        $"**Fishing Level:** {fishingLevel}\n" +
                        $"**Fishing Exp:** {fishingExp}/{fishingLevelCap}\n" +
                        $"**Progress:** {ExtrasService.ProgressBar(fishingExp, fishingLevelCap)}";

        var embed = new EmbedBuilder()
            .WithTitle($"{SubTitle(details.Staff, trainerNick)}'s Details")
            .AddField("Account Stats", accountText)
            .AddField("Shadow Hunt", huntText)
            .AddField("Energy & Fishing", statsText);

        var items = inventory.Where(i => i.Key != "coin-case" && !IsChestItem(i.Key)).ToList();
        var inventoryItems = items.Take(10).Select(pair =>
        {
            var (item, count) = pair;
            var itemName = item.Replace("-", " ").Capitalize();
            if (item.Contains("breeding"))
                return $"**{itemName}:** {count} (x{Service.CalculateBreedingMultiplier(count)})";
            if (item.Contains("iv"))
                return $"**{itemName}:** {count} (x{Service.CalculateIvMultiplier(count)})";
            return $"**{itemName}:** {count}";
        }).ToList();

        if (inventoryItems.Count > 0)
        {
            var inventoryText = string.Join("\n", inventoryItems);
            if (items.Count > 10)
                inventoryText += $"\n*... and {items.Count - 10} more items*";

            embed.AddField("Inventory", inventoryText);
        }

        return embed;
    }

    private static bool IsChestItem(string item) =>
        item.Contains("common chest") ||
        item.Contains("rare chest") ||
        item.Contains("mythic chest") ||
        item.Contains("legend chest") ||
        item.Contains("spooky chest") ||
        item.Contains("fleshy chest") ||
        item.Contains("ghost detector") ||
        item.Contains("horrific chest") ||
        item.Contains("heart chest");

    /// <summary>
    ///     Commands for spreading Honey in channels.
    /// </summary>
    /// <param name="dbContextProvider">The database context provider.</param>
    [Group("spread")]
    [Name("Spread")]
    [RequireContext(ContextType.Guild)]
    public class SpreadCommands(LinqToDbConnectionProvider dbContextProvider) : EeveeModule<ExtrasService>
    {
        /// <summary>
        ///     Spreads honey in the current channel to increase legendary and rare spawn rates.
        /// </summary>
        [Command("honey")]
        [Summary("Spread honey in the current channel to increase legendary and rare spawn rates.")]
        public async Task SpreadHoney()
        {
            await using var db = await dbContextProvider.GetConnectionAsync();

            var inventory = await db.Users.FirstOrDefaultAsync(u => u.UserId == ctx.User.Id);

            if (inventory == null)
            {
                await ErrorAsync($"You have not Started!\nStart with `{await GetPrefixAsync()}start` first!");
                return;
            }

            var honey = await db.Honey.FirstOrDefaultAsync(h => h.ChannelId == ctx.Channel.Id);

            if (honey != null)
            {
                await ErrorAsync($"There is already honey in this channel!\n🕐**Ends:** <t:{honey.Expires}:R> 🕐");
                return;
            }

            var inventoryDict = JsonSerializer.Deserialize<Dictionary<string, int>>(inventory.Inventory ?? "{}")
                                ?? new Dictionary<string, int>();

            if (!inventoryDict.TryGetValue("honey", out var honeyCount) || honeyCount < 1)
            {
                await ErrorAsync("You do not have any units of Honey!");
                return;
            }

            inventoryDict["honey"]--;
            var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

            await db.InsertAsync(new Honey
            {
                ChannelId = ctx.Channel.Id,
                Expires = (int)expires,
                OwnerId = ctx.User.Id,
                Type = "honey"
            });

            inventory.Inventory = JsonSerializer.Serialize(inventoryDict);
            await db.UpdateAsync(inventory);

            await ConfirmAsync(
                $"You have successfully spread some of your honey!\n> 🕐**Lasts for:** <t:{expires}:R> 🕐");
        }

        /// <summary>
        ///     Displays active honey in the current server.
        /// </summary>
        [Command("server")]
        [Summary("Community Honey Stats.")]
        public async Task SpreadServer()
        {
            await using var db = await dbContextProvider.GetConnectionAsync();
            var channelIds = ctx.Guild!.Channels.Keys.ToList();
            var honeyLocations = await db.Honey
                .Where(h => channelIds.Contains(h.ChannelId))
                .ToListAsync();

            StringBuilder desc = new();
            foreach (var honey in honeyLocations)
            {
                var honeyType = honey.Type switch
                {
                    "honey" => "Honey",
                    "ghost" => "Ghost Detector",
                    "cheer" => "Christmas Cheer",
                    _ => honey.Type
                };

                desc.AppendLine($"**{honeyType}** active in:");
                desc.AppendLine($"<#{honey.ChannelId}> -> `Expires:` <t:{honey.Expires}:R>\n");
            }

            var embed = new EmbedBuilder()
                .WithTitle("Community Stats")
                .WithColor(0xFFBC61u)
                .WithDescription(desc.Length == 0 ? "There is no honey active in this community." : desc.ToString());

            await SendAsync(embed: embed);
        }
    }

    /// <summary>
    ///     Commands for setting Pokémon properties.
    /// </summary>
    /// <param name="dbContextProvider">The database context provider.</param>
    [Group("set")]
    [Name("Set")]
    public class SetCommands(LinqToDbConnectionProvider dbContextProvider) : EeveeModule<ExtrasService>
    {
        private static readonly List<string> NatureList =
        [
            "Hardy", "Lonely", "Brave", "Adamant", "Naughty",
            "Bold", "Docile", "Relaxed", "Impish", "Lax",
            "Timid", "Hasty", "Serious", "Jolly", "Naive",
            "Modest", "Mild", "Quiet", "Bashful", "Rash",
            "Calm", "Gentle", "Sassy", "Careful", "Quirky"
        ];

        /// <summary>
        ///     Uses a nature capsule to change the selected Pokémon's nature.
        /// </summary>
        /// <param name="nature">The name of the nature to set.</param>
        [Command("nature")]
        [Summary("Uses a nature capsule to change your selected Pokemon's nature.")]
        public async Task SetNature(string nature)
        {
            nature = nature.ToLower().Capitalize()!;
            if (!NatureList.Contains(nature))
            {
                var guess = ExtrasService.FindClosest(nature, NatureList);
                await ErrorAsync("That Nature does not exist!" + (guess is null ? "" : $"\nDid you mean **{guess}**?"));
                return;
            }

            await using var db = await dbContextProvider.GetConnectionAsync();
            var prefix = await GetPrefixAsync();

            var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == ctx.User.Id);
            if (user == null)
            {
                await ErrorAsync($"You have not Started!\nStart with `{prefix}start` first!");
                return;
            }

            var inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(user.Inventory ?? "{}")
                            ?? new Dictionary<string, int>();

            if (!inventory.TryGetValue("nature-capsules", out var capsules) || capsules <= 0)
            {
                await ErrorAsync($"You have no nature capsules! Buy some with `{prefix}redeem nature capsules`.");
                return;
            }

            inventory["nature-capsules"]--;

            var selectedPokemon = await db.UserPokemon.FirstOrDefaultAsync(p => p.Id == user.Selected);
            if (selectedPokemon == null)
            {
                await ErrorAsync("You don't have a Pokémon selected!");
                return;
            }

            user.Inventory = JsonSerializer.Serialize(inventory);
            await db.UpdateAsync(user);

            await db.UserPokemon
                .Where(p => p.Id == user.Selected)
                .Set(p => p.Nature, nature)
                .UpdateAsync();

            await ConfirmAsync($"You have successfully changed your selected Pokemon's nature to {nature}");
        }

        /// <summary>
        ///     Sets or resets the nickname of the selected Pokémon.
        /// </summary>
        /// <param name="nick">The new nickname, or "None" to reset.</param>
        [Command("nick")]
        [Summary("Set or reset your selected pokemon's nickname. Leave it out to reset.")]
        public async Task SetNick([Remainder] string nick = "None")
        {
            await using var db = await dbContextProvider.GetConnectionAsync();
            var selectedId = await db.Users
                .Where(u => u.UserId == ctx.User.Id)
                .Select(u => u.Selected)
                .FirstOrDefaultAsync();

            if (selectedId is null or 0)
            {
                await ErrorAsync("You don't have a Pokémon selected!");
                return;
            }

            var (success, message) = await Service.SetNicknameAsync(ctx.User.Id, selectedId.Value, nick);
            if (success)
                await ConfirmAsync(message);
            else
                await ErrorAsync(message);
        }
    }

    /// <summary>
    ///     Commands for toggling visibility settings.
    /// </summary>
    /// <param name="dbContextProvider">The database context provider.</param>
    [Group("visible")]
    [Name("Visible")]
    public class VisibleCommands(LinqToDbConnectionProvider dbContextProvider) : EeveeModule<ExtrasService>
    {
        /// <summary>
        ///     Toggles visibility of the user's balance to other users.
        /// </summary>
        [Command("bal")]
        [Summary("Toggle your balance being visible to other users.")]
        public async Task VisibleBalance()
        {
            await using var db = await dbContextProvider.GetConnectionAsync();

            await db.Users.Where(u => u.UserId == ctx.User.Id)
                .Set(u => u.Visible, u => !u.Visible)
                .UpdateAsync();

            await ConfirmAsync("Toggled trainer card visibility!");
        }

        /// <summary>
        ///     Toggles visibility of the user's donation total on balance statements.
        /// </summary>
        /// <param name="toggle">True to show donations, false to hide them.</param>
        [Command("donations")]
        [Summary("Toggle your donation total being shown on your balance statement.")]
        public async Task VisibleDonations(bool toggle = false)
        {
            await using var db = await dbContextProvider.GetConnectionAsync();

            await db.Users.Where(u => u.UserId == ctx.User.Id)
                .Set(u => u.ShowDonations, toggle)
                .UpdateAsync();

            if (toggle)
                await ConfirmAsync("Your donations total will now show on your balance.");
            else
                await ConfirmAsync("Your donations total will no longer show on your balance.");
        }
    }

    /// <summary>
    ///     Commands for looking up Pokémon data.
    /// </summary>
    /// <param name="mongoService">The MongoDB service.</param>
    [Group("lookup")]
    [Name("Lookup")]
    public class LookupCommands(IMongoService mongoService) : EeveeModule<ExtrasService>
    {
        private const int AbilityPageSize = 25;

        private static readonly Dictionary<string, uint> Elements = new()
        {
            ["normal"] = 0xA9A87A,
            ["fire"] = 0xEE7D39,
            ["water"] = 0x6A91ED,
            ["grass"] = 0x7BC856,
            ["electric"] = 0xF7CF41,
            ["ice"] = 0x9AD8D8,
            ["fighting"] = 0xBE2C2D,
            ["poison"] = 0x9E409F,
            ["ground"] = 0xDFBF6E,
            ["flying"] = 0xA891EE,
            ["psychic"] = 0xF65689,
            ["bug"] = 0xA8B831,
            ["rock"] = 0xB69F40,
            ["ghost"] = 0x705796,
            ["dragon"] = 0x6F3CF5,
            ["dark"] = 0x6E5849,
            ["steel"] = 0xB8B8CF,
            ["fairy"] = 0xF6C9E3
        };

        private static readonly Dictionary<int, string> TypeIds = new()
        {
            [1] = "Normal",
            [2] = "Fighting",
            [3] = "Flying",
            [4] = "Poison",
            [5] = "Ground",
            [6] = "Rock",
            [7] = "Bug",
            [8] = "Ghost",
            [9] = "Steel",
            [10] = "Fire",
            [11] = "Water",
            [12] = "Grass",
            [13] = "Electric",
            [14] = "Psychic",
            [15] = "Ice",
            [16] = "Dragon",
            [17] = "Dark",
            [18] = "Fairy"
        };

        /// <summary>
        ///     Looks up information about a Pokémon move.
        /// </summary>
        /// <param name="move">The name of the move to look up.</param>
        [Command("move")]
        [Summary("Look up information about a Pokémon move.")]
        public async Task LookupMove([Remainder] string move)
        {
            move = move.Trim().ToLower().Replace(" ", "-");

            var exists = await mongoService.Moves.Find(x => x.Identifier == move).FirstOrDefaultAsync();
            if (exists == null)
            {
                var names = await mongoService.Moves.Find(_ => true).Project(m => m.Identifier).ToListAsync();
                var guess = ExtrasService.FindClosest(move, names);
                await ErrorAsync("That move does not exist!" +
                                 (guess is null ? "" : $"\nDid you mean **{guess.Replace("-", " ").Capitalize()}**?"));
                return;
            }

            var prio = exists.Priority;
            var pp = exists.PP;
            var type =
                (await mongoService.Types.Find(t => t.TypeId == exists.TypeId).FirstOrDefaultAsync())?.Identifier
                .Capitalize() ?? "Unknown";
            var acc = exists.Accuracy;
            var power = exists.Power;
            var dclass = exists.DamageClassId switch
            {
                1 => "Status",
                2 => "Physical",
                3 => "Special",
                _ => "Unknown"
            };

            var embed = new EmbedBuilder()
                .WithTitle(move.Capitalize()!.Replace("-", " "))
                .WithColor(Elements.GetValueOrDefault(type.ToLower(), 0x000001u))
                .WithDescription($"**Damage Class:** `{dclass}` " +
                                 (power.HasValue ? $"| **Power:** `{power}`" : "") +
                                 $"\n**Accuracy:** `{acc}` " +
                                 $"| **Type:** `{type}` " +
                                 $"| **PP:** `{pp}` " +
                                 (prio != 0 ? $"\n**Priority:** `{prio}`" : ""));

            var effectsDescription = exists.EffectChance != null
                ? $"Effect chance: {exists.EffectChance}%"
                : "No effect information available.";

            embed.AddField("Effect", effectsDescription);

            await SendAsync(embed: embed);
        }

        /// <summary>
        ///     Looks up information about a Pokémon ability and the Pokémon that have it.
        /// </summary>
        /// <param name="ability">The ability, optionally followed by a page of the Pokémon list.</param>
        [Command("ability")]
        [Summary("Look up information about a Pokémon ability. Add a page number to jump to a page of its Pokémon.")]
        public async Task LookupAbility([Remainder] string ability)
        {
            var words = ability.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var page = 1;
            if (words.Count > 1 && int.TryParse(words[^1], out var parsed))
            {
                page = parsed;
                words.RemoveAt(words.Count - 1);
            }

            ability = string.Join("-", words).ToLower();

            var exists = await mongoService.Abilities.Find(x => x.Identifier == ability).FirstOrDefaultAsync();
            if (exists == null)
            {
                var names = await mongoService.Abilities.Find(_ => true).Project(a => a.Identifier).ToListAsync();
                var guess = ExtrasService.FindClosest(ability, names);
                await ErrorAsync("That ability does not exist!" +
                                 (guess is null ? "" : $"\nDid you mean **{guess.Replace("-", " ").Capitalize()}**?"));
                return;
            }

            var title = ability.Capitalize()!.Replace("-", " ");
            var embed = new EmbedBuilder()
                .WithTitle(title)
                .WithColor(0xF699CDu)
                .AddField("Effect", "Effect information is stored in the database");

            await SendAsync(embed: embed);

            var pokeWithAbility = await mongoService.PokeAbilities
                .Find(a => a.AbilityId == exists.AbilityId)
                .ToListAsync();

            var pokemonIds = pokeWithAbility.Select(p => p.PokemonId).ToList();

            var pokemonList = await mongoService.Forms
                .Find(f => pokemonIds.Contains(f.PokemonId))
                .Project(f => f.Identifier)
                .ToListAsync();

            if (pokemonList.Count == 0)
                return;

            var pageCount = (pokemonList.Count + AbilityPageSize - 1) / AbilityPageSize;
            await Reactions.PaginateAsync(ctx.Channel, ctx.User.Id, pageCount, index =>
            {
                var pageEmbed = new EmbedBuilder()
                    .WithTitle("Pokemon with " + title)
                    .WithColor(0xF699CDu)
                    .WithDescription(string.Join("\n",
                        pokemonList.Skip(index * AbilityPageSize).Take(AbilityPageSize).Select(p => p.Capitalize())))
                    .WithFooter($"Page {index + 1}/{pageCount}");
                return Task.FromResult(new Page(null, [pageEmbed.Build()]));
            }, startPage: page - 1);
        }

        /// <summary>
        ///     Looks up type effectiveness information for one or two Pokémon types.
        /// </summary>
        /// <param name="type1">The first type.</param>
        /// <param name="type2">An optional second type.</param>
        [Command("type")]
        [Summary("Look up type effectiveness information for one or two types.")]
        public async Task LookupType(string type1, string? type2 = null)
        {
            var typeEffectiveness = new Dictionary<(string, string), double>();

            var allTypeEffectiveness = await mongoService.TypeEffectiveness.Find(_ => true).ToListAsync();
            foreach (var te in allTypeEffectiveness)
                if (TypeIds.TryGetValue(te.DamageTypeId, out var damageType) &&
                    TypeIds.TryGetValue(te.TargetTypeId, out var targetType))
                    typeEffectiveness[(damageType, targetType)] = te.DamageFactor / 100.0;

            type1 = type1.ToLower().Capitalize()!;
            List<string> types = [type1];

            if (!string.IsNullOrEmpty(type2)) types.Add(type2.ToLower().Capitalize()!);

            foreach (var t in types)
                if (!TypeIds.Values.Contains(t))
                {
                    var guess = ExtrasService.FindClosest(t, TypeIds.Values);
                    await ErrorAsync($"{t} is not a valid type." + (guess is null ? "" : $"\nDid you mean **{guess}**?"));
                    return;
                }

            var attackEffectiveness = new Dictionary<double, List<string>>();
            var defenseEffectiveness = new Dictionary<double, List<string>>();

            if (type2 == null)
                foreach (var targetType in TypeIds.Values)
                {
                    var effectiveness = 1.0;
                    if (typeEffectiveness.TryGetValue((type1, targetType), out var factor)) effectiveness *= factor;

                    if (!attackEffectiveness.ContainsKey(effectiveness))
                        attackEffectiveness[effectiveness] = [];

                    attackEffectiveness[effectiveness].Add(targetType);
                }

            foreach (var damageType in TypeIds.Values)
            {
                var effectiveness = 1.0;
                foreach (var defenseType in types)
                    if (typeEffectiveness.TryGetValue((damageType, defenseType), out var factor))
                        effectiveness *= factor;

                if (!defenseEffectiveness.ContainsKey(effectiveness))
                    defenseEffectiveness[effectiveness] = [];

                defenseEffectiveness[effectiveness].Add(damageType);
            }

            StringBuilder desc = new();

            if (defenseEffectiveness.TryGetValue(4.0, out var quadDamage) && quadDamage.Any())
                desc.AppendLine($"**x4 damage from:** `{string.Join(", ", quadDamage)}`");

            if (defenseEffectiveness.TryGetValue(2.0, out var doubleDamage) && doubleDamage.Any())
                desc.AppendLine($"**x2 damage from:** `{string.Join(", ", doubleDamage)}`");

            if (defenseEffectiveness.TryGetValue(1.0, out var normalDamage) && normalDamage.Any())
                desc.AppendLine($"**x1 damage from:** `{string.Join(", ", normalDamage)}`");

            if (defenseEffectiveness.TryGetValue(0.5, out var halfDamage) && halfDamage.Any())
                desc.AppendLine($"**x1/2 damage from:** `{string.Join(", ", halfDamage)}`");

            if (defenseEffectiveness.TryGetValue(0.25, out var quarterDamage) && quarterDamage.Any())
                desc.AppendLine($"**x1/4 damage from:** `{string.Join(", ", quarterDamage)}`");

            if (defenseEffectiveness.TryGetValue(0.0, out var immuneDamage) && immuneDamage.Any())
                desc.AppendLine($"**Immune to damage from:** `{string.Join(", ", immuneDamage)}`");

            desc.AppendLine();

            if (type2 == null)
            {
                if (attackEffectiveness.TryGetValue(2.0, out var superEffective) && superEffective.Any())
                    desc.AppendLine($"**x2 damage to:** `{string.Join(", ", superEffective)}`");

                if (attackEffectiveness.TryGetValue(1.0, out var normalEffective) && normalEffective.Any())
                    desc.AppendLine($"**x1 damage to:** `{string.Join(", ", normalEffective)}`");

                if (attackEffectiveness.TryGetValue(0.5, out var notEffective) && notEffective.Any())
                    desc.AppendLine($"**x1/2 damage to:** `{string.Join(", ", notEffective)}`");

                if (attackEffectiveness.TryGetValue(0.0, out var noEffect) && noEffect.Any())
                    desc.AppendLine($"**Does nothing to:** `{string.Join(", ", noEffect)}`");
            }

            var embed = new EmbedBuilder()
                .WithTitle(string.Join(", ", types))
                .WithColor(0xF699CDu)
                .WithDescription(desc.ToString());

            await SendAsync(embed: embed);
        }
    }

    /// <summary>
    ///     Commands for interacting with your Pokémon.
    /// </summary>
    /// <param name="mongoService">The MongoDB service.</param>
    /// <param name="dbContextProvider">The database context provider.</param>
    /// <param name="pokemonService">The Pokémon service, for evolutions.</param>
    [Group("poke")]
    [Name("Poke")]
    public class PokemonCommands(
        IMongoService mongoService,
        LinqToDbConnectionProvider dbContextProvider,
        PokemonService pokemonService) : EeveeModule<ExtrasService>
    {
        private const int MovesetPageSize = 20;

        private static readonly string[] Stats =
            ["attack", "hp", "defense", "special attack", "special defense", "speed"];

        /// <summary>
        ///     Learns moves in all four slots for the selected Pokémon.
        /// </summary>
        /// <param name="first">The move for slot 1.</param>
        /// <param name="second">The move for slot 2.</param>
        /// <param name="third">The move for slot 3.</param>
        /// <param name="fourth">The move for slot 4.</param>
        [Command("learn_all")]
        [Alias("learnall")]
        [Summary("Learn all four moves for your selected Pokémon. Write moves with spaces using hyphens or quotes.")]
        public async Task LearnAll(string first, string second, string third, string fourth)
        {
            await using var db = await dbContextProvider.GetConnectionAsync();

            var selectedPokemon = await (
                from pokemon in db.UserPokemon
                join user in db.Users on pokemon.Id equals user.Selected
                where user.UserId == ctx.User.Id
                select pokemon
            ).FirstOrDefaultAsync();

            if (selectedPokemon == null)
            {
                await ErrorAsync("You must select a pokemon!");
                return;
            }

            var pokemonName = selectedPokemon.PokemonName;
            var moves = await Service.GetMoves(pokemonName.ToLower());

            if (moves == null)
            {
                await ErrorAsync(
                    $"That pokemon cannot learn any moves! You might need to `{await GetPrefixAsync()}deform` it first.");
                Log.Warning("Could not get moves for {Pokemon}", pokemonName);
                return;
            }

            var newMoves = new string[4];
            string[] inputs = [first, second, third, fourth];
            for (var i = 0; i < 4; i++)
            {
                var resolved = await ResolveMoveAsync(inputs[i], moves, pokemonName,
                    $" ({i + 1})\n`Retry this command again`");
                if (resolved is null)
                    return;
                newMoves[i] = resolved;
            }

            await db.UserPokemon.Where(p => p.Id == selectedPokemon.Id)
                .Set(p => p.Moves, newMoves)
                .UpdateAsync();

            var displayName = selectedPokemon.Nickname == "None" ? "" : selectedPokemon.Nickname;
            var emote = "";
            if (selectedPokemon.Shiny == true) emote = Emojis.ShinyAlt;
            if (selectedPokemon.Radiant == true) emote = Emojis.Radiant;
            if (selectedPokemon.Skin == "shadow") emote = Emojis.Shadow;

            await ConfirmAsync(
                $"Your __**{displayName}({pokemonName.Capitalize()}){emote}**__ successfully learned the following moves:\n" +
                $"> __**{newMoves[0].Capitalize()}**__, __**{newMoves[1].Capitalize()}**__, " +
                $"__**{newMoves[2].Capitalize()}**__, and __**{newMoves[3].Capitalize()}**__");

            await pokemonService.TryEvolve(selectedPokemon.Id);
        }

        /// <summary>
        ///     Learns a move in a specific slot for the selected Pokémon.
        /// </summary>
        /// <param name="slot">The slot, from 1 to 4.</param>
        /// <param name="move">The move to learn.</param>
        [Command("learn")]
        [Summary("Learn a move in a slot (1 to 4) for your selected Pokémon.")]
        public async Task Learn(int slot, [Remainder] string move)
        {
            if (slot is < 1 or > 4)
            {
                await ErrorAsync("The slot must be 1, 2, 3 or 4.");
                return;
            }

            await using var db = await dbContextProvider.GetConnectionAsync();

            var selectedId = await db.Users
                .Where(u => u.UserId == ctx.User.Id)
                .Select(u => u.Selected)
                .FirstOrDefaultAsync();

            if (selectedId is null or 0)
            {
                await ErrorAsync("You do not have that Pokemon!");
                return;
            }

            var (success, message) = await Service.LearnMoveAsync(ctx.User.Id, selectedId.Value, slot, move);
            if (success)
                await ConfirmAsync(message);
            else
                await ErrorAsync(message.Replace("`/", $"`{await GetPrefixAsync()}"));
        }

        /// <summary>
        ///     Shows the moves the selected Pokémon has learned.
        /// </summary>
        [Command("moves")]
        [Summary("Show moves your pokemon has learned.")]
        public async Task Moves()
        {
            await using var db = await dbContextProvider.GetConnectionAsync();
            var prefix = await GetPrefixAsync();

            var selectedPokemon = await (
                from pokemon in db.UserPokemon
                join user in db.Users on pokemon.Id equals user.Selected
                where user.UserId == ctx.User.Id
                select pokemon
            ).FirstOrDefaultAsync();

            if (selectedPokemon == null)
            {
                await ErrorAsync($"You do not have a selected pokemon. Select one with `{prefix}select` first.");
                return;
            }

            var moveSet = selectedPokemon.Moves;
            var embed = new EmbedBuilder()
                .WithTitle("Moves")
                .WithColor(0xFFB6C1u)
                .WithFooter($"See available moves with {prefix}poke moveset");

            for (var i = 0; i < 4; i++)
                embed.AddField($"**Move {i + 1}**:", moveSet.ElementAtOrDefault(i) ?? "None");

            await SendAsync(embed: embed);
        }

        /// <summary>
        ///     Shows the moves that the selected Pokémon can learn.
        /// </summary>
        /// <param name="page">The page to start on.</param>
        [Command("moveset")]
        [Summary("Show moves your selected Pokemon can learn.")]
        public async Task Moveset(int page = 1)
        {
            await using var db = await dbContextProvider.GetConnectionAsync();

            var pokemonName = await db.UserPokemon
                .Where(p => p.Id == db.Users
                    .Where(u => u.UserId == ctx.User.Id)
                    .Select(u => u.Selected)
                    .FirstOrDefault())
                .Select(p => p.PokemonName)
                .FirstOrDefaultAsync();

            if (pokemonName == null)
            {
                await ErrorAsync("You have not selected a Pokemon");
                return;
            }

            pokemonName = pokemonName.ToLower();

            if (pokemonName == "egg")
            {
                await ErrorAsync("There are no available moves for an Egg");
                return;
            }

            var moves = await Service.GetMoves(pokemonName);

            if (moves == null)
            {
                await ErrorAsync(
                    $"That pokemon cannot learn any moves! You might need to `{await GetPrefixAsync()}deform` it first.");
                Log.Warning("Could not get moves for {Pokemon}", pokemonName);
                return;
            }

            var details = await Service.GetMoveDetailsAsync(moves);
            if (details.Count != moves.Count)
            {
                await ErrorAsync("An error occurred finding moves for that pokemon.");
                Log.Warning("A move of {Pokemon} is not in mongo moves", pokemonName);
                return;
            }

            var lines = details.Select(move =>
            {
                var damageClass = move.Category switch
                {
                    "Status" => Emojis.Status,
                    "Physical" => Emojis.Physical,
                    _ => Emojis.Special
                };
                var formattedMove = move.Name.Capitalize()!.Replace("'", "").Replace("-", " ");
                return $"**{damageClass} {formattedMove}** - Power:`{move.Power}` Acc:`{move.Accuracy}` Type:`{move.Type}`";
            }).ToList();

            var pageCount = Math.Max(1, (lines.Count + MovesetPageSize - 1) / MovesetPageSize);
            await Reactions.PaginateAsync(ctx.Channel, ctx.User.Id, pageCount, index =>
            {
                var embed = new EmbedBuilder()
                    .WithTitle("Learnable Move List")
                    .WithColor(0xFFB6C1u)
                    .WithDescription(string.Join("\n", lines.Skip(index * MovesetPageSize).Take(MovesetPageSize)))
                    .WithFooter($"Page {index + 1}/{pageCount}");
                return Task.FromResult(new Page(null, [embed.Build()]));
            }, startPage: page - 1);
        }

        /// <summary>
        ///     Adds EVs (Effort Values) to a stat for the selected Pokémon.
        /// </summary>
        /// <param name="amount">The amount of EVs to add.</param>
        /// <param name="stat">The stat to add EVs to.</param>
        [Command("addevs")]
        [Summary("Add ev's to one of your pokemon if you have ev points to spend.")]
        public async Task AddEvs(int amount, [Remainder] string stat)
        {
            stat = stat.Trim().ToLower().Replace("-", " ");
            var prefix = await GetPrefixAsync();

            if (!Stats.Contains(stat))
            {
                await ErrorAsync(
                    $"Correct usage of this command is: `{prefix}poke addevs <amount> <stat_name>`\n" +
                    $"Example: `{prefix}poke addevs 252 speed` To add to your __Selected__ Pokemon\n" +
                    $"Stats: {string.Join(", ", Stats)}");
                return;
            }

            if (amount > 252)
            {
                await ErrorAsync("You can not add more than 252 EVs to a stat");
                return;
            }

            if (amount < 1)
            {
                await ErrorAsync("You must add at least 1 EV to a stat");
                return;
            }

            await using var db = await dbContextProvider.GetConnectionAsync();

            var selectedId = await db.Users
                .Where(u => u.UserId == ctx.User.Id)
                .Select(u => u.Selected)
                .FirstOrDefaultAsync();

            if (selectedId == null)
            {
                await ErrorAsync($"You do not have a selected pokemon!\nUse `{prefix}select` to select a pokemon.");
                return;
            }

            var evPoints = await db.Users
                .Where(u => u.UserId == ctx.User.Id)
                .Select(u => u.EvPoints)
                .FirstOrDefaultAsync();

            if (evPoints == null)
            {
                await ErrorAsync($"You have not Started!\nStart with `{prefix}start` first!");
                return;
            }

            if (evPoints < amount)
            {
                await ErrorAsync($"You do not have {amount} EV Points to add!");
                return;
            }

            try
            {
                var pokemon = db.UserPokemon.Where(p => p.Id == selectedId);
                switch (stat)
                {
                    case "attack":
                        await pokemon.Set(p => p.AttackEv, p => p.AttackEv + amount).UpdateAsync();
                        break;
                    case "defense":
                        await pokemon.Set(p => p.DefenseEv, p => p.DefenseEv + amount).UpdateAsync();
                        break;
                    case "hp":
                        await pokemon.Set(p => p.HpEv, p => p.HpEv + amount).UpdateAsync();
                        break;
                    case "special attack":
                        await pokemon.Set(p => p.SpecialAttackEv, p => p.SpecialAttackEv + amount).UpdateAsync();
                        break;
                    case "special defense":
                        await pokemon.Set(p => p.SpecialDefenseEv, p => p.SpecialDefenseEv + amount).UpdateAsync();
                        break;
                    case "speed":
                        await pokemon.Set(p => p.SpeedEv, p => p.SpeedEv + amount).UpdateAsync();
                        break;
                }

                await db.Users.Where(u => u.UserId == ctx.User.Id)
                    .Set(u => u.EvPoints, u => u.EvPoints - amount)
                    .UpdateAsync();

                await ConfirmAsync(
                    $"You have successfully added {amount} EVs to the {stat} Stat of your Selected Pokemon!");
            }
            catch (Exception)
            {
                await ErrorAsync("Your Pokemon has maxed all 510 EVs");
            }
        }

        private async Task<string?> ResolveMoveAsync(string input, List<string> moves, string pokemonName,
            string label)
        {
            var move = input.Trim().Replace(" ", "-").ToLower();
            if (moves.Contains(move))
                return move;

            var guess = ExtrasService.FindClosest(move, moves);
            await ErrorAsync($"Your {pokemonName} can not learn that Move{label}" +
                             (guess is null ? "" : $"\nDid you mean `{guess}`?"));
            return null;
        }
    }
}
