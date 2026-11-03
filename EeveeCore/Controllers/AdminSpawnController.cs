using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using EeveeCore.Modules.Spawn.Services;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     The owner-only spawn tools, gated like the letsgo and debug commands.
/// </summary>
/// <param name="client">The Fluxer client, for the gateway cache.</param>
/// <param name="spawnService">The spawn service.</param>
/// <param name="audit">Records staff actions.</param>
[Route("api/admin/spawns")]
[DashBotOwner]
public class AdminSpawnController(FluxerClient client, SpawnService spawnService, StaffAuditService audit)
    : AdminControllerBase
{
    /// <summary>
    ///     Gets whether spawn storm mode is on.
    /// </summary>
    [HttpGet("storm")]
    public IActionResult Storm() => Ok(new AdminSpawnStorm(spawnService.AlwaysSpawn));

    /// <summary>
    ///     Turns spawn storm mode on or off, like the letsgo command.
    /// </summary>
    /// <param name="request">The reason.</param>
    [HttpPost("storm")]
    public async Task<IActionResult> ToggleStorm([FromBody] AdminReasonRequest request)
    {
        if (ReadReason(request.Reason) is not { } reason)
            return ReasonRequired();

        var enabled = spawnService.ToggleAlwaysSpawn();
        await audit.RecordAsync(UserId, UserName, "spawn_storm", reason, null, enabled ? "Turned on" : "Turned off");
        return Ok(new AdminSpawnStorm(enabled, enabled ? "Spawn storm is on." : "Spawn storm is off."));
    }

    /// <summary>
    ///     Gets a server's spawn debug output, like the debug command.
    /// </summary>
    /// <param name="guildId">The server's ID.</param>
    [HttpGet("debug/{guildId}")]
    public async Task<IActionResult> Debug(ulong guildId)
    {
        if (client.Gateway.GetGuild(guildId) is not { } guild)
            return Error(404, "PokeFlux is not in that server.");

        var info = await spawnService.GetSpawnDebugInfo(guild);
        return Ok(new AdminSpawnDebug(info.Split('\n').ToList()));
    }
}
