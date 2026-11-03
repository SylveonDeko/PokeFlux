namespace EeveeCore.Controllers.Models;

/// <summary>
///     A server the user can manage.
/// </summary>
/// <param name="Id">The server's ID.</param>
/// <param name="Name">The server's name.</param>
/// <param name="IconUrl">The server's icon, or null.</param>
/// <param name="Owner">Whether the user owns the server.</param>
public record GuildSummary(ulong Id, string Name, string? IconUrl, bool Owner);

/// <summary>
///     A text channel of a server.
/// </summary>
/// <param name="Id">The channel's ID.</param>
/// <param name="Name">The channel's name.</param>
/// <param name="Position">The channel's position.</param>
/// <param name="ParentId">The ID of the channel's category, or null.</param>
public record GuildChannelInfo(ulong Id, string Name, int Position, ulong? ParentId);

/// <summary>
///     A channel category of a server.
/// </summary>
/// <param name="Id">The category's ID.</param>
/// <param name="Name">The category's name.</param>
/// <param name="Position">The category's position.</param>
public record GuildCategoryInfo(ulong Id, string Name, int Position);

/// <summary>
///     A server's spawn settings.
/// </summary>
/// <param name="EnableSpawnsAll">Whether Pokémon spawn in every channel.</param>
/// <param name="EnabledChannels">The channels Pokémon spawn in.</param>
/// <param name="DisabledChannels">The channels Pokémon never spawn in.</param>
/// <param name="Redirects">The channels spawns are sent to.</param>
/// <param name="Speed">How often Pokémon spawn, from 1 to 20.</param>
/// <param name="SmallImages">Whether spawns show small images.</param>
/// <param name="ModalView">Whether spawns are only caught with the catch command.</param>
/// <param name="Delete">Whether spawn messages are deleted once caught.</param>
/// <param name="Pin">Whether caught spawn messages are pinned.</param>
public record SpawnSettings(
    bool EnableSpawnsAll,
    List<ulong> EnabledChannels,
    List<ulong> DisabledChannels,
    List<ulong> Redirects,
    int Speed,
    bool SmallImages,
    bool ModalView,
    bool Delete,
    bool Pin);

/// <summary>
///     A server's settings and text channels.
/// </summary>
/// <param name="Id">The server's ID.</param>
/// <param name="Name">The server's name.</param>
/// <param name="IconUrl">The server's icon, or null.</param>
/// <param name="Prefix">The command prefix.</param>
/// <param name="Spawns">The spawn settings.</param>
/// <param name="Channels">The text channels.</param>
/// <param name="Categories">The channel categories.</param>
public record GuildSettingsResponse(
    ulong Id,
    string Name,
    string? IconUrl,
    string Prefix,
    SpawnSettings Spawns,
    List<GuildChannelInfo> Channels,
    List<GuildCategoryInfo> Categories);

/// <summary>
///     Changes the prefix.
/// </summary>
/// <param name="Prefix">The new prefix.</param>
public record PrefixRequest(string Prefix);

/// <summary>
///     Spawn settings to change. Settings left out or null stay as they are.
/// </summary>
/// <param name="EnableSpawnsAll">Whether Pokémon spawn in every channel.</param>
/// <param name="EnabledChannels">The channels Pokémon spawn in.</param>
/// <param name="DisabledChannels">The channels Pokémon never spawn in.</param>
/// <param name="Redirects">The channels spawns are sent to.</param>
/// <param name="Speed">How often Pokémon spawn, from 1 to 20.</param>
/// <param name="SmallImages">Whether spawns show small images.</param>
/// <param name="ModalView">Whether spawns are only caught with the catch command.</param>
/// <param name="Delete">Whether spawn messages are deleted once caught.</param>
/// <param name="Pin">Whether caught spawn messages are pinned.</param>
public record SpawnSettingsRequest(
    bool? EnableSpawnsAll,
    List<ulong>? EnabledChannels,
    List<ulong>? DisabledChannels,
    List<ulong>? Redirects,
    int? Speed,
    bool? SmallImages,
    bool? ModalView,
    bool? Delete,
    bool? Pin);
