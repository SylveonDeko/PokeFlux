namespace EeveeCore.Controllers.Models;

/// <summary>
///     Bot-wide totals.
/// </summary>
/// <param name="Servers">How many servers the bot is in.</param>
/// <param name="Players">How many players have started.</param>
/// <param name="PokemonCaught">How many Pokémon exist.</param>
public record StatsResponse(int Servers, long Players, long PokemonCaught);

/// <summary>
///     A leaderboard row.
/// </summary>
/// <param name="Rank">The position, from 1.</param>
/// <param name="UserId">The player's ID.</param>
/// <param name="Name">The player's trainer nickname, or null.</param>
/// <param name="Value">The ranked amount.</param>
public record LeaderboardEntry(int Rank, ulong UserId, string? Name, long Value);

/// <summary>
///     A leaderboard.
/// </summary>
/// <param name="Kind">pokemon, shiny or credits.</param>
/// <param name="Entries">The top players.</param>
public record LeaderboardResponse(string Kind, List<LeaderboardEntry> Entries);
