using System.Text;
using EeveeCore.Common.Interactive;
using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Pokemon.Services;
using Humanizer;
using Serilog;
using PokemonModel = EeveeCore.Database.Linq.Models.Pokemon.Pokemon;

namespace EeveeCore.Modules.Pokemon;

/// <summary>
///     Viewing, selecting and managing the Pokémon in a player's collection.
/// </summary>
[Name("Pokemon")]
public partial class PokemonModule : EeveeModule<PokemonService>
{
    private const string InfoToggle = "ℹ️";
    private const string First = "⏮️";
    private const string Back = "◀️";
    private const string Next = "▶️";
    private const string Last = "⏭️";
    private const string Stop = "⏹️";

    private static readonly string[] SortValues =
        ["iv", "level", "name", "recent", "type", "favorite", "party", "champion"];

    private static readonly string[] FilterValues =
        ["all", "shiny", "radiant", "shadow", "legendary", "favorite", "champion", "party", "market"];

    private static readonly string[] GenderValues = ["all", "male", "female", "genderless"];
    private static readonly string[] ViewValues = ["normal", "compact", "detailed"];
    private static readonly string[] PokedexValues = ["national", "unowned", "shadow", "shiny", "radiant", "skin"];

    private static readonly string[] SubcommandWords =
        ["select", "resurrect", "info", "pokedex", "release", "sacrifice", "forms"];

    private static readonly string[] Footers =
    [
        "We are always looking for new help! Art Team, Staff Team, and Dev team-ask in the official server!"
    ];

    private readonly Random random = new();

    /// <summary>
    ///     Selects a Pokémon by its number.
    /// </summary>
    /// <param name="number">The Pokémon's number in the collection.</param>
    [Command("pokemon select")]
    [Alias("select")]
    [Summary("Select a Pokémon by its number.")]
    [RequireContext(ContextType.Guild)]
    public async Task Select(ulong number)
    {
        var result = await Service.SelectPokemon(ctx.User.Id, number);
        if (!result.Success)
        {
            await ErrorAsync(await PrefixedAsync(result.Message));
            return;
        }

        await ConfirmAsync(result.Message);
    }

