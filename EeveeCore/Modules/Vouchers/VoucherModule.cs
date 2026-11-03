using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Vouchers.Common;
using EeveeCore.Modules.Vouchers.Models;
using EeveeCore.Modules.Vouchers.Services;
using Serilog;

namespace EeveeCore.Modules.Vouchers;

/// <summary>
///     Voucher requests for custom Pokemon artwork.
/// </summary>
/// <param name="credentials">The bot settings, for the voucher channel and managers.</param>
[Name("Vouchers")]
public class VoucherModule(BotCredentials credentials) : EeveeModule<VoucherService>
{
    /// <summary>
    ///     Shows how to fill in a voucher request.
    /// </summary>
    [Command("voucher request")]
    [Summary("See how to submit a voucher request for custom Pokemon artwork.")]
    public async Task Request()
    {
        if (!await CheckCanRequestAsync())
            return;

        var prefix = await GetPrefixAsync();
        var embed = new EmbedBuilder()
            .WithTitle("🎫 Voucher Request Form")
            .WithDescription("Fill in every field, separated by `|`:\n" +
                             $"`{prefix}voucher request <pokemon> | <appearance> | <payment> [| <artist>]`\n\n" +
                             $"For example: `{prefix}voucher request Pikachu | Wearing a red scarf, sitting | Credits`")
            .AddField("🎯 Pokemon", "What Pokemon would you like?")
            .AddField("🎨 Appearance", "How do you want the Pokemon to look? Describe the appearance, pose, style, etc.")
            .AddField("💰 Payment", "What is your method of payment? e.g., Credits, PayPal, Commission trade")
            .AddField("👨‍🎨 Artist", "Optional. Mention the artist you have in mind, or give their user ID.")
            .WithColor(Color.Orange);

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Submits a voucher request after the user confirms it.
    /// </summary>
    /// <param name="form">Pokemon, appearance, payment and an optional artist, separated by <c>|</c>.</param>
    [Command("voucher request")]
    [Summary("Submit a voucher request: pokemon | appearance | payment [| artist].")]
    public async Task Request([Remainder] string form)
    {
        var prefix = await GetPrefixAsync();
        var parts = form.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4 || parts.Take(3).Any(string.IsNullOrWhiteSpace))
        {
            await ErrorAsync("Please fill out all required fields before submitting. Use " +
                             $"`{prefix}voucher request <pokemon> | <appearance> | <payment> [| <artist>]`.");
            return;
        }

        var formData = new VoucherRequestFormData
        {
            Pokemon = parts[0],
            Appearance = parts[1],
            PaymentMethod = parts[2]
        };

        if (formData.Pokemon.Length > 50 || formData.Appearance.Length > 500 || formData.PaymentMethod.Length > 200)
        {
            await ErrorAsync("The Pokemon can be at most 50 characters, the appearance 500 and the payment 200.");
            return;
        }

        if (parts.Length == 4 && !string.IsNullOrWhiteSpace(parts[3]))
        {
            if (!ulong.TryParse(parts[3].Trim('<', '@', '!', '>'), out var artist))
            {
                await ErrorAsync("Give the artist as a mention or a user ID, or leave it out for no preference.");
                return;
            }

            formData.Artist = artist;
        }

        if (!await CheckCanRequestAsync())
            return;

        var preview = FormEmbed(formData)
            .WithDescription("Submit this request? React with ✅ to submit or ❌ to cancel.");
        if (!await PromptConfirmAsync(preview))
        {
            await ConfirmAsync("Voucher request cancelled.");
            return;
        }

        try
        {
            var artistText = formData.Artist == 0 ? "No preference" : $"<@{formData.Artist}>";
            var requestEmbed = new EmbedBuilder()
                .WithTitle("New Voucher Request")
                .WithDescription($"Requested by {ctx.User.Mention}")
                .WithColor(Color.Blue)
                .AddField("Pokemon", formData.Pokemon, true)
                .AddField("Appearance", formData.Appearance)
                .AddField("Payment Method", formData.PaymentMethod, true)
                .AddField("Artist Preference", artistText, true)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            Message post;
            try
            {
                post = await ctx.Client.Rest.SendMessageAsync(credentials.VoucherChannelId,
                    $"Voucher Request - {ctx.User.Username}", [requestEmbed]);
            }
            catch (FluxerApiException e)
            {
                Log.Warning(e, "Could not post voucher request to channel {ChannelId}", credentials.VoucherChannelId);
                await ErrorAsync("Voucher requests are not set up yet. Please contact staff.");
                return;
            }

            await post.ModifyAsync($"Voucher Request - {ctx.User.Username}\n" +
                                   $"Request #{post.Id}. Staff update it with `voucher setstatus {post.Id} <status...>`.",
                [requestEmbed]);

            await Service.CreateVoucherRequestAsync(ctx.User.Id, formData, post.Id, credentials.VoucherChannelId);
            await ConfirmAsync("✅ Your voucher request has been submitted successfully!");
        }
        catch (Exception e)
        {
            Log.Error(e, "Error submitting voucher request for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while submitting your request. Please try again.");
        }
    }

