using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Fishing.Services;
using Serilog;

namespace EeveeCore.Modules.Fishing;

/// <summary>
///     Fishing for Pokémon with a rod.
/// </summary>
/// <param name="tradeLock">The trade locks, so players cannot fish mid-trade.</param>
[Group("fish")]
[Name("Fishing")]
[RequireContext(ContextType.Guild)]
public class FishingModule(ITradeLockService tradeLock) : EeveeModule<FishingService>
{
    /// <summary>
    ///     Casts the rod. The caster then mentions the bot with the Pokémon's name to catch it.
    /// </summary>
    [Command("cast")]
    [Summary("Cast your fishing rod to catch Pokémon!")]
    public async Task Cast()
    {
        if (await tradeLock.IsUserTradeLockedAsync(ctx.User.Id))
        {
            await ErrorAsync($"{ctx.User.Username} is currently in a trade!");
            return;
        }

        try
        {
            await tradeLock.AddTradeLockAsync(ctx.User.Id);
            var prefix = await GetPrefixAsync();
            var result = await Service.HandleFishing(ctx);

            if (!result.Success)
            {
                await ErrorAsync(result.Message!.Replace("`/", $"`{prefix}"));
                return;
            }

            var embed = result.ResponseEmbed!;
            embed.Description += $"\nMention <@{ctx.Client.Gateway.CurrentUser?.Id}> with the name to catch it!";
            var message = await SendFishingMessageAsync(embed);
            await Service.StoreFishingData(ctx.Channel.Id, message.Id, result);

            if (result.ShowMultiBox)
                await HandleMultiBoxAsync();

            if (result.RemainingEnergy <= 0)
                await ReplyAsync("Sorry, you seem to be out of energy! Energy comes back over time.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in fishing command for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while fishing. Please try again later.");
        }
        finally
        {
            await tradeLock.RemoveTradeLockAsync(ctx.User.Id);
        }
    }

    private async Task<Message> SendFishingMessageAsync(EmbedRequest embed)
    {
        var fishingImagePath = Path.Combine("data", "images", "fishing.gif");
        if (Media.Get(fishingImagePath) is { } hosted)
        {
            embed.Image = new EmbedMediaRequest { Url = hosted };
            return await ReplyAsync(embeds: [embed]);
        }

        if (File.Exists(fishingImagePath))
        {
            try
            {
                await using var fileStream = new FileStream(fishingImagePath, FileMode.Open, FileAccess.Read);
                return await ReplyAsync(embeds: [embed], attachments: [new AttachmentRequest(fileStream, "fishing.gif")]);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to send fishing image attachment");
            }
        }
        else
        {
            Log.Warning("Fishing image not found at path: {ImagePath}", fishingImagePath);
        }

        embed.Image = null;
        embed.Description = $"{embed.Description}\n\n*[Image not available]*";
        return await ReplyAsync(embeds: [embed]);
    }

    private async Task HandleMultiBoxAsync()
    {
        var boxMessage = await ReplyAsync("You fished up a box with question marks printed on all sides!");
        await Task.Delay(3000);

        var result = await Service.HandleMultiBox(ctx.User.Id);
        await boxMessage.ModifyAsync(result);
    }
}
