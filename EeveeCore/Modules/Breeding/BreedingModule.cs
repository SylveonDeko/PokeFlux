using System.IO;
using System.Text.RegularExpressions;
using EeveeCore.Common.Interactive;
using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Breeding.Services;
using EeveeCore.Modules.Missions.Services;
using EeveeCore.Modules.Pokemon.Services;

namespace EeveeCore.Modules.Breeding;

/// <summary>
///     Commands for Pokémon breeding.
/// </summary>
/// <param name="pokemonService">Finds Pokémon sprites.</param>
/// <param name="missionService">Counts breeds towards missions.</param>
[Group("breeding")]
[Name("Breeding")]
public partial class BreedingModule(PokemonService pokemonService, MissionService missionService)
    : EeveeModule<BreedingService>
{
    private const string Redo = "✅";
    private const string AutoRedo = "🔄";
    private const string CancelAuto = "❎";
    private const string GlitchBadge = "👾";
    private const int MaxRetries = 15;
    private const int FemalesPerPage = 5;

    /// <summary>
    ///     Clears the breeding list of female numbers.
    /// </summary>
    [Command("clear")]
    [Summary("Clears your breeding list of female IDs.")]
    public async Task ClearBreedingList()
    {
        await Service.ClearUserFemalesAsync(ctx.User.Id);
        await ConfirmAsync("Cleared your female list.");
    }

    /// <summary>
    ///     Sets the breeding list of female numbers.
    /// </summary>
    /// <param name="femaleIds">Female Pokémon numbers, separated by spaces or commas.</param>
    [Command("setfemales")]
    [Summary("Set your female Pokémon IDs. Accepts space or comma-separated values.")]
    public async Task SetFemales([Remainder] string femaleIds)
    {
        var idStrings = femaleIds.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var parsedIds = new List<ulong>();
        var invalidIds = new List<string>();

        foreach (var idStr in idStrings)
        {
            if (!ulong.TryParse(idStr, out var id))
            {
                invalidIds.Add(idStr);
                continue;
            }

            parsedIds.Add(id);
        }

        var validationResults = await Service.ValidateFemaleBatchAsync(ctx.User.Id, parsedIds);

        var validatedZeroBasedIds = new List<ulong>();
        var validatedOriginalIds = new List<ulong>();

        foreach (var id in parsedIds)
        {
            if (validationResults.GetValueOrDefault(id, false))
            {
                validatedZeroBasedIds.Add(id - 1);
                validatedOriginalIds.Add(id);
            }
            else
            {
                invalidIds.Add(id.ToString());
            }
        }

        if (validatedZeroBasedIds.Count > 0)
            await Service.UpdateUserFemalesAsync(ctx.User.Id, validatedZeroBasedIds);

        var responseMessage =
            $"Your female Pokémon list has been updated with valid IDs: {string.Join(", ", validatedOriginalIds)}";

        if (invalidIds.Count > 0)
            responseMessage += $"\nInvalid or non-female IDs detected: {string.Join(", ", invalidIds)}.";

        await ConfirmAsync(responseMessage);

        if (validatedOriginalIds.Count > 0)
            await ShowFemaleBreedingStats(validatedOriginalIds, 1);
    }

    /// <summary>
    ///     Shows breeding stats for the females in the breeding list.
    /// </summary>
    /// <param name="page">The page to show first.</param>
    [Command("showfemales")]
    [Summary("Show detailed breeding stats for your selected female Pokémon.")]
    public async Task ShowFemales(int page = 1)
    {
        var females = await Service.GetUserFemalesAsync(ctx.User.Id);
        if (females.Count == 0)
        {
            await ErrorAsync(
                $"You have no female Pokémon set for breeding. Use `{await GetPrefixAsync()}breeding setfemales` to set your breeding list first.");
            return;
        }

        await ShowFemaleBreedingStats(females, page);
    }

    /// <summary>
    ///     Breeds a male with the first female in the breeding list.
    /// </summary>
    /// <param name="maleId">The male Pokémon's number.</param>
    [Command("breed")]
    [Summary("Breed a male Pokémon with a female from your breeding list.")]
    public async Task Breed(string maleId)
    {
        if (!int.TryParse(maleId, out var parsedMaleId) || parsedMaleId < 1)
        {
            await ErrorAsync("Invalid male Pokémon ID provided.");
            return;
        }

        var message = await SendAsync(embed: ProgressEmbed());
        await BreedPokemon((ulong)parsedMaleId, message);
    }

    /// <summary>
    ///     Stops an auto-breed.
    /// </summary>
    [Command("cancel")]
    [Summary("Stop automatically retrying a breed.")]
    public async Task CancelAutoBreed()
    {
        if (Service.GetAutoBreedState(ctx.User.Id) is null)
        {
            await ErrorAsync("You do not have an active auto-breed.");
            return;
        }

        Service.SetAutoBreedState(ctx.User.Id, null);
        await ConfirmAsync("I will no longer automatically attempt to breed these pokes.");
    }

    private async Task BreedPokemon(ulong male, Message message)
    {
        var auto = false;
        while (true)
        {
            var femaleId = await Service.FetchFirstFemaleAsync(ctx.User.Id);
            if (femaleId == null)
            {
                await message.ModifyAsync("", [
                    new EmbedBuilder().WithErrorColor().WithDescription(
                            $"You must set your list of female pokemon ID's with `{await GetPrefixAsync()}breeding setfemales` before using this command!")
                        .Build()
                ]);
                return;
            }

            var result = await Service.AttemptBreedAsync(ctx.User.Id, male, femaleId.Value);
            if (result.Success)
            {
                await ShowSuccessAsync(male, femaleId.Value, result, message);
                return;
            }

            result.ErrorMessage = await WithPrefixAsync(result.ErrorMessage);

            if (!auto)
            {
                var embed = new EmbedBuilder()
                    .WithTitle("Breeding Attempt Failed!")
                    .WithDescription(
                        $"{result.ErrorMessage}\nYou can breed again: <t:{DateTimeOffset.UtcNow.AddSeconds(50).ToUnixTimeSeconds()}:R>\n\n" +
                        $"React {Redo} to redo the breed, or {AutoRedo} to redo it until it succeeds.")
                    .WithColor(Color.Red);
                if (result.Chance > 0) embed.WithFooter($"Chance of success: {result.Chance * 100:F2}%");
                await message.ModifyAsync("", [embed.Build()]);

                var pick = await Reactions.ChooseAsync(message, ctx.User.Id, [Redo, AutoRedo]);
                if (pick is null)
                    return;

                if (pick == 0)
                {
                    await message.ModifyAsync("", [ProgressEmbed().Build()]);
                    continue;
                }

                if (Service.GetAutoBreedState(ctx.User.Id) != null)
                {
                    await ErrorAsync(
                        $"You already have an active auto-breed. Cancel that one first with `{await GetPrefixAsync()}breeding cancel`!");
                    return;
                }

                await ConfirmAsync("I will attempt to breed these pokes until the breed is successful!");
                Service.SetAutoBreedState(ctx.User.Id, (int)male);
                auto = true;
                await ReactionService.AddReactionsAsync(message, [CancelAuto]);

                if (await WaitOrCancelAsync(male, message, TimeSpan.FromSeconds(37)))
                    return;
                continue;
            }

            if (Service.GetAutoBreedState(ctx.User.Id) != (int)male)
                return;

            var retryCount = Service.GetBreedRetries(ctx.User.Id, (int)male);
            if (retryCount >= MaxRetries)
            {
                var limitEmbed = new EmbedBuilder()
                    .WithTitle("Auto-breed retry limit reached!!")
                    .WithDescription(
                        $"Please use the breed command to try again.\n`Auto-retry attempts:` **{retryCount}**\n`(max {MaxRetries})`")
                    .WithColor(Color.Red);
                if (result.Chance > 0) limitEmbed.WithFooter($"Chance of success: {result.Chance * 100:F2}%");

                await message.ModifyAsync("", [limitEmbed.Build()]);
                await ReactionService.TryClearReactionsAsync(message);
                await SendAsync(ctx.User.Mention);

                Service.ResetBreedRetries(ctx.User.Id, (int)male);
                Service.SetAutoBreedState(ctx.User.Id, null);
                return;
            }

            var newRetryCount = Service.IncrementBreedRetries(ctx.User.Id, (int)male);
            var wait = TimeSpan.FromMilliseconds(500);
            var waiting = "";
            if (result.ErrorMessage.Contains("Command on cooldown for"))
            {
                var cooldownSeconds = ExtractCooldownTime(result.ErrorMessage);
                wait = cooldownSeconds > 0 ? TimeSpan.FromSeconds(cooldownSeconds + 1) : TimeSpan.FromSeconds(36);
                if (cooldownSeconds > 0)
                    waiting = "\n\nWaiting for cooldown...";
            }

            var retryEmbed = new EmbedBuilder()
                .WithTitle("Breeding Attempt Failed!")
                .WithDescription(
                    $"{result.ErrorMessage}{waiting}\n\n`Auto-retry attempts:` **{newRetryCount}**\n`(max {MaxRetries})`\n\n" +
                    $"React {CancelAuto} to cancel auto breed.")
                .WithColor(Color.Red);
            if (result.Chance > 0) retryEmbed.WithFooter($"Chance of success: {result.Chance * 100:F2}%");
            await message.ModifyAsync("", [retryEmbed.Build()]);

            if (await WaitOrCancelAsync(male, message, wait))
                return;
        }
    }

    private async Task<bool> WaitOrCancelAsync(ulong male, Message message, TimeSpan wait)
    {
        var pressed = await Reactions.WaitForPressAsync(message.Id, ctx.User.Id, [CancelAuto], wait);
        if (pressed is null && Service.GetAutoBreedState(ctx.User.Id) == (int)male)
            return false;

        await ReactionService.TryClearReactionsAsync(message);
        if (pressed is not null)
        {
            Service.SetAutoBreedState(ctx.User.Id, null);
            await ConfirmAsync("I will no longer automatically attempt to breed these pokes.");
        }

        return true;
    }

    private async Task ShowSuccessAsync(ulong male, ulong femaleId, BreedingService.BreedingResult result,
        Message message)
    {
        Service.SetAutoBreedState(ctx.User.Id, null);

        var parentNames = await Service.GetParentNamesAsync(ctx.User.Id, male, femaleId);
        var imageData = await Service.CreateSuccessImageAsync(result, parentNames.FatherName, parentNames.MotherName);

        var successEmbed = new EmbedBuilder()
            .WithTitle("Success!")
            .WithDescription($"It will hatch after {result.Counter} *counted* messages!\n" +
                             $"Your {parentNames.MotherName} will be on breeding cooldown for 6 Hours!\n\n" +
                             $"You can breed again in <t:{DateTimeOffset.UtcNow.AddSeconds(50).ToUnixTimeSeconds()}:R>")
            .WithImageUrl("attachment://image.png")
            .WithFooter($"Chance of success: {result.Chance * 100:F2}%");

        if (result.IsShadow)
            successEmbed.WithColor(0x4f0fff);
        else if (result.IsShiny)
            successEmbed.WithColor(0xffeb0f);
        else
            successEmbed.WithColor(0x0fff13);

        var tempPokemon = new Database.Linq.Models.Pokemon.Pokemon
        {
            PokemonName = result.Child.Name,
            HpIv = result.Child.Hp,
            AttackIv = result.Child.Attack,
            DefenseIv = result.Child.Defense,
            SpecialAttackIv = result.Child.SpAtk,
            SpecialDefenseIv = result.Child.SpDef,
            SpeedIv = result.Child.Speed,
            Level = result.Child.Level,
            Shiny = result.Child.Shiny,
            Nature = result.Child.Nature
        };
        var context = ctx;
        _ = Task.Run(() => missionService.FirePokemonBredEvent(context, tempPokemon, result.IsShadow));

        if (!result.Child.Name.Equals("ditto", StringComparison.OrdinalIgnoreCase))
            await Service.RemoveFirstFemaleAsync(ctx.User.Id);

        await ReactionService.TryClearReactionsAsync(message);
        await message.ModifyAsync(ctx.User.Mention, [successEmbed.Build()],
            attachments: [new AttachmentRequest(new MemoryStream(imageData), "image.png")]);

        Service.ResetBreedRetries(ctx.User.Id, (int)male);
    }

    private async Task ShowFemaleBreedingStats(List<ulong> femalePositions, int page)
    {
        var femalePokemons = await Service.GetBreedingFemalesBatch(ctx.User.Id, femalePositions);
        if (femalePokemons.Count == 0)
        {
            await ErrorAsync("No valid female Pokémon found in your breeding list.");
            return;
        }

        var totalPages = (femalePokemons.Count - 1) / FemalesPerPage + 1;

        await Reactions.PaginateAsync(ctx.Channel, ctx.User.Id, totalPages, async index =>
        {
            var embeds = new List<EmbedRequest>();
            var attachments = new List<AttachmentRequest>();

            foreach (var (pokemon, position) in femalePokemons.Skip(index * FemalesPerPage).Take(FemalesPerPage))
            {
                var favorite = pokemon.Favorite ? "⭐ " : "";
                var champion = pokemon.Champion ? "🏆 " : "";
                var ivTotal = pokemon.HpIv + pokemon.AttackIv + pokemon.DefenseIv +
                              pokemon.SpecialAttackIv + pokemon.SpecialDefenseIv + pokemon.SpeedIv;
                var ivPercentage = ivTotal / 186.0 * 100;

                var description = $"**Position** #{position} | **Level** {pokemon.Level}\n" +
                                  $"**IV Total** {ivTotal}/186 ({ivPercentage:F2}%) | **Nature** {pokemon.Nature}\n" +
                                  "**Breeding Stats:**\n" +
                                  $"• HP: {pokemon.HpIv}/31 • ATK: {pokemon.AttackIv}/31 • DEF: {pokemon.DefenseIv}/31\n" +
                                  $"• SP.ATK: {pokemon.SpecialAttackIv}/31 • SP.DEF: {pokemon.SpecialDefenseIv}/31 • SPD: {pokemon.SpeedIv}/31\n" +
                                  $"**Held Item** {pokemon.HeldItem ?? "None"}";

                if (!string.IsNullOrEmpty(pokemon.Nickname) && pokemon.Nickname != pokemon.PokemonName)
                    description += $"\n**Nickname** {pokemon.Nickname}";

                var breedingFlags = new List<string>();
                if (!pokemon.Breedable) breedingFlags.Add("Not Breedable");
                if (pokemon.PokemonName.Equals("ditto", StringComparison.OrdinalIgnoreCase))
                    breedingFlags.Add("Ditto (Universal Breeder)");
                if (breedingFlags.Count > 0)
                    description += $"\n**Status** {string.Join(", ", breedingFlags)}";

                var embed = new EmbedBuilder()
                    .WithTitle(
                        $"{Badge(pokemon.Shiny, pokemon.Radiant, pokemon.Skin)}{favorite}{champion}{pokemon.PokemonName.Capitalize()} {GenderEmoji(pokemon.Gender)}"
                            .Trim())
                    .WithDescription(description)
                    .WithColor(Color.Purple);

                var (_, imagePath) = await pokemonService.GetPokemonFormInfo(pokemon.PokemonName,
                    pokemon.Shiny == true, pokemon.Radiant == true, pokemon.Skin ?? "");
                SetImage(embed, imagePath, $"pokemon_{attachments.Count}.png", attachments, true);

                embeds.Add(embed.Build());
            }

            return new Page(
                $"**Your Breeding Females** • Page {index + 1}/{totalPages} • {femalePokemons.Count} Breeding Females",
                embeds, attachments);
        }, TimeSpan.FromMinutes(10), page - 1);
    }

    private static EmbedBuilder ProgressEmbed() =>
        new EmbedBuilder().WithOkColor().WithDescription("Breeding in progress...");

    private static int ExtractCooldownTime(string errorMessage)
    {
        var match = CooldownRegex().Match(errorMessage);
        return match.Success && int.TryParse(match.Groups[1].Value, out var seconds) ? seconds : 0;
    }

    private static string Badge(bool? shiny, bool? radiant, string? skin)
    {
        if (radiant == true) return Emojis.Radiant + " ";
        if (shiny == true) return Emojis.Shiny + " ";
        return skin switch
        {
            "glitch" => GlitchBadge + " ",
            "shadow" => Emojis.Shadow + " ",
            _ => ""
        };
    }

    private static string GenderEmoji(string? gender) =>
        gender?.ToLower() switch
        {
            "-m" or "male" => "♂️",
            "-f" or "female" => "♀️",
            _ => ""
        };

    private async Task<string> WithPrefixAsync(string text) => text.Replace("`/", $"`{await GetPrefixAsync()}");

    [GeneratedRegex(@"cooldown for (\d+)s")]
    private static partial Regex CooldownRegex();
}
