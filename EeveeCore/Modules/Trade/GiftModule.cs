using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Trade.Models;
using EeveeCore.Modules.Trade.Services;

namespace EeveeCore.Modules.Trade;

/// <summary>
///     Gifting credits, redeems, Pokémon and tokens to other players.
/// </summary>
[Name("Gift")]
[Group("gift")]
public class GiftModule : EeveeModule<GiftService>
{
    /// <summary>
    ///     Gifts credits to another user.
    /// </summary>
    /// <param name="user">The user to gift credits to.</param>
    /// <param name="amount">The amount, with optional k or m suffix.</param>
    [Command("credits")]
    [Summary("Gift credits to another user.")]
    [RequireNotTradeLocked]
    public async Task Credits(IUser user, string amount)
    {
        if (!TradeInput.TryParseAmount(amount, out var credits) || credits == 0)
        {
            await SendErrorAsync("❌ Invalid Amount", "You need to give at least 1 credit!");
            return;
        }

        if (ctx.User.Id == user.Id)
        {
            await SendErrorAsync("❌ Cannot Gift Yourself", "You cannot give yourself credits.");
            return;
        }

        if (!await ConfirmGiftAsync($"Are you sure you want to give **{credits:N0}** credits to {user.Mention}?"))
            return;

        var result = await Service.GiftCreditsAsync(ctx.User.Id, user.Id, credits);
        await SendResultAsync(result, $"{ctx.User.Mention} has gifted **{credits:N0}** credits to {user.Mention}!");
    }

    /// <summary>
    ///     Gifts redeems to another user.
    /// </summary>
    /// <param name="user">The user to gift redeems to.</param>
    /// <param name="amount">The number of redeems.</param>
    [Command("redeems")]
    [Summary("Gift redeems to another user.")]
    [RequireNotTradeLocked]
    public async Task Redeems(IUser user, int amount)
    {
        if (amount <= 0)
        {
            await SendErrorAsync("❌ Invalid Amount", "You need to give at least 1 redeem!");
            return;
        }

        if (ctx.User.Id == user.Id)
        {
            await SendErrorAsync("❌ Cannot Gift Yourself", "You cannot give yourself redeems.");
            return;
        }

        if (!await ConfirmGiftAsync($"Are you sure you want to give **{amount}** redeems to {user.Mention}?"))
            return;

        var result = await Service.GiftRedeemsAsync(ctx.User.Id, user.Id, amount);
        await SendResultAsync(result, $"{ctx.User.Mention} has gifted **{amount}** redeems to {user.Mention}!");
    }

    /// <summary>
    ///     Gifts a Pokémon to another user.
    /// </summary>
    /// <param name="user">The user to receive the Pokémon.</param>
    /// <param name="pokemon">The position of the Pokémon.</param>
    [Command("pokemon")]
    [Summary("Gift a Pokemon to another user.")]
    [RequireNotTradeLocked]
    public async Task Pokemon(IUser user, int pokemon)
    {
        if (pokemon <= 1)
        {
            await SendErrorAsync("❌ Cannot Gift That Pokemon", "You cannot give away your first Pokemon or use position 0!");
            return;
        }

        if (ctx.User.Id == user.Id)
        {
            await SendErrorAsync("❌ Cannot Gift Yourself", "You cannot give a Pokemon to yourself.");
            return;
        }

        if (!await ConfirmGiftAsync($"Are you sure you want to give your Pokemon at position **{pokemon}** to {user.Mention}?"))
            return;

        var result = await Service.GiftPokemonAsync(ctx.User.Id, user.Id, pokemon);
        await SendResultAsync(result, $"{ctx.User.Mention} has gifted a **{result.Amount}** to {user.Mention}!");
    }

