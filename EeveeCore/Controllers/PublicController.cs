using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     Public totals and leaderboards, which only need the API key.
/// </summary>
/// <param name="client">The Fluxer client, for the server count.</param>
/// <param name="dbProvider">The database connection provider.</param>
[Route("api")]
public class PublicController(FluxerClient client, LinqToDbConnectionProvider dbProvider) : PokeFluxControllerBase
{
    private const int LeaderboardSize = 25;
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, (DateTime At, object Value)> Cache = new();

    /// <summary>
    ///     Gets how many servers, players and Pokémon there are.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats() =>
        Ok(await CachedAsync("stats", async () =>
        {
            await using var db = await dbProvider.GetConnectionAsync();
            return new StatsResponse(client.Gateway.Guilds.Count, await db.Users.LongCountAsync(),
                await db.UserPokemon.LongCountAsync());
        }));

    /// <summary>
    ///     Gets the top 25 players who show their profile, by Pokémon, shinies or credits.
    /// </summary>
    /// <param name="kind">pokemon, shiny or credits.</param>
    [HttpGet("leaderboard")]
    public async Task<IActionResult> Leaderboard(string kind = "pokemon")
    {
        kind = kind.ToLowerInvariant();
        if (kind is not ("pokemon" or "shiny" or "credits"))
            return Error(400, "Unknown leaderboard. Use pokemon, shiny or credits.");

        return Ok(await CachedAsync($"leaderboard:{kind}", async () =>
        {
            await using var db = await dbProvider.GetConnectionAsync();
            var visible = db.Users.Where(u => u.Visible == true);

            var rows = kind switch
            {
                "credits" => await visible
                    .OrderByDescending(u => u.MewCoins)
                    .Take(LeaderboardSize)
                    .Select(u => new { UserId = u.UserId!.Value, Value = (long)(u.MewCoins ?? 0) })
                    .ToListAsync(),
                _ => await (from o in db.UserPokemonOwnerships
                        from p in db.UserPokemon.Where(p => p.Id == o.PokemonId)
                        from u in visible.Where(u => u.UserId == o.UserId)
                        where kind != "shiny" || p.Shiny == true
                        group o by o.UserId
                        into g
                        orderby g.Count() descending
                        select new { UserId = g.Key, Value = (long)g.Count() })
                    .Take(LeaderboardSize)
                    .ToListAsync()
            };

            var ids = rows.Select(r => (ulong?)r.UserId).ToList();
            var names = (await db.Users
                    .Where(u => ids.Contains(u.UserId))
                    .Select(u => new { u.UserId, u.TrainerNickname })
                    .ToListAsync())
                .ToDictionary(u => u.UserId!.Value, u => u.TrainerNickname);

            return new LeaderboardResponse(kind, rows
                .Select((r, i) => new LeaderboardEntry(i + 1, r.UserId,
                    string.IsNullOrWhiteSpace(names.GetValueOrDefault(r.UserId)) ? null : names[r.UserId], r.Value))
                .ToList());
        }));
    }

    private static async Task<object> CachedAsync<T>(string key, Func<Task<T>> load) where T : notnull
    {
        if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CacheTime)
            return cached.Value;

        await CacheLock.WaitAsync();
        try
        {
            if (Cache.TryGetValue(key, out cached) && DateTime.UtcNow - cached.At < CacheTime)
                return cached.Value;

            var value = await load();
            Cache[key] = (DateTime.UtcNow, value);
            return value;
        }
        finally
        {
            CacheLock.Release();
        }
    }
}
