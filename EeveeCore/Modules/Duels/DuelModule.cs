using System.Text.Json;
using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Duels.Impl;
using EeveeCore.Modules.Duels.Impl.DuelPokemon;
using EeveeCore.Modules.Duels.Services;
using EeveeCore.Modules.Missions.Services;
using EeveeCore.Modules.Spawn.Constants;
using EeveeCore.Services.Impl;
using MongoDB.Driver;
using LinqToDB;
using LinqToDB.Async;
using Serilog;

namespace EeveeCore.Modules.Duels;

/// <summary>
///     Commands for starting Pokémon battles: challenges, single, party, inverse and NPC duels.
///     Handles cooldowns, the challenge prompt, running the battle and its rewards.
/// </summary>
/// <param name="mongoService">The MongoDB service for accessing Pokémon data.</param>
/// <param name="db">The database connection provider.</param>
/// <param name="redis">The Redis cache for cooldowns.</param>
/// <param name="duelReactions">The reaction prompts players pick battle actions with.</param>
/// <param name="renderer">The renderer for battle images.</param>
/// <param name="missionService">The mission service for duel mission progress.</param>
/// <param name="botLog">The bot event log, for duel errors.</param>
[Name("Duels")]
[Group("duel")]
[RequireContext(ContextType.Guild)]
public class DuelModule(
    IMongoService mongoService,
    LinqToDbConnectionProvider db,
    RedisCache redis,
    DuelReactions duelReactions,
    DuelRenderer renderer,
    MissionService missionService,
    BotLog botLog) : EeveeModule<DuelService>
{
    private const string DateFormat = "MM/dd/yyyy, HH:mm:ss";

    private static readonly string[] PregameGifs =
    [
        Path.Combine("data", "images", "duel1.gif"),
        Path.Combine("data", "images", "duel2.gif"),
        Path.Combine("data", "images", "duel3.gif"),
        Path.Combine("data", "images", "duel4.gif")
    ];

    private enum DuelKind
    {
        Single,
        Party,
        Inverse
    }

    /// <summary>
    ///     Challenges another user to a 6v6 party battle, normal or inverse.
    /// </summary>
    /// <param name="opponent">The user to challenge.</param>
    /// <param name="battleType">normal or inverse.</param>
    [Command("challenge")]
    [Summary("Challenge another user to a Pokémon battle. Battle type is normal or inverse.")]
    public async Task ChallengeBattle(User opponent, string battleType = "normal")
    {
        battleType = battleType.ToLowerInvariant();
        if (battleType is not ("normal" or "inverse"))
        {
            await ErrorAsync("The battle type must be `normal` or `inverse`.");
            return;
        }

        if (opponent.IsBot)
        {
            await ErrorAsync("You cannot challenge a bot to a battle!");
            return;
        }

        if (opponent.Id == ctx.User.Id)
        {
            await ErrorAsync("You cannot challenge yourself to a battle!");
            return;
        }

        await ChallengeAsync(opponent, battleType == "inverse" ? DuelKind.Inverse : DuelKind.Party,
            $"{opponent.Mention} You have been challenged to a {battleType} battle by {ctx.User.Username}!");
    }

    /// <summary>
    ///     Challenges another user to a 1v1 duel with each player's selected Pokémon.
    /// </summary>
    /// <param name="opponent">The user to duel.</param>
    [Command("single")]
    [Summary("1v1 duel with another user's selected pokemon.")]
    public async Task SingleDuel(User opponent)
    {
        if (!await CheckOpponent(opponent) || !await CheckDuelCooldowns(ctx.User.Id))
            return;

        await ChallengeAsync(opponent, DuelKind.Single,
            $"{opponent.Mention} You have been challenged to a 1v1 duel by {ctx.User.Username}!");
    }

    /// <summary>
    ///     Challenges another user to a 6v6 duel with each player's party.
    /// </summary>
    /// <param name="opponent">The user to duel.</param>
    [Command("party")]
    [Summary("6v6 duel with another user's selected party.")]
    public async Task PartyDuel(User opponent)
    {
        if (!await CheckOpponent(opponent) || !await CheckDuelCooldowns(ctx.User.Id))
            return;

        await ChallengeAsync(opponent, DuelKind.Party,
            $"{opponent.Mention} You have been challenged to a 6v6 party duel by {ctx.User.Username}!");
    }

    /// <summary>
    ///     Challenges another user to a 6v6 inverse battle, where type effectiveness is reversed.
    /// </summary>
    /// <param name="opponent">The user to duel.</param>
    [Command("inverse")]
    [Summary("6v6 inverse battle with another user's selected party.")]
    public async Task InverseDuel(User opponent)
    {
        if (!await CheckOpponent(opponent) || !await CheckDuelCooldowns(ctx.User.Id))
            return;

        await ChallengeAsync(opponent, DuelKind.Inverse,
            $"{opponent.Mention} You have been challenged to a 6v6 inverse battle by {ctx.User.Username}!");
    }

    /// <summary>
    ///     Starts a 1v1 duel against an NPC trainer. Costs one energy and gives rewards for a win.
    /// </summary>
    [Command("npc")]
    [Summary("1v1 duel with an NPC AI.")]
    public async Task NpcDuel()
    {
        if (!await CheckNpcDuelCooldowns(ctx.User.Id))
            return;

        try
        {
            if (await Service.IsUserInBattle(ctx.User.Id))
            {
                await ErrorAsync("You are already in a battle! Please finish your current battle first.");
                return;
            }

            var prefix = await GetPrefixAsync();
            await using var conn = await db.GetConnectionAsync();

            var userData = await conn.Users.FirstOrDefaultAsync(u => u.UserId == ctx.User.Id);
            if (userData == null)
            {
                await ErrorAsync($"You have not Started! Start with `{prefix}start` first!");
                return;
            }

            var selectedPokemonId = userData.Selected ?? 0;
            if (selectedPokemonId == 0)
            {
                await ErrorAsync($"You have not selected a Pokemon! Select one with `{prefix}select <id>` first!");
                return;
            }

            var selectedPokemon = await conn.UserPokemon.FirstOrDefaultAsync(p => p.Id == selectedPokemonId);
            if (selectedPokemon == null)
            {
                await ErrorAsync("Failed to find your selected Pokemon!");
                return;
            }

            if (selectedPokemon.PokemonName.Equals("Egg", StringComparison.OrdinalIgnoreCase))
            {
                await ErrorAsync(
                    $"You have an egg selected! Select a different pokemon with `{prefix}select <id>` first!");
                return;
            }

            if (userData.Energy <= 0)
            {
                await ErrorAsync("You don't have any energy left!");
                return;
            }

            var npcPokemonList = await conn.UserPokemon
                .Where(p => p.Owner != ctx.User.Id &&
                            p.Level >= selectedPokemon.Level - 10 &&
                            p.Level <= selectedPokemon.Level + 10 &&
                            p.AttackIv <= 31 &&
                            p.DefenseIv <= 31 &&
                            p.SpecialAttackIv <= 31 &&
                            p.SpecialDefenseIv <= 31 &&
                            p.SpeedIv <= 31 &&
                            p.HpIv <= 31)
                .OrderByDescending(p => p.Id)
                .Take(1000)
                .ToListAsync();

            npcPokemonList = npcPokemonList
                .Where(p => p.Moves == null ||
                            !p.Moves.Any(m => m != null && m.Equals("tackle", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var npcPokemon = npcPokemonList.Count > 0
                ? npcPokemonList[Random.Shared.Next(npcPokemonList.Count)]
                : await GenerateNpcPokemonAsync(selectedPokemon.Level);
            if (npcPokemon is null)
            {
                await ErrorAsync("Could not find a suitable NPC Pokemon for battle. Please try again later.");
                return;
            }

            await conn.Users
                .Where(u => u.UserId == ctx.User.Id)
                .Set(u => u.Energy, u => u.Energy - 1)
                .UpdateAsync();

            await SendLoadingAsync("Pokemon Battle loading...", "Preparing your battle against an NPC trainer!");

            var playerPokemon = await DuelPokemon.Create(selectedPokemon, mongoService);
            var npcPokemonDuel = await DuelPokemon.Create(npcPokemon, mongoService);

            var playerTrainer = new MemberTrainer(ctx.User.Id, ctx.User.Username, [playerPokemon]);
            var npcTrainer = new NPCTrainer([npcPokemonDuel]);

            var battle = new Battle(duelReactions, ctx.Channel, playerTrainer, npcTrainer, mongoService);
            var battleId = ctx.Message.Id;
            await Service.RegisterBattle(ctx.User.Id, 0, battle, battleId);

            _ = Task.Run(async () =>
            {
                try
                {
                    var winner = await RunBattle(battle, battleId);
                    if (winner == playerTrainer)
                    {
                        _ = Task.Run(() => missionService.FireNpcBattleCompletedEvent(ctx, winner, battle));
                        await HandleNpcRewards(ctx.User.Id, winner, battle, userData.Inventory);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error running NPC battle");
                    await Service.EndBattle(battle);
                }
            });
        }
        catch (Exception ex)
        {
            await ErrorAsync($"An error occurred: {ex.Message}");
            Log.Error(ex, "Error in NPC duel");
        }
    }

    private async Task<bool> CheckOpponent(User opponent)
    {
        if (opponent.IsBot)
        {
            await ErrorAsync("You cannot duel a bot!");
            return false;
        }

        if (opponent.Id == ctx.User.Id)
        {
            await ErrorAsync("You cannot duel yourself!");
            return false;
        }

        return true;
    }

    private async Task ChallengeAsync(User opponent, DuelKind kind, string text)
    {
        var challenge = await SendAsync(text);
        var answer = await Reactions.ConfirmAsync(challenge, opponent.Id);

        if (answer != true)
        {
            await challenge.ModifyAsync(answer == false
                ? $"{opponent.Username} rejected the battle challenge."
                : $"{opponent.Username} did not answer the battle challenge in time.");
            return;
        }

        try
        {
            if (await Service.IsUserInBattle(ctx.User.Id))
            {
                await ErrorAsync($"{ctx.User.Username} is already in a battle!");
                return;
            }

            if (await Service.IsUserInBattle(opponent.Id))
            {
                await ErrorAsync($"{opponent.Username} is already in a battle!");
                return;
            }

            await SendLoadingAsync("Pokemon Battle accepted! Loading...", "Please wait");

            if (kind == DuelKind.Single)
                await StartSingleBattle(opponent, challenge);
            else
                await StartPartyBattle(opponent, kind == DuelKind.Inverse, challenge);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error accepting duel");
            await ErrorAsync("An error occurred accepting the duel. Please try again.");
        }
    }

    private async Task<Database.Linq.Models.Pokemon.Pokemon?> GenerateNpcPokemonAsync(int level)
    {
        var natures = await mongoService.Natures.Find(_ => true).ToListAsync();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var name = PokemonList.pList[Random.Shared.Next(PokemonList.pList.Count)]?.ToLowerInvariant();
            if (name is null)
                continue;

            var learnset = await mongoService.PokemonMoves.Find(p => p.Pokemon == name).FirstOrDefaultAsync();
            if (learnset?.Moves is not { Count: > 0 } moves)
                continue;

            return new Database.Linq.Models.Pokemon.Pokemon
            {
                PokemonName = name.Capitalize(),
                Level = level,
                Moves = moves.OrderBy(_ => Random.Shared.Next()).Take(4).ToArray(),
                Nature = natures.Count > 0 ? natures[Random.Shared.Next(natures.Count)].Identifier : "hardy",
                Gender = Random.Shared.Next(2) == 0 ? "-m" : "-f",
                HeldItem = "None",
                Happiness = 70,
                HpIv = Random.Shared.Next(32),
                AttackIv = Random.Shared.Next(32),
                DefenseIv = Random.Shared.Next(32),
                SpecialAttackIv = Random.Shared.Next(32),
                SpecialDefenseIv = Random.Shared.Next(32),
                SpeedIv = Random.Shared.Next(32)
            };
        }

        return null;
    }

    private async Task SendLoadingAsync(string title, string description)
    {
        var gifPath = PregameGifs[Random.Shared.Next(PregameGifs.Length)];
        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(description)
            .WithColor(new Color(255, 182, 193));

        var attachments = new List<AttachmentRequest>();
        SetImage(embed, gifPath, "duel.gif", attachments);
        await SendAsync(embed: embed, attachments: attachments);
    }

    private async Task StartSingleBattle(User opponent, Message challenge)
    {
        var prefix = await GetPrefixAsync();
        await using var conn = await db.GetConnectionAsync();

        var challengerSelected = await conn.Users
            .Where(u => u.UserId == ctx.User.Id)
            .Select(u => u.Selected)
            .FirstOrDefaultAsync() ?? 0;

        if (challengerSelected == 0)
        {
            await ErrorAsync(
                $"{ctx.User.Username} has not selected a Pokemon! Select one with `{prefix}select <id>` first!");
            return;
        }

        var challengerPoke = await conn.UserPokemon.FirstOrDefaultAsync(p => p.Id == challengerSelected);
        if (challengerPoke == null || challengerPoke.PokemonName.Equals("Egg", StringComparison.OrdinalIgnoreCase))
        {
            await ErrorAsync($"{ctx.User.Username} has no valid Pokemon selected!");
            return;
        }

        var opponentSelected = await conn.Users
            .Where(u => u.UserId == opponent.Id)
            .Select(u => u.Selected)
            .FirstOrDefaultAsync() ?? 0;

        if (opponentSelected == 0)
        {
            await ErrorAsync(
                $"{opponent.Username} has not selected a Pokemon! Select one with `{prefix}select <id>` first!");
            return;
        }

        var opponentPoke = await conn.UserPokemon.FirstOrDefaultAsync(p => p.Id == opponentSelected);
        if (opponentPoke == null || opponentPoke.PokemonName.Equals("Egg", StringComparison.OrdinalIgnoreCase))
        {
            await ErrorAsync($"{opponent.Username} has no valid Pokemon selected!");
            return;
        }

        var challengerDuelPoke = await DuelPokemon.Create(challengerPoke, mongoService);
        var opponentDuelPoke = await DuelPokemon.Create(opponentPoke, mongoService);

        var trainer1 = new MemberTrainer(ctx.User.Id, ctx.User.Username, [challengerDuelPoke]);
        var trainer2 = new MemberTrainer(opponent.Id, opponent.Username, [opponentDuelPoke]);

        var battle = new Battle(duelReactions, ctx.Channel, trainer1, trainer2, mongoService);
        var battleId = ctx.Message.Id;
        await Service.RegisterBattle(ctx.User.Id, opponent.Id, battle, battleId);

        await challenge.ModifyAsync($"Battle between {trainer1.Name} and {trainer2.Name} has begun!");

        _ = Task.Run(() => RunBattle(battle, battleId));
    }

    private async Task StartPartyBattle(User opponent, bool inverseBattle, Message challenge)
    {
        var challengerPokemon = await Service.GetUserPokemonParty(ctx.User.Id, ctx.User.Username);
        var opponentPokemon = await Service.GetUserPokemonParty(opponent.Id, opponent.Username);

        if (challengerPokemon.Count == 0)
        {
            await ErrorAsync($"{ctx.User.Username} doesn't have any Pokemon in their party!");
            return;
        }

        if (opponentPokemon.Count == 0)
        {
            await ErrorAsync($"{opponent.Username} doesn't have any Pokemon in their party!");
            return;
        }

        var trainer1 = new MemberTrainer(ctx.User.Id, ctx.User.Username, challengerPokemon);
        var trainer2 = new MemberTrainer(opponent.Id, opponent.Username, opponentPokemon);

        var battle = new Battle(duelReactions, ctx.Channel, trainer1, trainer2, mongoService, inverseBattle);
        var battleId = ctx.Message.Id;
        await Service.RegisterBattle(ctx.User.Id, opponent.Id, battle, battleId);

        await challenge.ModifyAsync($"Battle between {trainer1.Name} and {trainer2.Name} has begun!");

        trainer1.Event = new TaskCompletionSource<bool>();
        trainer2.Event = new TaskCompletionSource<bool>();

        await renderer.GenerateTeamPreview(battle);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(trainer1.Event.Task, trainer2.Event.Task);
                await RunBattle(battle, battleId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error waiting for lead Pokémon selection");
                await battle.Channel.SendMessageAsync("An error occurred starting the battle. Please try again.");
                await Service.EndBattle(battle);
            }
        });
    }

    private async Task<Trainer?> RunBattle(Battle battle, ulong battleId)
    {
        Trainer? winner = null;

        try
        {
            winner = await battle.Run();
        }
        catch (HttpRequestException)
        {
            await battle.Channel.SendMessageAsync(
                "The bot encountered an unexpected network issue, " +
                "and the duel could not continue. " +
                "Please try again in a few moments.\n" +
                "Note: Do not report this as a bug.");
        }
        catch (TimeoutException)
        {
            await battle.Channel.SendMessageAsync(
                "The battle timed out. " +
                "Please try again in a few moments.\n" +
                "Note: Do not report this as a bug.");
        }
        catch (Exception e)
        {
            await battle.Channel.SendMessageAsync(
                "`The duel encountered an error.`\n" +
                $"Your error code is **`{battleId}`**.\n" +
                "Please post this code in the support channel with " +
                "details about what was happening when this error occurred.");

            foreach (var page in PaginateErrorMessage(e.ToString(), battleId))
                await botLog.SendAsync($"```csharp\n{page}\n```");

            Log.Error(e, "Duels encountered an error.");
        }

        if (winner is MemberTrainer memWinner && battle is { Trainer1: MemberTrainer t1, Trainer2: MemberTrainer t2 })
            try
            {
                _ = Task.Run(() => missionService.FireDuelCompletedEvent(ctx, battle, memWinner));

                var description = "";
                await using var conn = await db.GetConnectionAsync();

                await conn.Achievements.Where(a => a.UserId == memWinner.Id)
                    .Set(a => a.DuelPartyWins, a => a.DuelPartyWins + 1)
                    .UpdateAsync();

                await conn.Achievements.Where(a => a.UserId == t1.Id || a.UserId == t2.Id)
                    .Set(a => a.DuelsTotal, a => a.DuelsTotal + 1)
                    .UpdateAsync();

                description += await GrantWinnerExp(conn, winner);

                if (!string.IsNullOrEmpty(description))
                    await battle.Channel.SendMessageAsync(embeds:
                    [
                        new EmbedBuilder()
                            .WithDescription(description)
                            .WithColor(new Color(255, 182, 193))
                            .Build()
                    ]);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing battle rewards");
            }

        await Service.EndBattle(battle, battleId.ToString());
        return winner;
    }

    private static async Task<string> GrantWinnerExp(DittoDataConnection conn, Trainer winner)
    {
        var description = "";
        foreach (var poke in winner.Party.Where(p => p is { Hp: > 0, EverSentOut: true }))
        {
            var pokeData = await conn.UserPokemon.FirstOrDefaultAsync(p => p.Id == poke.Id);
            if (pokeData == null)
                continue;

            var heldItem = pokeData.HeldItem?.ToLower();
            if (heldItem == "xp-block")
                continue;

            var expValue = 150 * poke.Level / 7.0;
            if (heldItem == "lucky-egg")
                expValue *= 2.5;

            var exp = Math.Min((int)expValue, int.MaxValue - pokeData.Experience);
            description += $"{poke.Name} got {exp} exp from winning.\n";

            await conn.UserPokemon
                .Where(p => p.Id == poke.Id)
                .Set(p => p.Happiness, p => p.Happiness + 1)
                .Set(p => p.Experience, p => p.Experience + exp)
                .UpdateAsync();
        }

        return description;
    }

    private async Task HandleNpcRewards(ulong userId, Trainer winner, Battle battle, string? inventory)
    {
        try
        {
            await using var conn = await db.GetConnectionAsync();

            var battleMulti = 1m;
            try
            {
                if (!string.IsNullOrEmpty(inventory))
                {
                    using var inventoryJson = JsonDocument.Parse(inventory);
                    if (inventoryJson.RootElement.TryGetProperty("battle-multiplier", out var element))
                    {
                        if (element.ValueKind == JsonValueKind.Number)
                            battleMulti = element.GetDecimal();
                        else if (element.ValueKind == JsonValueKind.String &&
                                 decimal.TryParse(element.GetString(), out var parsed))
                            battleMulti = parsed;
                    }
                }

                battleMulti = Math.Min(battleMulti, 50m);
            }
            catch
            {
                battleMulti = 1;
            }

            var creds = (int)(Random.Shared.Next(100, 600) * battleMulti);
            var desc = $"You received {creds} credits for winning the duel!\n\n";

            await conn.Achievements
                .Where(a => a.UserId == userId)
                .Set(a => a.DuelsTotal, a => a.DuelsTotal + 1)
                .Set(a => a.NpcWins, a => a.NpcWins + 1)
                .UpdateAsync();

            await conn.Users
                .Where(u => u.UserId == userId)
                .Set(u => u.MewCoins, u => u.MewCoins + Convert.ToUInt64(creds))
                .UpdateAsync();

            desc += await GrantWinnerExp(conn, winner);

            await battle.Channel.SendMessageAsync(embeds:
            [
                new EmbedBuilder()
                    .WithDescription(desc)
                    .WithColor(new Color(255, 182, 193))
                    .Build()
            ]);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling NPC battle rewards");
        }
    }

    private static List<string> PaginateErrorMessage(string message, ulong errorId)
    {
        const int maxLength = 1900;
        var full = $"Exception ID {errorId}\n\n{message}";
        var parts = new List<string>();
        for (var i = 0; i < full.Length; i += maxLength)
            parts.Add(full.Substring(i, Math.Min(maxLength, full.Length - i)));
        return parts;
    }

    private async Task<bool> CheckChannelPermissions()
    {
        var perms = ctx.Guild?.CurrentMember?.GetPermissions(ctx.Channel);
        if (perms is { SendMessages: true, EmbedLinks: true, AttachFiles: true, AddReactions: true })
            return true;

        await ErrorAsync(
            "I need `send_messages`, `embed_links`, `attach_files` and `add_reactions` permissions in order to let you duel!");
        return false;
    }

    private async Task<bool> CheckDuelCooldowns(ulong userId)
    {
        if (!await CheckChannelPermissions())
            return false;

        try
        {
            var cache = redis.Redis.GetDatabase();
            var duelReset = await cache.StringGetAsync($"duelcooldowns:{userId}");
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (duelReset.HasValue && double.Parse(duelReset!) > currentTime)
            {
                var resetInSeconds = (int)(double.Parse(duelReset!) - currentTime);
                await ErrorAsync($"Command on cooldown for {resetInSeconds}s");
                return false;
            }

            await cache.StringSetAsync($"duelcooldowns:{userId}", (currentTime + 20).ToString());

            var duelResetTime = await cache.StringGetAsync("duelcooldownreset");
            if (!duelResetTime.HasValue)
            {
                await cache.StringSetAsync("duelcooldownreset", DateTime.UtcNow.ToString(DateFormat));
                await cache.HashSetAsync("dailyduelcooldowns", userId.ToString(), "0");
            }
            else if (DateTime.UtcNow > DateTime.ParseExact(duelResetTime.ToString(), DateFormat, null).AddDays(1))
            {
                await cache.StringSetAsync("duelcooldownreset", DateTime.UtcNow.ToString(DateFormat));
                await cache.KeyDeleteAsync("dailyduelcooldowns");
                await cache.HashSetAsync("dailyduelcooldowns", userId.ToString(), "0");
            }

            var usedCount = await cache.HashGetAsync("dailyduelcooldowns", userId.ToString());
            var used = usedCount.HasValue ? int.Parse(usedCount.ToString()) : 0;

            if (used >= 50)
            {
                await ErrorAsync("You have hit the maximum number of duels per day!");
                return false;
            }

            await cache.HashSetAsync("dailyduelcooldowns", userId.ToString(), (used + 1).ToString());
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking duel cooldowns");
            await ErrorAsync("An error occurred checking cooldowns. Please try again later.");
            return false;
        }
    }

    private async Task<bool> CheckNpcDuelCooldowns(ulong userId)
    {
        if (!await CheckChannelPermissions())
            return false;

        try
        {
            var cache = redis.Redis.GetDatabase();
            var duelReset = await cache.StringGetAsync($"duelcooldowns:{userId}");
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (duelReset.HasValue && double.Parse(duelReset!) > currentTime)
            {
                var resetInSeconds = (int)(double.Parse(duelReset!) - currentTime);
                await ErrorAsync($"Command on cooldown for {resetInSeconds}s");
                return false;
            }

            await cache.StringSetAsync($"duelcooldowns:{userId}", (currentTime + 20).ToString());
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking NPC duel cooldowns");
            await ErrorAsync("An error occurred checking cooldowns. Please try again later.");
            return false;
        }
    }
}
