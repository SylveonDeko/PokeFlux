using System.Net.Http;
using System.Text;
using EeveeCore.Common.ModuleBases;
using EeveeCore.Database.Models.Mongo.Game;
using EeveeCore.Modules.Missions.Common;
using EeveeCore.Modules.Missions.Services;
using Serilog;

namespace EeveeCore.Modules.Missions;

/// <summary>
///     Daily missions, mission XP and the crystal slime exchange.
/// </summary>
[Group("missions")]
[Alias("mission")]
[Name("Missions")]
public class MissionsModule : EeveeModule<MissionService>
{
    private const string Keep = "✅";
    private const string Risk = "🎲";

    private static readonly string[] Numbers = ["1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣", "9️⃣"];

    /// <summary>
    ///     Shows the crystal slime exchange.
    /// </summary>
    [Command("store")]
    [Alias("shop")]
    [Summary("See what you can buy with Crystallized Slime.")]
    public async Task Store()
    {
        var prefix = await GetPrefixAsync();
        var slime = MissionConstants.CrystalSlimeEmojiId;
        var items = string.Join("\n", MissionConstants.StoreItems.Values
            .Select(i => $"- `{prefix}missions buy {i.Name}`: {i.Cost} {slime}"));

        var embed = new EmbedBuilder()
            .WithTitle("Crystal Slime Exchange")
            .WithDescription(
                "### Exchange your Crystallized Slime for Credits!\n" +
                $"- 150 {slime} for 100,000 credits.\n" +
                "### Friendship Stone: Increases friendship/happiness instantly!\n" +
                $"- 10 {slime}\n" +
                "### Shadow Essence: Increase your shadow-chain instantly!\n" +
                $"- 100 {slime} for random chance: +15-75 Shadow Chain\n" +
                "### Meowth Tickets: Try Your Luck!\n" +
                $"- 75 {slime} for 1 Ticket\n" +
                "### VIP Tokens: Get exclusive benefits!\n" +
                $"- 1000 {slime} for 1 VIP Token\n" +
                $"- 2500 {slime} for 3 VIP Tokens (x3)\n" +
                "\n**How Meowth Tickets Work:**\n" +
                "Each Meowth Ticket hides a secret amount of credits ranging from a tiny sum to a whopping 150,000.\n" +
                "- Upon buying a Meowth Ticket, you'll be presented with 9 choices, each concealing different amounts of credits.\n" +
                "- Make your first choice to uncover what's hidden beneath. But that's not all! You'll then face a tantalizing decision: keep your initial winnings or risk your first pick for potentially more!\n" +
                "**You decide!**\n" +
                "### Buy\n" + items)
            .WithColor(MissionConstants.StoreColor)
            .WithFooter($"When you buy a Meowth Ticket, you have {MissionConstants.LotteryTimeoutSeconds} seconds before the game ends, no refunds!");

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Buys an item from the crystal slime exchange.
    /// </summary>
    /// <param name="item">The item's name.</param>
    [Command("buy")]
    [Summary("Buy an item from the Crystal Slime Exchange.")]
    public async Task Buy([Remainder] string item)
    {
        if (await FindStoreItemAsync(item) is not { } itemKey)
            return;

        var storeItem = MissionConstants.StoreItems[itemKey];
        var userId = ctx.User.Id;

        try
        {
            if (await Service.GetUserCrystalSlimeAsync(userId) < storeItem.Cost)
            {
                await ErrorAsync("Not enough crystallized slime.");
                return;
            }

            switch (itemKey)
            {
                case "friendship-stone":
                    await Service.AddItemToInventoryAsync(userId, "friendship-stone");
                    break;

                case "credits_small":
                    await Service.AddMewCoinsAsync(userId, storeItem.Reward);
                    break;

                case "shadow-essence":
                    var (_, hunt) = await Service.GetUserChainAndHuntAsync(userId);
                    if (string.IsNullOrEmpty(hunt))
                    {
                        await ErrorAsync("You have not set a shadow hunt yet.");
                        return;
                    }

                    await Service.IncreaseChainAsync(userId,
                        Random.Shared.Next(storeItem.MinChainIncrease, storeItem.MaxChainIncrease + 1));
                    break;

                case "small_chance_ticket":
                    if (!await Service.DeductCrystalSlimeAsync(userId, storeItem.Cost))
                    {
                        await ErrorAsync("Not enough crystallized slime.");
                        return;
                    }

                    await ConfirmAsync("Good luck with your Meowth Ticket!");
                    await PlayLotteryAsync();
                    return;

                case "vip_token_single":
                case "vip_token_pack":
                    await Service.AddVipTokensAsync(userId, storeItem.VipTokensAmount);
                    break;

                default:
                    await ErrorAsync("Purchase failed. Please try again later.");
                    return;
            }

            await Service.DeductCrystalSlimeAsync(userId, storeItem.Cost);

            var prefix = await GetPrefixAsync();
            await ConfirmAsync(itemKey switch
            {
                "friendship-stone" => $"You bought a {storeItem.Name}! Use it with `{prefix}apply friendship-stone`.",
                "credits_small" => $"Successfully purchased {storeItem.Name} for {storeItem.Cost} crystallized slime!",
                "shadow-essence" => "You bought some shadow essence! Your chain has been increased!",
                "vip_token_single" => $"Successfully purchased {storeItem.VipTokensAmount} VIP token for {storeItem.Cost} crystallized slime!",
                "vip_token_pack" => $"Successfully purchased {storeItem.VipTokensAmount} VIP tokens for {storeItem.Cost} crystallized slime!",
                _ => $"Successfully purchased {storeItem.Name}!"
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing store purchase for user {UserId}", userId);
            await ErrorAsync("An error occurred while processing your purchase. Please try again later.");
        }
    }

    /// <summary>
    ///     Shows your mission XP, level and crystal slime as an image.
    /// </summary>
    [Command("xp")]
    [Summary("Displays your current XP progress towards the next level.")]
    public async Task Xp()
    {
        try
        {
            var (currentXp, level, crystalSlime) = await Service.GetUserXpInfoAsync(ctx.User.Id);
            var title = await Service.GetUserTitleAsync(ctx.User.Id);

            byte[]? avatarBytes = null;
            try
            {
                using var httpClient = new HttpClient();
                avatarBytes = await httpClient.GetByteArrayAsync(ctx.User.GetAvatarOrDefaultUrl(256));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to download avatar for user {UserId}", ctx.User.Id);
            }

            var imageBytes = await new XpImageGenerationService().GenerateXpImageAsync(
                ctx.User.Username, currentXp, level, crystalSlime, title, avatarBytes);

            await using var stream = new MemoryStream(imageBytes);
            await SendAsync(attachments: [new AttachmentRequest(stream, "xp_progress.png")]);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in XP command for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while generating your XP image.");
        }
    }

    /// <summary>
    ///     Shows your progress on today's missions with progress bars.
    /// </summary>
    [Command("progress")]
    [Summary("Shows your progress on current missions.")]
    public async Task Progress()
    {
        try
        {
            var userProgress = await Service.GetUserProgressAsync(ctx.User.Id);
            if (userProgress == null)
            {
                await ErrorAsync("You do not have any progress recorded for current missions.");
                return;
            }

            var activeMissions = await Service.GetActiveMissionsAsync();
            if (activeMissions.Count == 0)
            {
                await ErrorAsync("No active missions are currently available.");
                return;
            }

            var progressMessage = new StringBuilder($"**{ctx.User.Username}'s Mission Progress:**\n");
            foreach (var mission in activeMissions)
            {
                var progress = GetProgressForMission(userProgress, mission.Key);
                var filled = Math.Min((int)(progress / (double)mission.Target * 10), 10);
                progressMessage.AppendLine(
                    $"- **{mission.Name}**: {BuildBar(filled, 10)} {progress}/{mission.Target}");
            }

            await ReplyAsync(progressMessage.ToString());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in Progress command for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while retrieving your progress.");
        }
    }

    /// <summary>
    ///     Lists today's missions.
    /// </summary>
    [Command("list")]
    [Summary("Lists all currently active missions.")]
    public async Task List()
    {
        try
        {
            var missions = await Service.GetActiveMissionsAsync();
            if (missions.Count == 0)
            {
                await ErrorAsync("Missions are currently down, please wait a few minutes and try again!");
                return;
            }

            var userProgress = await Service.GetUserProgressAsync(ctx.User.Id) ?? new UserProgress
            {
                UserId = ctx.User.Id
            };

            var embed = new EmbedBuilder()
                .WithTitle("Today's missions")
                .WithColor(MissionConstants.MissionCompleteColor)
                .WithFooter("Missions reset daily");

            foreach (var mission in missions)
                embed.AddField($"{mission.Name} ({mission.Target})",
                    $"{GetProgressForMission(userProgress, mission.Key)}/{mission.Target} {MissionConstants.MissionEmojiId}");

            await SendAsync(embed: embed);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in List command for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while retrieving missions.");
        }
    }

    private async Task PlayLotteryAsync()
    {
        var userId = ctx.User.Id;
        var amounts = GenerateMeowthTicketAmounts(MissionConstants.LotteryChoiceCount);
        var timeout = TimeSpan.FromSeconds(MissionConstants.LotteryTimeoutSeconds);

        var message = await SendAsync(embed: new EmbedBuilder()
            .WithTitle("🎰 Meowth Ticket Lottery!")
            .WithDescription("React with a number to reveal the prize behind it:\n" +
                             "*You'll then decide whether to keep it or risk it for potentially more!*")
            .WithColor(MissionConstants.StoreColor));

        if (await Reactions.ChooseAsync(message, userId, Numbers, timeout) is not { } first)
        {
            await message.ModifyAsync(embeds: [LotteryEndedEmbed()]);
            return;
        }

        var revealed = amounts[first];
        await message.ModifyAsync(embeds:
        [
            new EmbedBuilder()
                .WithTitle($"🎰 You revealed: {revealed:N0} MewCoins!")
                .WithDescription("**Decision time!**\n" +
                                 $"{Keep} **Keep**: Take your {revealed:N0} MewCoins and end the game\n" +
                                 $"{Risk} **Risk It**: Forfeit your current amount and pick another number for a chance at more!\n\n" +
                                 $"⏰ *You have {MissionConstants.LotteryTimeoutSeconds} seconds to decide...*")
                .WithColor(MissionConstants.StoreColor)
                .Build()
        ]);

        var decision = await Reactions.ChooseAsync(message, userId, [Keep, Risk], timeout);
        if (decision is null)
        {
            await message.ModifyAsync(embeds: [LotteryEndedEmbed()]);
            return;
        }

        if (decision == 0)
        {
            await Service.AddMewCoinsAsync(userId, (ulong)revealed);
            await message.ModifyAsync(embeds:
            [
                new EmbedBuilder()
                    .WithTitle("🎰 Lottery Complete!")
                    .WithDescription($"**Congratulations!**\nYou kept your winnings and received **{revealed:N0} MewCoins**!")
                    .WithColor(MissionConstants.LotteryMediumColor)
                    .WithFooter("Thanks for playing the Meowth Ticket Lottery!")
                    .Build()
            ]);
            return;
        }

        var remaining = Numbers.Where((_, i) => i != first).ToList();
        await message.ModifyAsync(embeds:
        [
            new EmbedBuilder()
                .WithTitle("🎲 You chose to risk it!")
                .WithDescription($"Your previous choice ({Numbers[first]}) is now locked out. React with your final number:\n" +
                                 "⚠️ *This is your final choice - no more chances!*")
                .WithColor(MissionConstants.StoreColor)
                .Build()
        ]);

        if (await Reactions.ChooseAsync(message, userId, remaining, timeout) is not { } final)
        {
            await message.ModifyAsync(embeds: [LotteryEndedEmbed()]);
            return;
        }

        var finalAmount = amounts[Array.IndexOf(Numbers, remaining[final])];
        await Service.AddMewCoinsAsync(userId, (ulong)finalAmount);
        await message.ModifyAsync(embeds:
        [
            new EmbedBuilder()
                .WithTitle("🎰 Final Lottery Result!")
                .WithDescription($"**Your final choice revealed: {finalAmount:N0} Coins!**\n\n" +
                                 $"You have been awarded **{finalAmount:N0} MewCoins**!")
                .WithColor(finalAmount >= 50000 ? MissionConstants.LotteryHighColor
                    : finalAmount >= 10000 ? MissionConstants.LotteryMediumColor
                    : MissionConstants.LotteryLowColor)
                .WithFooter("Thanks for playing the Meowth Ticket Lottery!")
                .Build()
        ]);
    }

    private static EmbedRequest LotteryEndedEmbed() => new EmbedBuilder()
        .WithTitle("🎰 Lottery Over")
        .WithDescription("You ran out of time, so the Meowth Ticket game has ended.")
        .WithErrorColor()
        .Build();

    private async Task<string?> FindStoreItemAsync(string input)
    {
        var query = input.Trim().ToLower();
        foreach (var (key, item) in MissionConstants.StoreItems)
        {
            if (key.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        var partial = MissionConstants.StoreItems
            .Where(i => i.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (partial.Count == 1)
            return partial[0].Key;

        var suggestions = (partial.Count > 0 ? partial : MissionConstants.StoreItems.ToList())
            .OrderBy(i => i.Value.Name.ToLower().LevenshteinDistance(query))
            .Take(3)
            .Select(i => $"`{i.Value.Name}`");

        await ErrorAsync($"There is no store item called `{input}`. Did you mean {string.Join(", ", suggestions)}?");
        return null;
    }

    private static string BuildBar(int filled, int length)
    {
        var bar = new StringBuilder();
        for (var i = 0; i < length; i++)
        {
            var isFilled = i < filled;
            bar.Append(i switch
            {
                0 => isFilled ? Emojis.Bar.MissionStart : Emojis.Bar.MissionEmptyStart,
                _ when i == length - 1 => isFilled ? Emojis.Bar.MissionEnd : Emojis.Bar.MissionEmptyEnd,
                _ => isFilled ? Emojis.Bar.MissionMiddle : Emojis.Bar.MissionEmptyMiddle
            });
        }

        return bar.ToString();
    }

    private static int[] GenerateMeowthTicketAmounts(int numChoices)
    {
        var amounts = MissionConstants.LotteryAmounts;
        var weights = MissionConstants.LotteryWeights;
        var totalWeight = weights.Sum();
        var result = new int[numChoices];

        for (var i = 0; i < numChoices; i++)
        {
            var randomWeight = Random.Shared.Next(totalWeight);
            var currentWeight = 0;
            for (var j = 0; j < amounts.Length; j++)
            {
                currentWeight += weights[j];
                if (randomWeight >= currentWeight)
                    continue;

                result[i] = amounts[j];
                break;
            }
        }

        return result;
    }

    private static int GetProgressForMission(UserProgress userProgress, string missionKey) => missionKey switch
    {
        "breed" => userProgress.Breed,
        "catch" => userProgress.Catch,
        "duel_win" => userProgress.DuelWin,
        "duel_lose" => userProgress.DuelLose,
        "ev" => userProgress.Ev,
        "fish" => userProgress.Fish,
        "npc" => userProgress.Npc,
        "party" => userProgress.Party,
        "pokemon_setup" => userProgress.PokemonSetup,
        "vote" => userProgress.Vote,
        "game_wordsearch" => userProgress.GameWordSearch,
        "game_slots" => userProgress.GameSlots,
        "game_slots_win" => userProgress.GameSlotsWin,
        _ => 0
    };
}