    /// <summary>
    ///     Shows the status of the user's voucher requests.
    /// </summary>
    [Command("voucher status")]
    [Summary("View the status of your voucher requests.")]
    public async Task Status()
    {
        var requests = await Service.GetUserVoucherRequestsAsync(ctx.User.Id);
        if (requests.Count == 0)
        {
            await SendAsync(embed: new EmbedBuilder()
                .WithTitle("📋 Your Voucher Requests")
                .WithDescription("You haven't submitted any voucher requests yet.\n\n" +
                                 $"Use `{await GetPrefixAsync()}voucher request` to create your first request!")
                .WithColor(Color.Blue));
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("📋 Your Voucher Requests")
            .WithDescription($"You have {requests.Count} voucher request(s):")
            .WithColor(Color.Blue);

        foreach (var request in requests.Take(10))
            embed.AddField($"Request #{request.RequestId}",
                $"**Status:** {VoucherService.FormatStatusList(request.Status)}\n" +
                $"**Pokemon:** {(string.IsNullOrEmpty(request.Pokemon) ? "Not specified" : request.Pokemon)}\n" +
                $"**Created:** <t:{request.CreatedAt.ToUnixTimeSeconds()}:R>");

        if (requests.Count > 10)
            embed.WithFooter($"... and {requests.Count - 10} more requests");

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Explains the voucher request system.
    /// </summary>
    [Command("voucher info")]
    [Summary("Learn about the voucher request system.")]
    public async Task Info()
    {
        var prefix = await GetPrefixAsync();
        var embed = new EmbedBuilder()
            .WithTitle("🎫 Voucher Request System")
            .WithDescription("Welcome to the voucher request system! Here's everything you need to know:")
            .WithColor(Color.Purple);

        embed.AddField("🎯 **What are Vouchers?**",
            "Vouchers are special currency that can be redeemed for custom Pokemon artwork.\n" +
            "They're earned through various activities and events in the bot.");

        embed.AddField("🎨 **How to Use Vouchers**",
            $"1. Use `{prefix}voucher request` to see the request form\n" +
            "2. Fill it in with your Pokemon and appearance details\n" +
            "3. Specify your payment method and artist preferences\n" +
            "4. Submit your request and wait for processing");

        embed.AddField("📋 **Request Process**",
            "• Your request will be reviewed by staff\n" +
            "• Artists will be assigned based on availability\n" +
            "• You'll be notified of status updates\n" +
            "• Completed artwork will be delivered to you");

        embed.AddField("⚠️ **Important Notes**",
            "• You can have multiple active requests\n" +
            "• Requests cannot be cancelled once submitted\n" +
            "• Processing time varies based on complexity\n" +
            "• Follow community guidelines for requests");

        embed.AddField("🔧 **Commands**",
            $"`{prefix}voucher request` - Submit a new request\n" +
            $"`{prefix}voucher status` - View your request status\n" +
            $"`{prefix}voucher info` - Show this information");

        embed.WithFooter("Happy requesting! 🎨");

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Sets the statuses of a voucher request. Only voucher managers can use it.
    /// </summary>
    /// <param name="requestId">The request number.</param>
    /// <param name="statuses">One or more statuses.</param>
    [Command("voucher setstatus")]
    [Summary("Update a voucher request's status. Voucher managers only.")]
    public async Task SetStatus(ulong requestId, [Remainder] string statuses)
    {
        if (!credentials.VoucherManagerIds.Contains(ctx.User.Id) && !credentials.OwnerIds.Contains(ctx.User.Id))
        {
            await ErrorAsync("You don't have permission to manage voucher requests.");
            return;
        }

        var picked = new List<string>();
        foreach (var word in statuses.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var status = VoucherConstants.StatusOptions.FirstOrDefault(s =>
                s.Equals(word, StringComparison.OrdinalIgnoreCase));
            if (status is null)
            {
                await ErrorAsync($"`{word}` is not a status. Pick from " +
                                 $"{string.Join(", ", VoucherConstants.StatusOptions)}.");
                return;
            }

            if (!picked.Contains(status))
                picked.Add(status);
        }

        try
        {
            if (await Service.UpdateVoucherRequestStatusAsync(requestId, picked))
                await ConfirmAsync($"Status updated to: {string.Join(", ", picked)}");
            else
                await ErrorAsync("Failed to update status. Request may not exist.");
        }
        catch (Exception e)
        {
            Log.Error(e, "Error updating voucher request status");
            await ErrorAsync("An error occurred while updating the status.");
        }
    }

    private async Task<bool> CheckCanRequestAsync()
    {
        if (!await Service.HasAvailableVouchersAsync(ctx.User.Id))
        {
            await SendAsync(embed: new EmbedBuilder()
                .WithTitle("❌ No Vouchers Available")
                .WithDescription("You don't have any vouchers available to spend.\n\n" +
                                 "Vouchers can be earned through various activities in the bot.")
                .WithColor(Color.Red));
            return false;
        }

        if (await Service.CanUserCreateMoreRequestsAsync(ctx.User.Id))
            return true;

        var existingRequests = await Service.GetUserVoucherRequestsAsync(ctx.User.Id);
        var limitEmbed = new EmbedBuilder()
            .WithTitle("⚠️ Request Limit Reached")
            .WithDescription($"You have reached the maximum number of active voucher requests ({existingRequests.Count}).\n\n" +
                             "Please wait for your current requests to be processed before submitting new ones.")
            .WithColor(Color.Orange);

        if (existingRequests.Count > 0)
            limitEmbed.AddField("Your Active Requests", string.Join("\n", existingRequests.Select(r =>
                $"• Request #{r.RequestId}: {VoucherService.FormatStatusList(r.Status)}")));

        await SendAsync(embed: limitEmbed);
        return false;
    }

    private static EmbedBuilder FormEmbed(VoucherRequestFormData formData) =>
        new EmbedBuilder()
            .WithTitle("🎫 Voucher Request Form")
            .WithColor(formData.IsComplete ? Color.Green : Color.Orange)
            .AddField("🎯 Pokemon", $"✅ {formData.Pokemon}", true)
            .AddField("🎨 Appearance", $"✅ {formData.Appearance}")
            .AddField("💰 Payment", $"✅ {formData.PaymentMethod}", true)
            .AddField("👨‍🎨 Artist", formData.Artist == 0 ? "❌ Not specified" : $"✅ <@{formData.Artist}>", true);
}
