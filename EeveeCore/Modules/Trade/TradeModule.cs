using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Trade.Models;
using EeveeCore.Modules.Trade.Services;
using LinqToDB.Async;
using Serilog;
using TokenType = EeveeCore.Modules.Trade.Models.TokenType;

namespace EeveeCore.Modules.Trade;

/// <summary>
///     Shared trade logic: starting sessions and finding the session a player is in.
/// </summary>
public abstract class TradeModuleBase : EeveeModule<TradeService>
{
    /// <summary>
    ///     Gets or sets the trade lock service.
    /// </summary>
    public ITradeLockService TradeLocks { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the database connection provider.
    /// </summary>
    public LinqToDbConnectionProvider DbProvider { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the fraud detection service.
    /// </summary>
    public FraudDetectionService FraudDetection { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the trade evolution service.
    /// </summary>
    public TradeEvolutionService TradeEvolution { get; set; } = null!;

    /// <summary>
    ///     Replaces slash command mentions in a service message with the server prefix.
    /// </summary>
    /// <param name="message">The message.</param>
    protected async Task<string> WithPrefixAsync(string? message) =>
        TradeInput.WithPrefix(message, await GetPrefixAsync());

    /// <summary>
    ///     Gets the session a user is in, cancelling it when it has expired.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    protected async Task<TradeSession?> GetUserSessionAsync(ulong userId)
    {
        var session = Service.GetActiveSessionForUser(userId);
        if (session is not { Status: not TradeStatus.Processing } || !session.IsExpired())
            return session;

        await Service.CancelTradeSessionAsync(session.SessionId, userId);
        return null;
    }

    /// <summary>
    ///     Asks a user to trade and starts the session when they accept.
    /// </summary>
    /// <param name="user">The user to trade with.</param>
    /// <param name="quickPosition">A Pokémon position to add right away, for quick trades.</param>
    protected async Task RequestTradeAsync(IUser user, int? quickPosition)
    {
        if (ctx.User.Id == user.Id)
        {
            await ErrorAsync("You cannot trade with yourself!");
            return;
        }

        if (user.IsBot)
        {
            await ErrorAsync("You cannot trade with bots!");
            return;
        }

        var prefix = await GetPrefixAsync();

        await using (var db = await DbProvider.GetConnectionAsync())
        {
            var currentUser = await db.Users
                .Where(u => u.UserId == ctx.User.Id)
                .Select(u => new { u.TradeBanned, u.TradeBanReason })
                .FirstOrDefaultAsync();

            if (currentUser?.TradeBanned == true)
            {
                await ErrorAsync($"You are banned from trading. Reason: {currentUser.TradeBanReason ?? "Fraud detection"}");
                return;
            }

            var targetUser = await db.Users
                .Where(u => u.UserId == user.Id)
                .Select(u => new { u.TradeBanned })
                .FirstOrDefaultAsync();

            if (targetUser?.TradeBanned == true)
            {
                await ErrorAsync($"{user.Username} is banned from trading.");
                return;
            }
        }

        await GetUserSessionAsync(ctx.User.Id);
        await GetUserSessionAsync(user.Id);

        if (await TradeLocks.IsUserTradeLockedAsync(ctx.User.Id))
        {
            await ErrorAsync($"{ctx.User.Username} is currently in a trade! Use `{prefix}canceltrades` if you think this is an error.");
            return;
        }

        if (await TradeLocks.IsUserTradeLockedAsync(user.Id))
        {
            await ErrorAsync($"{user.Username} is currently in a trade! They can use `{prefix}canceltrades` if they think this is an error.");
            return;
        }

        var kind = quickPosition is null ? "Trade" : "Quick Trade";
        var request = new EmbedBuilder()
            .WithTitle($"🔄 {kind} Request")
            .WithDescription(quickPosition is null
                ? $"{ctx.User.Mention} has requested a trade with {user.Mention}!"
                : $"{ctx.User.Mention} wants to trade their Pokemon at position **{quickPosition}** with {user.Mention}!")
            .WithColor(Color.Blue)
            .WithFooter($"Waiting for {user.Username} to accept... React with ✅ to accept or ❌ to decline.")
            .WithTimestamp(DateTimeOffset.UtcNow);

        var message = await ReplyAsync(user.Mention, [request.Build()]);
        var answer = await Reactions.ConfirmAsync(message, user.Id, TimeSpan.FromMinutes(2));

        if (answer is null)
        {
            await message.ModifyAsync(embeds:
            [
                new EmbedBuilder()
                    .WithTitle($"⏰ {kind} Request Expired")
                    .WithDescription($"{user.Mention} took too long to respond to the {kind.ToLowerInvariant()} request.")
                    .WithColor(Color.Orange)
                    .Build()
            ]);
            return;
        }

        if (answer == false)
        {
            await message.ModifyAsync(embeds:
            [
                new EmbedBuilder()
                    .WithTitle($"❌ {kind} Declined")
                    .WithDescription($"{user.Mention} declined the {kind.ToLowerInvariant()} request.")
                    .WithColor(Color.Red)
                    .Build()
            ]);
            return;
        }

        await StartSessionAsync(user, message, quickPosition, kind);
    }

    private async Task StartSessionAsync(IUser user, Message message, int? quickPosition, string kind)
    {
        var sessionResult = await Service.CreateTradeSessionAsync(ctx.User.Id, user.Id, ctx.Channel.Id, ctx.Guild!.Id);
        if (!sessionResult.Success)
        {
            await ShowFailureAsync(message, $"❌ {kind} Failed", await WithPrefixAsync(sessionResult.Message));
            return;
        }

        var session = (TradeSession)sessionResult.Data!;
        await TradeLocks.AddTradeLockAsync(ctx.User.Id);
        await TradeLocks.AddTradeLockAsync(user.Id);

        if (quickPosition is { } position)
        {
            var addResult = await Service.AddPokemonToTradeAsync(session.SessionId, ctx.User.Id, position);
            if (!addResult.Success)
            {
                await Service.CancelTradeSessionAsync(session.SessionId, ctx.User.Id);
                await ShowFailureAsync(message, $"❌ {kind} Failed",
                    $"Could not add Pokemon to trade: {await WithPrefixAsync(addResult.Message)}");
                return;
            }
        }

        session.TradeMessage = message;
        await Service.UpdateTradeInterfaceAsync(session);
    }

    private static Task ShowFailureAsync(Message message, string title, string description) =>
        message.ModifyAsync(embeds:
        [
            new EmbedBuilder()
                .WithTitle(title)
                .WithDescription(description)
                .WithColor(Color.Red)
                .Build()
        ]);
}

/// <summary>
///     Trading Pokémon, credits and tokens with other players.
/// </summary>
[Name("Trade")]
public class TradeModule : TradeModuleBase
{
    /// <summary>
    ///     Starts a trade with a Pokémon already offered.
    /// </summary>
    /// <param name="user">The user to trade with.</param>
    /// <param name="pokemon">The position of the Pokémon to offer.</param>
    [Command("quicktrade")]
    [Summary("Quickly start a trade with a specific Pokemon!")]
    [RequireContext(ContextType.Guild)]
    public Task QuickTrade(IUser user, int pokemon) => RequestTradeAsync(user, pokemon);

    /// <summary>
    ///     Shows whether the user is in a trade.
    /// </summary>
    [Command("mytrades")]
    [Summary("View your current trade sessions.")]
    public async Task MyTrades()
    {
        if (await TradeLocks.IsUserTradeLockedAsync(ctx.User.Id))
            await ConfirmAsync("You are currently in an active trade session.");
        else
            await ConfirmAsync("You are not currently in any trade sessions.");
    }

    /// <summary>
    ///     Cancels the user's trades and clears their trade locks.
    /// </summary>
    [Command("canceltrades")]
    [Summary("Cancel all your trade sessions.")]
    public async Task CancelTrades()
    {
        await using (var db = await DbProvider.GetConnectionAsync())
        {
            var user = await db.Users
                .Where(u => u.UserId == ctx.User.Id)
                .Select(u => new { u.TradeBanned, u.TradeBanReason })
                .FirstOrDefaultAsync();

            if (user?.TradeBanned == true)
            {
                await ErrorAsync($"You are banned from trading. Reason: {user.TradeBanReason ?? "Fraud detection"}");
                return;
            }
        }

        if (Service.GetActiveSessionForUser(ctx.User.Id) is { Status: not TradeStatus.Processing } session)
            await Service.CancelTradeSessionAsync(session.SessionId, ctx.User.Id);

        await TradeLocks.ClearAllTradeLocksAsync(ctx.User.Id);
        await Service.ClearOrphanedTradeLocksAsync(ctx.User.Id);

        await ConfirmAsync("All your trade sessions have been cancelled and trade locks cleared.");
    }

    /// <summary>
    ///     Explains how trading works.
    /// </summary>
    [Command("tradehelp")]
    [Summary("Get help with trading.")]
    public async Task TradeHelp()
    {
        var prefix = await GetPrefixAsync();
        var embed = new EmbedBuilder()
            .WithTitle("🔄 Trading Help")
            .WithDescription("Learn how to trade with other users!")
            .WithColor(Color.Blue)
            .AddField("**Starting a Trade**",
                $"Use `{prefix}trade @user` to request a trade with another user. They must react with ✅ before the trade begins.\n" +
                $"Use `{prefix}quicktrade @user <position>` to start a trade with a Pokemon already offered.")
            .AddField("**Adding Items**",
                $"• **Pokemon**: `{prefix}trade add <positions>` (e.g. `{prefix}trade add 5` or `{prefix}trade add 2 5 10`)\n" +
                $"• **Credits**: `{prefix}trade credits <amount>` (supports `k` and `m` for thousands/millions)\n" +
                $"• **Tokens**: `{prefix}trade tokens <type> <amount>`")
            .AddField("**Removing Items**",
                $"• **Pokemon**: `{prefix}trade remove <positions>`\n" +
                $"• **Tokens**: `{prefix}trade untokens <type> <amount>`")
            .AddField("**Completing the Trade**",
                $"Once both users have added items, use `{prefix}trade confirm`. Both users must confirm before the trade executes. " +
                "Changing the trade resets both confirmations.\n" +
                $"Use `{prefix}trade cancel` to cancel.")
            .AddField("**Trade Evolution**",
                "Some Pokemon evolve when traded! The bot will automatically check and evolve eligible Pokemon.")
            .AddField("**Important Notes**",
                "• You cannot trade your #1 Pokemon\n" +
                "• You cannot trade eggs or favorited Pokemon\n" +
                "• Pokemon on the market cannot be traded\n" +
                "• Trade sessions expire after 6 minutes of inactivity")
            .WithFooter($"Use {prefix}gift commands to give items without expecting anything in return!");

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Starting a trade and changing the trade you are in.
    /// </summary>
    [Group("trade")]
    public class TradeCommands : TradeModuleBase
    {
        /// <summary>
        ///     Asks a user to trade.
        /// </summary>
        /// <param name="user">The user to trade with.</param>
        [Command]
        [Summary("Begin a trade with another user!")]
        [RequireContext(ContextType.Guild)]
        [Priority(-1)]
        public Task Start(IUser user) => RequestTradeAsync(user, null);

        /// <summary>
        ///     Adds Pokémon to the trade by position.
        /// </summary>
        /// <param name="positions">Positions separated by spaces or commas.</param>
        [Command("add")]
        [Summary("Add Pokemon to your trade by position.")]
        public async Task Add([Remainder] string positions)
        {
            var session = await GetCommandSessionAsync();
            if (session is null)
                return;

            var parsed = TradeInput.ParsePositions(positions);
            if (parsed.Count == 0)
            {
                await ErrorAsync("Please enter valid Pokemon position numbers.");
                return;
            }

            var reset = Reopen(session);
            var results = new List<string>();
            var errors = new List<string>();

            foreach (var position in parsed)
            {
                if (position == 1)
                {
                    errors.Add("You cannot give away your Number 1 Pokemon");
                    continue;
                }

                if (position > int.MaxValue)
                {
                    errors.Add($"Position {position}: You don't have that Pokemon or that Pokemon is currently in the market!");
                    continue;
                }

                var result = await Service.AddPokemonToTradeAsync(session.SessionId, ctx.User.Id, (int)position);
                if (result.Success)
                    results.Add($"Added Pokemon at position {position}");
                else
                    errors.Add($"Position {position}: {await WithPrefixAsync(result.Message)}");
            }

            await ReportAsync(session, results, errors, "No Pokemon were added to the trade.", reset);
        }

        /// <summary>
        ///     Removes Pokémon from the trade by position.
        /// </summary>
        /// <param name="positions">Positions separated by spaces or commas.</param>
        [Command("remove")]
        [Summary("Remove Pokemon from your trade by position.")]
        public async Task Remove([Remainder] string positions)
        {
            var session = await GetCommandSessionAsync();
            if (session is null)
                return;

            var parsed = TradeInput.ParsePositions(positions);
            if (parsed.Count == 0)
            {
                await ErrorAsync("Please enter valid Pokemon position numbers.");
                return;
            }

            var reset = Reopen(session);
            var results = new List<string>();
            var errors = new List<string>();

            foreach (var position in parsed)
            {
                var result = await Service.RemovePokemonFromTradeAsync(session.SessionId, ctx.User.Id, position);
                if (result.Success)
                    results.Add($"Removed Pokemon at position {position}");
                else
                    errors.Add($"Position {position}: {result.Message}");
            }

            await ReportAsync(session, results, errors, "No Pokemon were removed from the trade.", reset);
        }

        /// <summary>
        ///     Sets the credits the user offers.
        /// </summary>
        /// <param name="amount">The amount, with optional k or m suffix.</param>
        [Command("credits")]
        [Summary("Set the credits you offer in your trade.")]
        public async Task Credits(string amount)
        {
            var session = await GetCommandSessionAsync();
            if (session is null)
                return;

            if (!TradeInput.TryParseAmount(amount, out var credits))
            {
                await ErrorAsync($"Please enter a valid number of credits: {amount}");
                return;
            }

            if (credits == 0)
            {
                await ErrorAsync("You can't trade 0 credits!");
                return;
            }

            var reset = Reopen(session);
            var result = await Service.AddCreditsToTradeAsync(session.SessionId, ctx.User.Id, credits);
            await FinishChangeAsync(session, result.Success, result.Success
                ? $"Added {credits:N0} credits to the trade."
                : result.Message, reset);
        }

        /// <summary>
        ///     Adds tokens to the trade.
        /// </summary>
        /// <param name="type">The token type.</param>
        /// <param name="amount">The number of tokens.</param>
        [Command("tokens")]
        [Summary("Add radiant tokens to your trade.")]
        public async Task Tokens(string type, int amount)
        {
            var session = await GetCommandSessionAsync();
            if (session is null)
                return;

            if (!TradeInput.TryParseToken(type, out var tokenType))
            {
                await ErrorAsync($"Invalid token type: {type}. Valid types: {TradeInput.TokenTypeList}");
                return;
            }

            if (amount <= 0)
            {
                await ErrorAsync("Invalid token count, please enter a positive number.");
                return;
            }

            var reset = Reopen(session);
            var result = await Service.AddTokensToTradeAsync(session.SessionId, ctx.User.Id, tokenType, amount);
            await FinishChangeAsync(session, result.Success, result.Success
                ? $"Added {amount} {tokenType.GetDisplayName()} tokens to the trade."
                : result.Message, reset);
        }

        /// <summary>
        ///     Removes tokens from the trade.
        /// </summary>
        /// <param name="type">The token type.</param>
        /// <param name="amount">The number of tokens.</param>
        [Command("untokens")]
        [Summary("Remove radiant tokens from your trade.")]
        public async Task Untokens(string type, int amount)
        {
            var session = await GetCommandSessionAsync();
            if (session is null)
                return;

            if (!TradeInput.TryParseToken(type, out var tokenType))
            {
                await ErrorAsync($"Invalid token type: {type}. Valid types: {TradeInput.TokenTypeList}");
                return;
            }

            var reset = Reopen(session);
            var result = await Service.RemoveTokensFromTradeAsync(session.SessionId, ctx.User.Id, tokenType, amount);
            await FinishChangeAsync(session, result.Success, result.Message, reset);
        }

        /// <summary>
        ///     Confirms the trade, completing it once both users have confirmed.
        /// </summary>
        [Command("confirm")]
        [Summary("Confirm your trade. It completes once both users confirm.")]
        public async Task Confirm()
        {
            var session = await GetCommandSessionAsync();
            if (session is null)
                return;

            if (!session.HasItems())
            {
                await ErrorAsync("Cannot confirm an empty trade. Add items first.");
                return;
            }

            bool processing;
            bool bothConfirmed;
            lock (session)
            {
                processing = session.Status == TradeStatus.Processing;
                bothConfirmed = false;
                if (!processing)
                {
                    session.SetPlayerConfirmation(ctx.User.Id, true);
                    session.Status = TradeStatus.PendingConfirmation;
                    bothConfirmed = session.IsBothConfirmed();
                    if (bothConfirmed)
                        session.Status = TradeStatus.Processing;
                }
            }

            if (processing)
            {
                await ErrorAsync("This trade is already being processed.");
                return;
            }

            await Service.UpdateSessionInRedisAsync(session);

            if (!bothConfirmed)
            {
                await Service.UpdateTradeInterfaceAsync(session);
                await SendAsync(embed: new EmbedBuilder()
                    .WithTitle("✅ Trade Confirmed")
                    .WithDescription($"You have confirmed the trade. Waiting for <@{session.GetOtherPlayer(ctx.User.Id)}> to confirm...")
                    .WithColor(Color.Orange)
                    .WithFooter("The trade will execute once both players confirm."));
                return;
            }

            await CompleteTradeAsync(session);
        }

        /// <summary>
        ///     Cancels the trade.
        /// </summary>
        [Command("cancel")]
        [Summary("Cancel your trade.")]
        public async Task Cancel()
        {
            var session = await GetCommandSessionAsync();
            if (session is null)
                return;

            var result = await Service.CancelTradeSessionAsync(session.SessionId, ctx.User.Id);
            if (result.Success)
                await ConfirmAsync("Trade cancelled successfully! All trade locks have been removed.");
            else
                await ErrorAsync(result.Message);
        }

        private async Task<TradeSession?> GetCommandSessionAsync()
        {
            var session = await GetUserSessionAsync(ctx.User.Id);
            if (session is null)
            {
                var prefix = await GetPrefixAsync();
                if (await TradeLocks.IsUserTradeLockedAsync(ctx.User.Id))
                {
                    await Service.ClearOrphanedTradeLocksAsync(ctx.User.Id);
                    await ErrorAsync("Trade session not found (likely due to bot restart). Trade locks have been cleared for all participants. " +
                                     $"Please start a new trade with `{prefix}trade @user`.");
                }
                else
                {
                    await ErrorAsync($"You are not in a trade. Start one with `{prefix}trade @user`.");
                }

                return null;
            }

            if (session.Status == TradeStatus.Processing)
            {
                await ErrorAsync("This trade is already being processed.");
                return null;
            }

            return session;
        }

        private static bool Reopen(TradeSession session)
        {
            lock (session)
            {
                if (session.Status != TradeStatus.PendingConfirmation)
                    return false;

                session.ResetConfirmations();
                session.Status = TradeStatus.Active;
                return true;
            }
        }

        private async Task ReportAsync(TradeSession session, List<string> results, List<string> errors,
            string nothing, bool reset)
        {
            var response = string.Join("\n", results);
            if (errors.Count > 0)
            {
                if (results.Count > 0)
                    response += "\n\n**Errors:**\n";
                response += string.Join("\n", errors);
            }

            if (string.IsNullOrEmpty(response))
                response = nothing;

            await FinishChangeAsync(session, results.Count > 0, response, reset);
        }

        private async Task FinishChangeAsync(TradeSession session, bool success, string message, bool reset)
        {
            if (success || reset)
            {
                await Service.UpdateSessionInRedisAsync(session);
                await Service.UpdateTradeInterfaceAsync(session);
            }

            if (reset)
                message += "\nThe trade changed, so both players need to confirm again.";

            if (success)
                await ConfirmAsync(message);
            else
                await ErrorAsync(message);
        }

        private async Task CompleteTradeAsync(TradeSession session)
        {
            var fraudResult = await FraudDetection.AnalyzeTradeAsync(session);
            if (!fraudResult.IsAllowed)
            {
                session.Status = TradeStatus.Failed;
                await Service.UpdateSessionInRedisAsync(session);
                await Service.ClearOrphanedTradeLocksAsync(session.Player1Id);
                await Service.ClearOrphanedTradeLocksAsync(session.Player2Id);

                var blocked = new EmbedBuilder()
                    .WithTitle("❌ Trade Blocked")
                    .WithDescription(fraudResult.Message ?? "Trade blocked due to suspicious activity.")
                    .WithColor(Color.Red);
                await ShowResultAsync(session, blocked);
                await SendAsync(embed: blocked);
                return;
            }

            var executeResult = await Service.ExecuteTradeAsync(session.SessionId);
            await TradeLocks.RemoveTradeLockAsync(session.Player1Id);
            await TradeLocks.RemoveTradeLockAsync(session.Player2Id);

            if (!executeResult.Success)
            {
                var failed = new EmbedBuilder()
                    .WithTitle("❌ Trade Failed")
                    .WithDescription(executeResult.Message)
                    .WithColor(Color.Red);
                await ShowResultAsync(session, failed);
                await SendAsync(embed: failed);
                return;
            }

            await ShowResultAsync(session, new EmbedBuilder()
                .WithTitle("🎉 Trade Completed!")
                .WithDescription(await Service.GenerateTradeSummaryAsync(session))
                .WithColor(Color.Green)
                .WithFooter($"Session ID: {session.SessionId}")
                .WithTimestamp(DateTimeOffset.UtcNow));

            await SendAsync(embed: new EmbedBuilder()
                .WithTitle("🎉 Trade Completed!")
                .WithDescription("The trade has been completed successfully! Checking for trade evolutions...")
                .WithColor(Color.Green));

            await ProcessTradeEvolutionsAsync(session);
        }

        private static async Task ShowResultAsync(TradeSession session, EmbedBuilder embed)
        {
            if (session.TradeMessage is null)
                return;

            try
            {
                await session.TradeMessage.ModifyAsync(embeds: [embed.Build()]);
            }
            catch (FluxerApiException ex)
            {
                Log.Debug(ex, "Could not update trade message for session {SessionId}", session.SessionId);
            }
        }

        private async Task ProcessTradeEvolutionsAsync(TradeSession session)
        {
            try
            {
                foreach (var pokemonEntry in session.GetPokemonBy(session.Player2Id))
                {
                    await TradeEvolution.CheckAndProcessTradeEvolutionAsync(
                        pokemonEntry.PokemonId!.Value, session.Player1Id, ctx.Channel);
                }

                foreach (var pokemonEntry in session.GetPokemonBy(session.Player1Id))
                {
                    await TradeEvolution.CheckAndProcessTradeEvolutionAsync(
                        pokemonEntry.PokemonId!.Value, session.Player2Id, ctx.Channel);
                }
            }
            catch (Exception ex)
            {
                Log.Information($"Error processing trade evolutions: {ex.Message}");
            }
        }
    }
}
