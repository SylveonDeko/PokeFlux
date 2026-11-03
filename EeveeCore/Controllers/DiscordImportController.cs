using EeveeCore.Controllers.Common;
using EeveeCore.Services;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     Brings a player's game data over from their Discord EeveeCore account. The dashboard proves the Discord
///     account by signing the player in with Discord before calling these.
/// </summary>
/// <param name="imports">The import service.</param>
[Route("api/discord-import")]
public class DiscordImportController(DiscordImportService imports) : PokeFluxUserControllerBase
{
    /// <summary>
    ///     Shows what an import from a Discord account would bring over.
    /// </summary>
    /// <param name="discordId">The Discord account the player signed in with.</param>
    [HttpGet("{discordId}")]
    public async Task<IActionResult> Preview(ulong discordId)
    {
        if (!imports.Enabled)
            return Error(StatusCodes.Status404NotFound, "Importing from Discord is not set up.");

        return Ok(await imports.PreviewAsync(discordId, UserId));
    }

    /// <summary>
    ///     Copies the Discord account's data into the player's account. Each account can only take part in one import.
    /// </summary>
    /// <param name="discordId">The Discord account the player signed in with.</param>
    [HttpPost("{discordId}")]
    public async Task<IActionResult> Import(ulong discordId)
    {
        if (!imports.Enabled)
            return Error(StatusCodes.Status404NotFound, "Importing from Discord is not set up.");

        var (success, message, result) = await imports.ImportAsync(discordId, UserId);
        return success
            ? Ok(new DiscordImportResponse(message, result!))
            : Error(StatusCodes.Status400BadRequest, message);
    }
}

/// <summary>
///     The result of an import.
/// </summary>
/// <param name="Message">What happened.</param>
/// <param name="Result">What was copied.</param>
public record DiscordImportResponse(string Message, DiscordImportResult Result);