    /// <summary>
    ///     Shows the player's Pokémon, sorted, filtered and searched by the options given.
    /// </summary>
    /// <param name="options">Sort, filter, gender, view, page and search options.</param>
    [Command("pokemon list")]
    [Alias("pokemon", "p")]
    [Summary("Shows a detailed list of all your obtained Pokémon.\n" +
             "Options, in any order:\n" +
             "`sort:<iv|level|name|recent|type|favorite|party|champion>`\n" +
             "`filter:<all|shiny|radiant|shadow|legendary|favorite|champion|party|market>`\n" +
             "`gender:<all|male|female|genderless>`\n" +
             "`view:<detailed|normal|compact>`\n" +
             "`page:<number>` or a bare number\n" +
             "`search:<text>` or any other words\n" +
             "For example `{prefix}p shiny compact 2`.")]
    [Priority(-1)]
    public async Task List([Remainder] string? options = null)
    {
        var prefix = await GetPrefixAsync();
        var tokens = (options ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0 && tokens[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            tokens = tokens[1..];
        if (tokens.Length > 0 && SubcommandWords.Contains(tokens[0].ToLowerInvariant()))
        {
            await ErrorAsync($"That is not how `{prefix}pokemon {tokens[0].ToLowerInvariant()}` is used. " +
                             $"See `{prefix}help pokemon {tokens[0].ToLowerInvariant()}`.");
            return;
        }

        var (parsed, error) = ParseListOptions(tokens);
        if (parsed is null)
        {
            await ErrorAsync(error!);
            return;
        }

        var sortOrder = parsed.Sort switch
        {
            "iv" => SortOrder.Iv,
            "level" => SortOrder.Level,
            "name" => SortOrder.Name,
            "recent" => SortOrder.Recent,
            "type" => SortOrder.Type,
            "favorite" => SortOrder.Favorite,
            "party" => SortOrder.Party,
            "champion" => SortOrder.Champion,
            _ => SortOrder.Default
        };

        var (filteredList, stats, partyLookup, selectedPokemon) =
            await Service.GetFilteredPokemonList(ctx.User.Id, sortOrder, parsed.Filter, parsed.Gender,
                parsed.Search!);

        if (stats is null || (filteredList.Count == 0 && parsed.Gender == "all" && parsed.Sort == "default" &&
                              parsed.Filter == "all" && string.IsNullOrWhiteSpace(parsed.Search)))
        {
            await ErrorAsync($"You have not started!\nStart with `{prefix}start` first!");
            return;
        }

        if (filteredList.Count == 0)
        {
            await ErrorAsync("No Pokemon match your filters! Try different filter options.");
            return;
        }

        var filterInfo = parsed.Filter != "all" ? $" ({parsed.Filter.Titleize()})" : "";
        var sortInfo = parsed.Sort != "default" ? $" - Sorted by {parsed.Sort.Titleize()}" : "";
        var searchInfo = string.IsNullOrWhiteSpace(parsed.Search) ? "" : $" - Search: `{parsed.Search}`";
        var genderInfo = parsed.Gender != "all" ? $" - {parsed.Gender.Titleize()}" : "";

        if (parsed.View == "detailed")
        {
            const int perPage = 5;
            var pageCount = (filteredList.Count - 1) / perPage + 1;

            async Task<Page> RenderDetailed(int index)
            {
                var embeds = new List<EmbedRequest>();
                var attachments = new List<AttachmentRequest>();
                var pageItems = filteredList.Skip(index * perPage).Take(perPage).ToList();

                for (var i = 0; i < pageItems.Count; i++)
                {
                    var pokemon = pageItems[i];
                    var badge = Badge(pokemon.Shiny, pokemon.Radiant, pokemon.Skin, "");
                    var title = new StringBuilder(badge.Length > 0 ? badge + " " : "");
                    if (pokemon.Favorite) title.Append("⭐ ");
                    if (partyLookup.Contains(pokemon.BotId)) title.Append("👥 ");
                    if (pokemon.BotId == selectedPokemon) title.Append("🔍 ");
                    if (pokemon.Champion) title.Append("🏆 ");
                    if (pokemon.MarketEnlist) title.Append("💰 ");
                    title.Append(pokemon.Name.Titleize());
                    title.Append(' ').Append(GenderEmoji(pokemon.Gender));

                    var description = new StringBuilder()
                        .AppendLine($"**No.** {pokemon.Number} | **Level** {pokemon.Level}")
                        .AppendLine($"**IV%** {pokemon.IvPercent:P2} | **Nature** {pokemon.Nature}")
                        .AppendLine($"**Held Item** {pokemon.HeldItem ?? "None"}");

                    if (!string.IsNullOrEmpty(pokemon.Nickname) && pokemon.Nickname != pokemon.Name)
                        description.AppendLine($"**Nickname** {pokemon.Nickname}");

                    var types = await Service.GetPokemonTypes(pokemon.Name);
                    if (types is { Count: > 0 })
                        description.AppendLine($"**Type** {string.Join(" ", types.Select(Emojis.Type))}");

                    var embed = new EmbedBuilder()
                        .WithTitle(title.ToString().TrimEnd())
                        .WithDescription(description.ToString())
                        .WithColor(Color.Blue);

                    var (_, imagePath) = await Service.GetPokemonFormInfo(pokemon.Name, pokemon.Shiny,
                        pokemon.Radiant, pokemon.Skin ?? "");
                    SetImage(embed, imagePath, $"pokemon_{i}.png", attachments, true);

                    embeds.Add(embed.Build());
                }

                var header = $"**Your Pokemon{filterInfo}{sortInfo}{genderInfo}{searchInfo}**\n" +
                             $"Detailed View • Page {index + 1}/{pageCount} • {filteredList.Count} Pokémon";
                return new Page(header, embeds, attachments);
            }

            await Reactions.PaginateAsync(ctx.Channel, ctx.User.Id, pageCount, RenderDetailed,
                startPage: parsed.Page - 1);
            return;
        }

        var itemsPerPage = parsed.View == "compact" ? 30 : 15;
        var listPages = (filteredList.Count - 1) / itemsPerPage + 1;

        var statsEmbed = new EmbedBuilder()
            .WithTitle("Your Pokemon Collection")
            .WithColor(Color.Gold)
            .WithDescription(GenerateCollectionStats(stats, parsed.Filter, filteredList.Count))
            .Build();

        var legendEmbed = new EmbedBuilder()
            .WithTitle("Icon Legend")
            .WithDescription(
                "⭐ - Favorite Pokemon\n" +
                "👥 - Party Member\n" +
                "🔍 - Currently Selected\n" +
                "🏆 - Champion Pokemon\n" +
                "💰 - Listed in Market\n" +
                $"{Emojis.Shiny} - Shiny Pokemon\n" +
                $"{Emojis.Radiant} - Radiant Pokemon\n" +
                $"{Emojis.Shadow} - Shadow Pokemon\n" +
                $"{Emojis.Skin} - Pokemon with a skin\n" +
                "♂️ - Male Pokemon\n" +
                "♀️ - Female Pokemon")
            .WithColor(Color.Gold)
            .Build();

        Task<Page> RenderText(int index)
        {
            if (index == 0)
                return Task.FromResult(new Page(null, [statsEmbed]));
            if (index == listPages + 1)
                return Task.FromResult(new Page(null, [legendEmbed]));

            var description = new StringBuilder();
            foreach (var pokemon in filteredList.Skip((index - 1) * itemsPerPage).Take(itemsPerPage))
            {
                var flags = Badge(pokemon.Shiny, pokemon.Radiant, pokemon.Skin, Emojis.Blank) +
                            GenderEmoji(pokemon.Gender) +
                            (pokemon.Favorite ? "⭐ " : "") +
                            (partyLookup.Contains(pokemon.BotId) ? "👥 " : "") +
                            (pokemon.BotId == selectedPokemon ? "🔍 " : "") +
                            (pokemon.Champion ? "🏆 " : "") +
                            (pokemon.MarketEnlist ? "💰 " : "");

                if (parsed.View == "compact")
                {
                    description.AppendLine($"{flags}**{pokemon.Name.Titleize()}** | " +
                                           $"Lv.{pokemon.Level} | {pokemon.IvPercent:P0}");
                }
                else
                {
                    description.AppendLine($"{flags}**{pokemon.Name.Titleize()}** | " +
                                           $"**No.** {pokemon.Number} | " +
                                           $"**Level** {pokemon.Level} | " +
                                           $"**IV%** {pokemon.IvPercent:P2}");

                    if (!string.IsNullOrEmpty(pokemon.Nickname) && pokemon.Nickname != pokemon.Name)
                        description.AppendLine(
                            $"└ Nickname: `{pokemon.Nickname}` | Held Item: `{pokemon.HeldItem ?? "None"}`");
                }
            }

            var embed = new EmbedBuilder()
                .WithTitle($"Your Pokemon{filterInfo}{sortInfo}{genderInfo}{searchInfo}")
                .WithDescription(description.ToString())
                .WithColor(new Color(255, 182, 193))
                .WithFooter($"Page {index}/{listPages} • {filteredList.Count} Pokémon")
                .Build();
            return Task.FromResult(new Page(null, [embed]));
        }

        await Reactions.PaginateAsync(ctx.Channel, ctx.User.Id, listPages + 2, RenderText,
            startPage: parsed.Page > 0 ? parsed.Page : 0);
    }

    /// <summary>
    ///     Resurrects the player's dead Pokémon, recovering lost references first.
    /// </summary>
    [Command("pokemon resurrect")]
    [Summary("Attempt to resurrect dead Pokémon.")]
    [RequireContext(ContextType.Guild)]
    public async Task Resurrect()
    {
        var (potentialDeadPokemon, _) = await Service.CheckInvalidReferencesAgainstDeadPokemon(ctx.User.Id);
        var recoveredCount = 0;

        if (potentialDeadPokemon.Count > 0)
        {
            recoveredCount = await Service.RecoverDeadPokemonReferences(ctx.User.Id);

            if (recoveredCount > 0)
                await SendAsync(
                    $"Recovered {recoveredCount} references to dead Pokémon that were missing from your collection.");
        }

        var deadPokemon = await Service.GetDeadPokemon(ctx.User.Id);

        if (deadPokemon.Count == 0)
        {
            await SendAsync(recoveredCount > 0
                ? "Strange... recovered references to dead Pokémon, but couldn't find any actual dead Pokémon."
                : "You do not have any dead Pokémon.");
            return;
        }

        await SendAsync("Checking for dead Pokémon... and performing necromancy.");

        var recoveredIds = potentialDeadPokemon.Select(r => r.PokemonId).ToHashSet();
        var pages = new List<EmbedRequest>();
        const int itemsPerPage = 30;

        for (var i = 0; i < deadPokemon.Count; i += itemsPerPage)
        {
            var text = new StringBuilder();
            foreach (var pokemon in deadPokemon.Skip(i).Take(itemsPerPage))
            {
                var ivPercentage = (pokemon.HpIv + pokemon.AttackIv + pokemon.DefenseIv +
                                    pokemon.SpecialAttackIv + pokemon.SpecialDefenseIv + pokemon.SpeedIv) / 186.0 *
                                   100;
                var marker = recoveredIds.Contains(pokemon.Id) ? "🔄 " : "✅ ";
                text.AppendLine(
                    $"{marker}**ID: `{pokemon.Id}` | Name: `{pokemon.PokemonName}` | IV%: `{ivPercentage:F2}`%**");
            }

            pages.Add(new EmbedBuilder().WithColor(Color.Purple).WithDescription(text.ToString()).Build());
        }

        if (recoveredCount > 0)
            pages.Insert(0, new EmbedBuilder()
                .WithColor(Color.Blue)
                .WithTitle("Pokémon Recovery Report")
                .WithDescription(
                    "📊 **Recovery Summary**\n\n" +
                    $"• Found {recoveredCount} references to dead Pokémon that were missing from your collection\n" +
                    "• Successfully linked these references to your actual dead Pokémon\n" +
                    $"• Proceeding with resurrection of all {deadPokemon.Count} dead Pokémon\n\n" +
                    "In the following pages, recovered Pokémon are marked with 🔄\n" +
                    "Regular resurrections are marked with ✅")
                .Build());

        await Service.ResurrectPokemon(deadPokemon);

        await Reactions.PaginateAsync(ctx.Channel, ctx.User.Id, pages.Count,
            i => Task.FromResult(new Page(pages.Count > 1 ? $"Page {i + 1}/{pages.Count}" : null, [pages[i]])));
    }

    /// <summary>
    ///     Shows one of the player's Pokémon, or a species, as a card.
    /// </summary>
    /// <param name="pokemon">A number, <c>new</c>, a species name, or nothing for the selected Pokémon.</param>
    [Command("pokemon info")]
    [Alias("info", "i")]
    [Summary("Get information about a Pokémon.\n" +
             "No argument shows your selected Pokémon, `new` your newest, a number that Pokémon in your collection. " +
             "A name shows the species, and `shiny`, `radiant` or `shadow` in the name shows that variant. " +
             "◀️ ▶️ page through your collection and ℹ️ shows forms and the evolution line.")]
    [RequireContext(ContextType.Guild)]
    public async Task Info([Remainder] string? pokemon = null)
    {
        var prefix = await GetPrefixAsync();
        try
        {
            var totalPokemonCount = await Service.GetUserPokemonCount(ctx.User.Id);

            if (string.IsNullOrWhiteSpace(pokemon))
            {
                var selectedPokemon = await Service.GetSelectedPokemonAsync(ctx.User.Id);
                if (selectedPokemon == null)
                {
                    await ErrorAsync(
                        $"You haven't selected a Pokemon. Use `{prefix}select <number>` to select one first, " +
                        "or specify a Pokemon name/number.");
                    return;
                }

                var pokemonIndex = await Service.GetPokemonIndex(ctx.User.Id, selectedPokemon.Id);
                await DisplayPokemonWithNavigation(totalPokemonCount, pokemonIndex, false, prefix);
                return;
            }

            if (pokemon.ToLower() is "newest" or "latest" or "atest" or "ewest" or "new")
            {
                await DisplayPokemonWithNavigation(totalPokemonCount, totalPokemonCount, true, prefix);
                return;
            }

            if (long.TryParse(pokemon, out var pokeNumber))
            {
                if (pokeNumber < 1)
                {
                    await ErrorAsync("That is not a valid pokemon number!");
                    return;
                }

                if (pokeNumber > 4000000000)
                {
                    await ErrorAsync("You probably don't have that many pokemon...");
                    return;
                }

                await DisplayPokemonWithNavigation(totalPokemonCount, (ulong)pokeNumber, false, prefix);
                return;
            }

            await DisplaySpecies(pokemon, prefix);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in info command for user {UserId} with parameter '{Parameter}'", ctx.User.Id,
                pokemon ?? "null");
            await ErrorAsync($"An error occurred while retrieving Pokemon information.\n**Error**: {ex.Message}");
        }
    }

    /// <summary>
    ///     Shows the player's Pokédex progress.
    /// </summary>
    /// <param name="variant">national, unowned, shadow, shiny, radiant or skin.</param>
    /// <param name="page">The page to open.</param>
    [Command("pokemon pokedex")]
    [Summary("View your Pokédex progress. Variants: `national`, `unowned`, `shadow`, `shiny`, `radiant`, `skin`.")]
    public async Task Pokedex(string? variant = null, int page = 1)
    {
        if (int.TryParse(variant, out var pageArgument))
        {
            page = pageArgument;
            variant = null;
        }

        variant = string.IsNullOrEmpty(variant) ? "national" : variant.ToLowerInvariant();
        if (!PokedexValues.Contains(variant))
        {
            await ErrorAsync(
                "Invalid variant selected. Available options: national, unowned, shadow, shiny, radiant, skin");
            return;
        }

        try
        {
            var pokemonList = await Service.GetAllPokemon();
            var userPokemon = await Service.GetUserPokemons(ctx.User.Id);

            List<string> lines;
            string title;

            switch (variant)
            {
                case "national":
                {
                    var owned = userPokemon.Select(p => p.PokemonName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    lines = pokemonList.Select(p =>
                        $"**{p.Identifier.Titleize()}** - {(owned.Contains(p.Identifier) ? Emojis.Save : Emojis.Delete)}")
                        .ToList();
                    title = $"National Pokedex ({userPokemon.DistinctBy(x => x.PokemonName).Count()}/{pokemonList.Count})";
                    break;
                }
                case "unowned":
                {
                    var owned = userPokemon.Select(p => p.PokemonName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    lines = pokemonList.Where(p => !owned.Contains(p.Identifier))
                        .Select(p => $"**{p.Identifier.Titleize()}** - {Emojis.DeleteAlt}")
                        .ToList();
                    title = $"Unowned Pokemon ({userPokemon.Count}/{pokemonList.Count})";
                    break;
                }
                default:
                {
                    var ownedVariant = variant switch
                    {
                        "shiny" => userPokemon.Where(p => p.Shiny == true),
                        "radiant" => userPokemon.Where(p => p.Radiant == true),
                        "shadow" => userPokemon.Where(p => p.Skin == "shadow"),
                        _ => userPokemon.Where(p => !string.IsNullOrEmpty(p.Skin))
                    };
                    var ownedList = ownedVariant.ToList();
                    var owned = ownedList.Select(p => p.PokemonName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var icon = variant switch
                    {
                        "shiny" => Emojis.Star,
                        "radiant" => Emojis.RadiantAnimated,
                        "shadow" => Emojis.Shadow,
                        _ => Emojis.Skin
                    };
                    lines = pokemonList.Select(p =>
                        $"**{p.Identifier.Titleize()}** - {(owned.Contains(p.Identifier) ? icon : Emojis.DeleteAlt)}")
                        .ToList();
                    title = $"{variant.Titleize()} Pokedex ({ownedList.Count}/{pokemonList.Count})";
                    break;
                }
            }

            if (lines.Count == 0)
            {
                await ConfirmAsync($"**{title}**\nThere is nothing to show here.");
                return;
            }

            const int perPage = 20;
            var pageCount = (lines.Count - 1) / perPage + 1;
            await Reactions.PaginateAsync(ctx.Channel, ctx.User.Id, pageCount,
                i => Task.FromResult(new Page(null,
                [
                    new EmbedBuilder()
                        .WithColor(Color.Blue)
                        .WithTitle(title)
                        .WithDescription(string.Join("\n", lines.Skip(i * perPage).Take(perPage)))
                        .WithFooter($"Page {i + 1}/{pageCount}")
                        .Build()
                ])), startPage: page - 1);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error displaying {Variant} pokedex", variant);
            await ErrorAsync("An error occurred while loading the Pokedex.");
        }
    }

    /// <summary>
    ///     Releases a Pokémon permanently after confirmation.
    /// </summary>
    /// <param name="number">The Pokémon's number in the collection.</param>
    [Command("pokemon release")]
    [Summary("Release a Pokémon permanently.")]
    public async Task Release(ulong number)
    {
        try
        {
            var pokemon = await Service.GetPokemonByNumberAsync(ctx.User.Id, number);
            if (pokemon == null)
            {
                await ErrorAsync("That pokemon does not exist!");
                return;
            }

            var ivPercentage = IvPercent(pokemon);
            var confirmEmbed = new EmbedBuilder()
                .WithTitle("Confirm Release")
                .WithDescription($"Are you sure you want to release your {pokemon.PokemonName} ({ivPercentage:F2}%)?")
                .WithColor(Color.Red)
                .WithFooter("This action cannot be undone!");

            if (!await PromptConfirmAsync(confirmEmbed))
            {
                await ErrorAsync("Release cancelled.");
                return;
            }

            await Service.RemoveUserPokemon(ctx.User.Id, pokemon.Id, true);
            await ConfirmAsync($"You have successfully released your {pokemon.PokemonName}!");
        }
        catch (Exception ex)
        {
            await ErrorAsync(ex.Message);
        }
    }

    /// <summary>
    ///     Sacrifices a Pokémon to fill the soul gauge after confirmation.
    /// </summary>
    /// <param name="number">The Pokémon's number in the collection.</param>
    [Command("pokemon sacrifice")]
    [Summary("Sacrifice a Pokémon to fill your soul-gauge.")]
    public async Task Sacrifice(ulong number)
    {
        try
        {
            var pokemon = await Service.GetPokemonByNumberAsync(ctx.User.Id, number);
            if (pokemon == null)
            {
                await ErrorAsync("That pokemon does not exist!");
                return;
            }

            var ivPercentage = IvPercent(pokemon);
            var soulIncrement = ivPercentage switch
            {
                <= 49.99 => random.NextDouble() * (0.03 - 0.01) + 0.01,
                <= 59.99 => random.NextDouble() * (0.25 - 0.1) + 0.1,
                <= 64.99 => random.NextDouble() * (0.25 - 0.15) + 0.15,
                <= 69.99 => random.NextDouble() * (0.3 - 0.2) + 0.2,
                <= 74.99 => random.NextDouble() * (0.4 - 0.35) + 0.35,
                <= 79.99 => random.NextDouble() * (0.5 - 0.35) + 0.35,
                <= 84.99 => random.NextDouble() * (2.0 - 0.9) + 0.9,
                <= 86.99 => random.NextDouble() * (5.0 - 0.9) + 0.9,
                <= 88.99 => random.NextDouble() * (10.0 - 5.0) + 5.0,
                <= 91.99 => random.NextDouble() * (20.0 - 10.0) + 10.0,
                <= 92.99 => random.NextDouble() * (40.0 - 10.0) + 10.0,
                <= 93.99 => random.NextDouble() * (60.0 - 40.0) + 40.0,
                <= 95.99 => random.NextDouble() * (100.0 - 60.0) + 60.0,
                <= 97.99 => random.NextDouble() * (150.0 - 60.0) + 60.0,
                <= 100.0 => random.NextDouble() * (175.0 - 150.0) + 150.0,
                _ => 0
            };

            var confirmEmbed = new EmbedBuilder()
                .WithTitle("Confirm Sacrifice")
                .WithDescription($"Are you sure you want to sacrifice your {pokemon.PokemonName} ({ivPercentage:F2}%)?")
                .WithColor(Color.Red)
                .WithFooter("This action cannot be undone!");

            if (!await PromptConfirmAsync(confirmEmbed))
            {
                await ErrorAsync("Sacrifice cancelled.");
                return;
            }

            await Service.RemoveUserPokemon(ctx.User.Id, pokemon.Id);
            await Service.IncrementSoulGauge(ctx.User.Id, soulIncrement);

            var currentGauge = await Service.GetUserSoulGauge(ctx.User.Id);
            var gaugeDisplay = currentGauge >= 1000
                ? string.Concat(Enumerable.Repeat(Emojis.Bar.BeamMiddle, 10))
                : string.Concat(Enumerable.Repeat(Emojis.Bar.BeamMiddle, currentGauge / 100)) + Emojis.Bar.BeamStart;

            var responseEmbed = new EmbedBuilder()
                .WithTitle("Pokemon Sacrificed")
                .WithDescription($"You sacrificed your {pokemon.PokemonName} ({ivPercentage:F2}%)\n" +
                                 $"Soul Gauge increased by {soulIncrement:F2}\n" +
                                 $"Current Gauge: {gaugeDisplay}")
                .WithColor(Color.Purple);

            if (currentGauge >= 1000)
                responseEmbed.AddField("Soul Gauge Full!",
                    "You must empty your gauge by communicating with the Ancient Pokemon to collect a reward and to continue collecting souls.\n" +
                    $"Use `{await GetPrefixAsync()}meditate` to try to communicate with the Ancient Pokemons spirit.");

            await SendAsync(embed: responseEmbed);
        }
        catch (Exception ex)
        {
            await ErrorAsync(ex.Message);
        }
    }

    private async Task DisplayPokemonWithNavigation(ulong totalPokemonCount, ulong startPosition, bool recentMode,
        string prefix)
    {
        var startingPokemon = await Service.GetPokemonByNumberAsync(ctx.User.Id, startPosition);
        if (startingPokemon == null)
        {
            await ErrorAsync("You do not have that many pokemon. Go catch some more first!");
            return;
        }

        async Task<Page> Render(int index, bool more)
        {
            var position = (ulong)index + 1;
            try
            {
                var pokemon = await Service.GetPokemonByNumberAsync(ctx.User.Id, position);
                if (pokemon == null)
                    return new Page(null,
                    [
                        new EmbedBuilder()
                            .WithTitle("Pokemon Not Found")
                            .WithDescription($"No Pokemon found at position {position}")
                            .WithColor(Color.Red)
                            .Build()
                    ]);

                var embed = await CreatePokemonInfoEmbed(pokemon, totalPokemonCount, position,
                    recentMode ? (int)position : null, recentMode ? (int)totalPokemonCount : null);

                if (more)
                {
                    var forms = await Service.GetPokemonForms(pokemon.PokemonName);
                    var evolutionLine = await Service.GetEvolutionLine(pokemon.PokemonName);
                    AddMoreInfoFields(embed, forms, evolutionLine, prefix);
                }

                var (_, imagePath) = await Service.GetPokemonFormInfo(pokemon.PokemonName,
                    pokemon.Shiny ?? false, pokemon.Radiant ?? false, pokemon.Skin ?? "");
                var attachments = new List<AttachmentRequest>();
                SetImage(embed, imagePath, "pokemon.png", attachments);

                return new Page(null, [embed.Build()], attachments);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error generating Pokemon info page for user {UserId} at position {Position}",
                    ctx.User.Id, position);
                return new Page(null,
                [
                    new EmbedBuilder()
                        .WithTitle("Error")
                        .WithDescription($"Error loading Pokemon at position {position}\n**Error**: {ex.Message}")
                        .WithColor(Color.Red)
                        .WithFooter("Please report this error to support")
                        .Build()
                ]);
            }
        }

        await ShowCardAsync((int)Math.Min(totalPokemonCount, int.MaxValue), (int)startPosition - 1, Render);
    }

    private async Task DisplaySpecies(string poke, string prefix)
    {
        var pokemonName = poke.ToLower().Replace("alolan", "alola").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shiny = false;
        var radiant = false;
        string? skin = null;

        if (pokemonName.Contains("shiny"))
        {
            shiny = true;
            pokemonName = pokemonName.Where(v => v != "shiny").ToArray();
        }
        else if (pokemonName.Contains("radiant"))
        {
            radiant = true;
            pokemonName = pokemonName.Where(v => v != "radiant").ToArray();
        }
        else if (pokemonName.Contains("shadow") && Array.IndexOf(pokemonName, "shadow") == 0)
        {
            skin = "shadow";
            pokemonName = pokemonName.Where(v => v != "shadow").ToArray();
        }

        var finalName = string.Join("-", pokemonName);
        var val = finalName.ToTitleCase();

        var pokemonInfo = await Service.GetPokemonInfo(val);
        if (pokemonInfo == null)
        {
            var allPokemon = await Service.GetAllPokemon();
            var suggestion = allPokemon
                .Where(p => !string.IsNullOrEmpty(p.Identifier))
                .MinBy(p => p.Identifier.LevenshteinDistance(finalName))?.Identifier;
            await ErrorAsync(suggestion is null
                ? $"There is no Pokémon called `{poke}`."
                : $"There is no Pokémon called `{poke}`. Did you mean **{suggestion.Titleize()}**?");
            return;
        }

        var (_, imagePath) = await Service.GetPokemonFormInfo(pokemonInfo.Name, shiny, radiant, skin!);
        var footer = string.Format(Footers[random.Next(Footers.Length)], prefix);

        var infoField = new StringBuilder();
        infoField.AppendLine($"**Abilities**: `{string.Join(", ", pokemonInfo.Abilities).ToTitleCase()}`");
        infoField.AppendLine($"**Types**: {string.Join(", ", pokemonInfo.Types.Select(Emojis.Type))}");
        infoField.AppendLine($"**Egg Groups**: {string.Join(", ", pokemonInfo.EggGroups.Select(e => e.Titleize()))}");
        infoField.AppendLine($"**Weight**: `{pokemonInfo.Weight} kg`");
        infoField.AppendLine("\n**Stats**");
        infoField.AppendLine($"{PokemonDisplayConstants.HP_DISPLAY} `{pokemonInfo.Stats.Hp}`");
        infoField.AppendLine($"{PokemonDisplayConstants.ATK_DISPLAY} `{pokemonInfo.Stats.Attack}`");
        infoField.AppendLine($"{PokemonDisplayConstants.DEF_DISPLAY} `{pokemonInfo.Stats.Defense}`");
        infoField.AppendLine($"{PokemonDisplayConstants.SPATK_DISPLAY} `{pokemonInfo.Stats.SpecialAttack}`");
        infoField.AppendLine($"{PokemonDisplayConstants.SPDEF_DISPLAY} `{pokemonInfo.Stats.SpecialDefense}`");
        infoField.AppendLine($"{PokemonDisplayConstants.SPE_DISPLAY} `{pokemonInfo.Stats.Speed}`");

        var color = new Color(random.Next(256), random.Next(256), random.Next(256));

        async Task<Page> Render(int _, bool more)
        {
            var embed = new EmbedBuilder()
                .WithTitle($"{Badge(shiny, radiant, skin, "")} {val}".TrimStart())
                .WithColor(color)
                .WithFooter(footer)
                .AddField("Pokemon Information", infoField.ToString());

            var attachments = new List<AttachmentRequest>();
            SetImage(embed, imagePath, "pokemon.png", attachments);

            var embeds = new List<EmbedRequest> { embed.Build() };
            if (more)
            {
                var moreInfo = new EmbedBuilder().WithColor(0xFF0060).WithTitle("Additional Information");
                AddMoreInfoFields(moreInfo, await Service.GetPokemonForms(pokemonInfo.Name),
                    await Service.GetEvolutionLine(pokemonInfo.Name), prefix);
                embeds.Add(moreInfo.Build());
            }

            return new Page(null, embeds, attachments);
        }

        await ShowCardAsync(1, 0, Render);
    }

    private static void AddMoreInfoFields(EmbedBuilder embed, string? forms, string? evolutionLine, string prefix)
    {
        if (string.IsNullOrEmpty(forms) || forms.Contains("None"))
            embed.AddField("Available Forms:", "`This Pokemon has no forms.`");
        else
            embed.AddField("Available Forms:",
                $"{forms}\n`{prefix}pokemon forms form <form name>`\nor `{prefix}pokemon forms mega`");

        if (!string.IsNullOrEmpty(evolutionLine))
            embed.AddField("Evolution Line:", evolutionLine);
    }

    /// <summary>
    ///     Sends a card and lets the player page with arrows and toggle the extra information with ℹ️.
    /// </summary>
    private async Task ShowCardAsync(int pageCount, int startPage, Func<int, bool, Task<Page>> render)
    {
        var index = Math.Clamp(startPage, 0, Math.Max(0, pageCount - 1));
        var more = false;
        var page = await render(index, more);
        var message = await ReplyAsync(page.Content, page.Embeds, attachments: page.Attachments);

        string[] controls = pageCount switch
        {
            > 2 => [First, Back, Next, Last, InfoToggle, Stop],
            2 => [Back, Next, InfoToggle, Stop],
            _ => [InfoToggle, Stop]
        };
        var adding = ReactionService.AddReactionsAsync(message, controls);

        while (await Reactions.WaitForPressAsync(message.Id, ctx.User.Id, controls, TimeSpan.FromMinutes(5)) is
               { } pressed)
        {
            if (pressed == Stop)
                break;

            var next = pressed switch
            {
                First => 0,
                Back => index == 0 ? pageCount - 1 : index - 1,
                Next => index == pageCount - 1 ? 0 : index + 1,
                Last => pageCount - 1,
                _ => index
            };
            if (pressed == InfoToggle)
                more = !more;
            else if (next == index)
                continue;

            index = next;
            page = await render(index, more);
            try
            {
                await message.ModifyAsync(page.Content ?? "", page.Embeds, attachments: page.Attachments ?? []);
            }
            catch (FluxerApiException ex)
            {
                Log.Debug(ex, "Could not update the info card {MessageId}", message.Id);
                break;
            }
        }

        await adding;
        await ReactionService.TryClearReactionsAsync(message);
    }

    private async Task<EmbedBuilder> CreatePokemonInfoEmbed(PokemonModel pokemon, ulong pokeCount, ulong selectedPoke,
        int? currentIndex = null, int? totalRecent = null)
    {
        try
        {
            var pokemonInfo = await Service.GetPokemonInfo(pokemon.PokemonName);
            if (pokemonInfo == null)
            {
                Log.Warning("Pokemon info not found for {PokemonName} (ID: {PokemonId})", pokemon.PokemonName,
                    pokemon.Id);
                return new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription(
                        $"Could not find information for {pokemon.PokemonName}. This Pokemon may not exist in the database.")
                    .WithColor(Color.Red);
            }

            var calculatedStats = await Service.CalculateStats(pokemon, pokemonInfo.Stats);
            var friendship = Service.CalculateFriendship(pokemon);
            var ivPercentage = IvPercent(pokemon);

            var titlePrefix = pokemon.Skin switch
            {
                "staff_gif" => Emojis.Staff + " ",
                { } s when s.StartsWith("custom/") => Emojis.Custom + " ",
                _ => ""
            };

            var footerText = currentIndex.HasValue && totalRecent.HasValue
                ? $"Recent #{currentIndex}/{totalRecent} | Number {selectedPoke}/{pokeCount} | Global ID#: {pokemon.Id}"
                : $"Number {selectedPoke}/{pokeCount} | Global ID#: {pokemon.Id}";

            var embed = new EmbedBuilder()
                .WithTitle(
                    ($"{titlePrefix}{Badge(pokemon.Shiny.GetValueOrDefault(), pokemon.Radiant.GetValueOrDefault(), pokemon.Skin, "")} " +
                     $"{GenderEmoji(pokemon.Gender)}{pokemon.PokemonName.Titleize()}").TrimStart())
                .WithColor(new Color(random.Next(256), random.Next(256), random.Next(256)))
                .WithFooter(footerText);

            var infoField = new StringBuilder();
            infoField.AppendLine($"**Level**: `{pokemon.Level}`");

            if (!string.IsNullOrEmpty(pokemon.Nickname) && pokemon.Nickname != pokemon.PokemonName)
                infoField.AppendLine($"**Nickname**: `{pokemon.Nickname}`");

            if (!string.IsNullOrEmpty(pokemon.HeldItem) && pokemon.HeldItem.ToLower() != "none")
                infoField.AppendLine($"**Held Item**: `{pokemon.HeldItem}`");

            var abilityName = await Service.GetAbilityName(pokemon.PokemonName, pokemon.AbilityIndex);
            infoField.AppendLine($"**Ability**: `{abilityName}`");
            infoField.AppendLine($"**Types**: {string.Join(", ", pokemonInfo.Types.Select(Emojis.Type))}");
            infoField.AppendLine($"**Nature**: `{pokemon.Nature}`");

            if (friendship > 0)
                infoField.AppendLine($"**Friendship**: `{friendship}`");

            if (pokemon.CaughtBy is > 0)
            {
                var trainerName = await Service.GetTrainerName(pokemon.CaughtBy.Value);
                infoField.AppendLine($"**Original Trainer**: `{trainerName}`");
            }

            if (pokemon.Timestamp.HasValue)
                infoField.AppendLine($"**Caught on**: `{pokemon.Timestamp.Value:MMM d, yyyy}`");

            var statusFlags = new List<string>();
            if (pokemon.Champion) statusFlags.Add("Champion");
            if (pokemon.Voucher.GetValueOrDefault()) statusFlags.Add("Voucher");
            if (!pokemon.Tradable) statusFlags.Add("Not Tradable");
            if (!pokemon.Breedable) statusFlags.Add("Not Breedable");

            if (statusFlags.Count > 0)
                infoField.AppendLine($"**Status**: `{string.Join(", ", statusFlags)}`");

            if (pokemon.Tags is { Length: > 0 } && !pokemon.Tags.All(string.IsNullOrEmpty))
                infoField.AppendLine(
                    $"**Tags**: `{string.Join(", ", pokemon.Tags.Where(t => !string.IsNullOrEmpty(t)))}`");

            embed.AddField("Pokemon Information", infoField.ToString());

            var moves = pokemon.Moves?.Where(m => !string.IsNullOrEmpty(m)).ToList() ?? [];
            embed.AddField("Moves", moves.Count > 0
                ? string.Join("\n", moves.Select(m => $"• `{m.Titleize()}`"))
                : "No moves learned yet.");

            var statsField = new StringBuilder();
            statsField.AppendLine(
                $"{PokemonDisplayConstants.HP_DISPLAY} `{pokemon.HpIv}/31` | `{pokemon.HpEv}/252` | `{calculatedStats.MaxHp}`");
            statsField.AppendLine(
                $"{PokemonDisplayConstants.ATK_DISPLAY} `{pokemon.AttackIv}/31` | `{pokemon.AttackEv}/252` | `{calculatedStats.Attack}`");
            statsField.AppendLine(
                $"{PokemonDisplayConstants.DEF_DISPLAY} `{pokemon.DefenseIv}/31` | `{pokemon.DefenseEv}/252` | `{calculatedStats.Defense}`");
            statsField.AppendLine(
                $"{PokemonDisplayConstants.SPATK_DISPLAY} `{pokemon.SpecialAttackIv}/31` | `{pokemon.SpecialAttackEv}/252` | `{calculatedStats.SpecialAttack}`");
            statsField.AppendLine(
                $"{PokemonDisplayConstants.SPDEF_DISPLAY} `{pokemon.SpecialDefenseIv}/31` | `{pokemon.SpecialDefenseEv}/252` | `{calculatedStats.SpecialDefense}`");
            statsField.AppendLine(
                $"{PokemonDisplayConstants.SPE_DISPLAY} `{pokemon.SpeedIv}/31` | `{pokemon.SpeedEv}/252` | `{calculatedStats.Speed}`");

            embed.AddField($"Stats (IV | EV | Total) - {ivPercentage:F2}% IV", statsField.ToString());

            if (pokemon.Level < 100)
                embed.AddField("Experience",
                    $"`{pokemon.Experience:N0}` / `{pokemon.ExperienceCap:N0}` " +
                    $"(`{(double)pokemon.Experience / pokemon.ExperienceCap * 100:F1}%` to next level)");

            if (pokemon.Counter is > 0)
                embed.AddField("Usage Stats", $"This Pokemon has been used `{pokemon.Counter.Value}` times.");

            return embed;
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "Error creating Pokemon info embed for {PokemonName} (ID: {PokemonId}) owned by user {UserId}",
                pokemon.PokemonName, pokemon.Id, ctx.User.Id);

            return new EmbedBuilder()
                .WithTitle("Error")
                .WithDescription($"Failed to load information for {pokemon.PokemonName}.\n**Error**: {ex.Message}")
                .WithColor(Color.Red)
                .WithFooter($"Pokemon ID: {pokemon.Id} | Please report this error to support");
        }
    }

    private static string GenerateCollectionStats(Dictionary<string, int> stats, string filter, int filteredCount)
    {
        var statsBuilder = new StringBuilder();

        statsBuilder.AppendLine($"**Collection Overview** - {stats["Total"]} Pokémon");
        statsBuilder.AppendLine($"• Shiny: {stats["Shiny"]}");
        statsBuilder.AppendLine($"• Radiant: {stats["Radiant"]}");
        statsBuilder.AppendLine($"• Shadow: {stats["Shadow"]}");
        statsBuilder.AppendLine($"• Legendary: {stats["Legendary"]}");
        statsBuilder.AppendLine($"• Favorite: {stats["Favorite"]}");
        statsBuilder.AppendLine($"• Champion: {stats["Champion"]}");
        statsBuilder.AppendLine($"• Market Listed: {stats["Market"]}");
        statsBuilder.AppendLine($"• Male: {stats["Male"]}");
        statsBuilder.AppendLine($"• Female: {stats["Female"]}");
        statsBuilder.AppendLine($"• Genderless: {stats["Genderless"]}");

        if (stats.TryGetValue("TotalCount", out var totalCount) && totalCount != stats["Total"])
        {
            statsBuilder.AppendLine();
            statsBuilder.AppendLine($"**Total Collection Size**: {totalCount} Pokémon");
        }

        if (filter != "all")
        {
            statsBuilder.AppendLine();
            statsBuilder.AppendLine($"**Current Filter**: {filter.Titleize()} - {filteredCount} Pokémon");
        }

        statsBuilder.AppendLine();
        statsBuilder.AppendLine("Use the arrow reactions to navigate your Pokemon list");
        statsBuilder.AppendLine("Check the last page for an icon legend");

        return statsBuilder.ToString();
    }

    private sealed record ListOptions(string Sort, string Filter, string Gender, string View, string? Search, int Page);

    private static (ListOptions? Options, string? Error) ParseListOptions(IEnumerable<string> tokens)
    {
        string sort = "default", filter = "all", gender = "all", view = "detailed";
        var page = 0;
        var search = new List<string>();

        foreach (var raw in tokens)
        {
            var token = raw.ToLowerInvariant();
            var split = token.IndexOfAny([':', '=']);
            if (split > 0)
            {
                var key = token[..split];
                var value = token[(split + 1)..];
                string? Check(string[] allowed, string name) =>
                    allowed.Contains(value) ? null : $"`{value}` is not a {name}. Use one of: {string.Join(", ", allowed)}.";

                var error = key switch
                {
                    "sort" => Check(SortValues, "sort"),
                    "filter" => Check(FilterValues, "filter"),
                    "gender" => Check(GenderValues, "gender"),
                    "view" => Check(ViewValues, "view"),
                    "page" => int.TryParse(value, out var p) && p > 0 ? null : $"`{value}` is not a page number.",
                    "search" => null,
                    _ => $"`{key}` is not an option. Use sort, filter, gender, view, page or search."
                };
                if (error is not null)
                    return (null, error);

                switch (key)
                {
                    case "sort": sort = value; break;
                    case "filter": filter = value; break;
                    case "gender": gender = value; break;
                    case "view": view = value; break;
                    case "page": page = int.Parse(value); break;
                    case "search": search.Add(raw[(split + 1)..]); break;
                }

                continue;
            }

            if (int.TryParse(token, out var number) && number > 0)
                page = number;
            else if (ViewValues.Contains(token))
                view = token;
            else if (token is "male" or "female" or "genderless")
                gender = token;
            else if (token != "all" && FilterValues.Contains(token))
                filter = token;
            else if (SortValues.Contains(token))
                sort = token;
            else
                search.Add(raw);
        }

        var term = string.Join(" ", search).Trim();
        return (new ListOptions(sort, filter, gender, view, term.Length == 0 ? null : term, page), null);
    }

    private static double IvPercent(PokemonModel pokemon) =>
        (pokemon.HpIv + pokemon.AttackIv + pokemon.DefenseIv +
         pokemon.SpecialAttackIv + pokemon.SpecialDefenseIv + pokemon.SpeedIv) / 186.0 * 100;

    private static string Badge(bool shiny, bool radiant, string? skin, string none)
    {
        if (radiant) return Emojis.Radiant;
        if (shiny) return Emojis.Shiny;
        return skin switch
        {
            "glitch" => "👾",
            "shadow" => Emojis.Shadow,
            null or "" => none,
            _ => Emojis.Skin
        };
    }

    private static string GenderEmoji(string? gender) => gender?.ToLower() switch
    {
        "-m" or "male" => "♂️ ",
        "-f" or "female" => "♀️ ",
        _ => ""
    };

    private async Task<string> PrefixedAsync(string message) =>
        message.WithPrefix(await GetPrefixAsync());
}

/// <summary>
///     Text helpers for the Pokémon commands.
/// </summary>
internal static class PokemonText
{
    /// <summary>
    ///     Replaces slash command mentions such as <c>`/pokemon select`</c> in a service message with the prefix.
    /// </summary>
    /// <param name="message">The service message.</param>
    /// <param name="prefix">The prefix in the server.</param>
    public static string WithPrefix(this string message, string prefix) => message.Replace("`/", "`" + prefix);
}