    /// <summary>
    ///     Gifts radiant tokens to another user.
    /// </summary>
    /// <param name="user">The user to gift tokens to.</param>
    /// <param name="type">The token type.</param>
    /// <param name="amount">The number of tokens.</param>
    [Command("tokens")]
    [Summary("Gift radiant tokens to another user.")]
    [RequireNotTradeLocked]
    public async Task Tokens(IUser user, string type, int amount)
    {
        if (amount <= 0)
        {
            await SendErrorAsync("❌ Invalid Amount", "You need to gift at least 1 token!");
            return;
        }

        if (ctx.User.Id == user.Id)
        {
            await SendErrorAsync("❌ Cannot Gift Yourself", "You cannot give yourself tokens.");
            return;
        }

        if (!TradeInput.TryParseToken(type, out var tokenType))
        {
            await SendErrorAsync("❌ Invalid Token Type",
                $"Invalid token type: {type}. Valid types: {TradeInput.TokenTypeList}");
            return;
        }

        var tokens = $"**{amount}** {tokenType.GetEmoji()} **{tokenType.GetDisplayName()}** tokens";
        if (!await ConfirmGiftAsync($"Are you sure you want to give {tokens} to {user.Mention}?"))
            return;

        var result = await Service.GiftTokensAsync(ctx.User.Id, user.Id, tokenType, amount);
        await SendResultAsync(result, $"{ctx.User.Mention} has gifted {tokens} to {user.Mention}!");
    }

    /// <summary>
    ///     Explains how gifting works.
    /// </summary>
    [Command("help")]
    [Summary("Get help with gifting.")]
    public async Task Help()
    {
        var prefix = await GetPrefixAsync();
        var embed = new EmbedBuilder()
            .WithTitle("🎁 Gifting Help")
            .WithDescription("Learn how to gift items to other users!")
            .WithColor(Color.Gold)
            .AddField("**Gift Credits**",
                $"Use `{prefix}gift credits @user amount` to give Coins to another user.\n" +
                $"Example: `{prefix}gift credits @friend 10000`")
            .AddField("**Gift Redeems**",
                $"Use `{prefix}gift redeems @user amount` to give redeems to another user.\n" +
                $"Example: `{prefix}gift redeems @friend 3`")
            .AddField("**Gift Pokemon**",
                $"Use `{prefix}gift pokemon @user position` to give a Pokemon to another user.\n" +
                $"Example: `{prefix}gift pokemon @friend 5`\n" +
                "⚠️ You cannot gift your #1 Pokemon, eggs, or favorited Pokemon")
            .AddField("**Gift Tokens**",
                $"Use `{prefix}gift tokens @user type amount` to give radiant tokens.\n" +
                $"Example: `{prefix}gift tokens @friend Fire 5`\n" +
                $"Available types: {TradeInput.TokenTypeList}")
            .AddField("**Important Notes**",
                "• All gifts require confirmation before sending\n" +
                "• Gifts cannot be undone once sent\n" +
                $"• Both users must have started (`{prefix}start`) to gift\n" +
                "• Trade-locked users cannot send or receive gifts\n" +
                "• All gifts are logged for security")
            .WithFooter($"Use {prefix}trade to exchange items with another user!")
            .WithTimestamp(DateTimeOffset.UtcNow);

        await SendAsync(embed: embed);
    }

    private async Task<bool> ConfirmGiftAsync(string question)
    {
        var confirmed = await PromptConfirmAsync(new EmbedBuilder()
            .WithTitle("🎁 Confirm Gift")
            .WithDescription(question)
            .WithColor(Color.Orange)
            .WithFooter("This action cannot be undone."));

        if (!confirmed)
            await SendErrorAsync("❌ Gift Cancelled", "The gift has been cancelled.");

        return confirmed;
    }

    private async Task SendResultAsync(GiftResult result, string success)
    {
        if (!result.Success)
        {
            await SendErrorAsync("❌ Gift Failed", TradeInput.WithPrefix(result.Message, await GetPrefixAsync()));
            return;
        }

        await SendAsync(embed: new EmbedBuilder()
            .WithTitle("🎉 Gift Successful!")
            .WithDescription(success)
            .WithColor(Color.Green)
            .WithTimestamp(DateTimeOffset.UtcNow));
    }

    private Task<Message> SendErrorAsync(string title, string description) =>
        SendAsync(embed: new EmbedBuilder().WithTitle(title).WithDescription(description).WithColor(Color.Red));
}
