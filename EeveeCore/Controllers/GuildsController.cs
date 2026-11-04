using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Spawn.Services;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     Server settings, for members with Manage Server, the server owner and bot owners.
/// </summary>
/// <param name="client">The Fluxer client, for the gateway cache.</param>
/// <param name="credentials">The bot credentials, for the bot owners.</param>
/// <param name="guildSettings">The prefix settings.</param>
/// <param name="spawnService">The spawn settings.</param>
[Route("api/guilds")]
public class GuildsController(
    FluxerClient client,
    BotCredentials credentials,
    GuildSettingsService guildSettings,
    SpawnService spawnService) : PokeFluxUserControllerBase
{
    private const int MaxPrefixLength = 10;

    /// <summary>
    ///     Lists the servers the user shares with the bot and can manage.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var managed = new List<GuildSummary>();
        foreach (var guild in client.Gateway.Guilds.Values.OrderBy(g => g.Name))
            if (await CanManageAsync(guild, true))
                managed.Add(new GuildSummary(guild.Id, guild.Name, guild.GetIconUrl(), guild.OwnerId == UserId));
        return Ok(managed);
    }

    /// <summary>
    ///     Gets a server's prefix, spawn settings and text channels.
    /// </summary>
    /// <param name="guildId">The server's ID.</param>
    [HttpGet("{guildId}")]
    public async Task<IActionResult> Get(ulong guildId)
    {
        var (guild, error) = await GetManagedGuildAsync(guildId);
        if (guild is null)
            return error!;

        var config = await spawnService.GetGuildConfig(guildId);
        var channels = TextChannels(guild)
            .OrderBy(c => c.Position ?? 0)
            .Select(c => new GuildChannelInfo(c.Id, c.Name ?? "", c.Position ?? 0, c.ParentId))
            .ToList();
        var categories = guild.Channels.Values
            .Where(c => c.Type == ChannelType.GuildCategory)
            .OrderBy(c => c.Position ?? 0)
            .Select(c => new GuildCategoryInfo(c.Id, c.Name ?? "", c.Position ?? 0))
            .ToList();

        return Ok(new GuildSettingsResponse(guild.Id, guild.Name, guild.GetIconUrl(),
            await guildSettings.GetPrefix(guild),
            new SpawnSettings(config.EnableSpawnsAll, config.EnabledChannels ?? [],
                config.DisabledSpawnChannels ?? [], config.Redirects ?? [], config.Speed, config.SmallImages,
                config.ModalView, config.DeleteSpawns, config.PinSpawns),
            channels, categories));
    }

    /// <summary>
    ///     Changes a server's prefix.
    /// </summary>
    /// <param name="guildId">The server's ID.</param>
    /// <param name="request">The new prefix.</param>
    [HttpPut("{guildId}/prefix")]
    public async Task<IActionResult> SetPrefix(ulong guildId, [FromBody] PrefixRequest request)
    {
        var (guild, error) = await GetManagedGuildAsync(guildId);
        if (guild is null)
            return error!;

        var prefix = request.Prefix?.Trim();
        if (string.IsNullOrEmpty(prefix) || prefix.Contains(' '))
            return Error(400, "A prefix without spaces is required.");
        if (prefix.Length > MaxPrefixLength)
            return Error(400, "The prefix can be at most 10 characters.");

        await guildSettings.SetPrefix(guild, prefix);
        return Ok(new ApiMessage($"The prefix here is now `{prefix}`."));
    }

    /// <summary>
    ///     Changes some of a server's spawn settings.
    /// </summary>
    /// <param name="guildId">The server's ID.</param>
    /// <param name="request">The settings to change.</param>
    [HttpPut("{guildId}/spawns")]
    public async Task<IActionResult> SetSpawns(ulong guildId, [FromBody] SpawnSettingsRequest request)
    {
        var (guild, error) = await GetManagedGuildAsync(guildId);
        if (guild is null)
            return error!;

        if (request.Speed is < 1 or > 20)
            return Error(400, "The speed must be between 1 and 20.");

        var textChannels = TextChannels(guild).Select(c => c.Id).ToHashSet();
        var channels = new[] { request.EnabledChannels, request.DisabledChannels, request.Redirects };
        if (channels.Any(list => list?.Any(id => !textChannels.Contains(id)) == true))
            return Error(400, "That channel is not a text channel in this community.");

        await spawnService.UpdateSpawnSettings(guildId, new SpawnSettingsUpdate(
            request.EnableSpawnsAll,
            request.EnabledChannels?.Distinct().ToList(),
            request.DisabledChannels?.Distinct().ToList(),
            request.Redirects?.Distinct().ToList(),
            request.Speed,
            request.SmallImages,
            request.ModalView,
            request.Delete,
            request.Pin));

        return Ok(new ApiMessage("Spawn settings updated."));
    }

    private async Task<(SocketGuild? Guild, IActionResult? Error)> GetManagedGuildAsync(ulong guildId)
    {
        if (client.Gateway.GetGuild(guildId) is not { } guild)
            return (null, Error(404, "PokeFlux is not in that community."));

        return await CanManageAsync(guild)
            ? (guild, null)
            : (null, Error(403, "You need Manage Community in that community."));
    }

    private async Task<bool> CanManageAsync(SocketGuild guild, bool requireMember = false)
    {
        if (guild.OwnerId == UserId)
            return true;

        SocketGuildMember? member;
        try
        {
            member = await guild.GetMemberAsync(UserId);
        }
        catch (FluxerApiException)
        {
            member = null;
        }

        if (credentials.OwnerIds.Contains(UserId))
            return member is not null || !requireMember;

        return member is not null &&
               (member.GuildPermissions.Has(GuildPermission.Administrator) ||
                member.GuildPermissions.Has(GuildPermission.ManageGuild));
    }

    private static IEnumerable<Channel> TextChannels(SocketGuild guild) =>
        guild.Channels.Values.Where(c => c.Type == ChannelType.GuildText);
}
