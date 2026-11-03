using EeveeCore.Modules.Duels.Utils;
using EeveeCore.Services.Impl;
using MongoDB.Driver;
using Serilog;

namespace EeveeCore.Modules.Duels.Impl.DuelPokemon;

public partial class DuelPokemon
{
    /// <summary>
    ///     Creates a new DuelPokemon object asynchronously using the raw data provided.
    /// </summary>
    public static async Task<DuelPokemon> Create(Database.Linq.Models.Pokemon.Pokemon pokemon, IMongoService mongoService)
    {
        // Initialize local variables from pokemon object
        var pn = pokemon.PokemonName;
        var nick = pokemon.Nickname;
        var hpiv = Math.Min(31, pokemon.HpIv);
        var atkiv = Math.Min(31, pokemon.AttackIv);
        var defiv = Math.Min(31, pokemon.DefenseIv);
        var spatkiv = Math.Min(31, pokemon.SpecialAttackIv);
        var spdefiv = Math.Min(31, pokemon.SpecialDefenseIv);
        var speediv = Math.Min(31, pokemon.SpeedIv);
        var happiness = pokemon.Happiness;
        var hitem = pokemon.HeldItem;
        var plevel = pokemon.Level;
        var id = pokemon.Id;
        var gender = pokemon.Gender;

        // Sanitize the nickname to remove Discord emotes and formatting
        // Store the original nickname and create a sanitized version for battle display
        var sanitizedNick = string.IsNullOrWhiteSpace(nick)
            ? PokemonNameSanitizer.SanitizeDisplayName(pn) // Use species name if no nickname
            : PokemonNameSanitizer.SanitizeDisplayName(nick); // Sanitize the nickname

        // Validate IVs
        var totalIvs = hpiv + atkiv + defiv + spatkiv + spdefiv + speediv;
        var ivPercentage = Math.Round(totalIvs / 186.0 * 100, 2);
        if (ivPercentage > 100.0) throw new ArgumentException($"IVs must be 100.0% or less, but got {ivPercentage}%");

        // Normalize form name - check if it's a battle form that shouldn't start this way
        pn = NormalizeFormName(pn, plevel);

        // Prepare for potential mega evolution
        string megaForm = null;
        if (pn != "Rayquaza")
            megaForm = hitem switch
            {
                "mega-stone" => pn + "-mega",
                "mega-stone-x" => pn + "-mega-x",
                "mega-stone-y" => pn + "-mega-y",
                _ => null
            };
        else if (pokemon.Moves.Contains("dragon-ascent")) megaForm = pn + "-mega";

        // Determine forms we need data for
        var extraForms = GetExtraForms(pn);
        if (megaForm != null)
            extraForms.Add(megaForm);

        // Create a list of tasks for parallel execution
        var tasks = new List<Task>();

        // Get form information
        var formInfoTask = mongoService.Forms.Find(f => f.Identifier == pn.ToLower()).FirstOrDefaultAsync();
        tasks.Add(formInfoTask);

        // Get nature data
        var natureTask = mongoService.Natures.Find(n => n.Identifier == pokemon.Nature.ToLower()).FirstOrDefaultAsync();
        tasks.Add(natureTask);

        // Get item data
        var itemTask = mongoService.Items.Find(i => i.Identifier == hitem).FirstOrDefaultAsync();
        tasks.Add(itemTask);

        // Wait for these initial tasks to complete
        await Task.WhenAll(tasks);

        var formInfo = await formInfoTask;
        var natureData = await natureTask;
        var hitemData = await itemTask;

        tasks.Clear();

        // Get stat types for nature
        var decStatTask = mongoService.StatTypes.Find(s => s.StatId == natureData.DecreasedStatId)
            .FirstOrDefaultAsync();
        var incStatTask = mongoService.StatTypes.Find(s => s.StatId == natureData.IncreasedStatId)
            .FirstOrDefaultAsync();

        // Get type data
        var typeDataTask = mongoService.PokemonTypes.Find(pt => pt.PokemonId == formInfo.PokemonId)
            .FirstOrDefaultAsync();

        // Get stats data
        var statsDataTask = mongoService.PokemonStats.Find(ps => ps.PokemonId == formInfo.PokemonId)
            .FirstOrDefaultAsync();

        // Get ability data
        var abilityRecordsTask =
            mongoService.PokeAbilities.Find(pa => pa.PokemonId == formInfo.PokemonId).ToListAsync();

        // Check evolution
        var pid = await GetBasePokemonId(pn, formInfo, mongoService);
        var evoCheckTask = mongoService.PFile.Find(pf => pf.EvolvesFromSpeciesId == pid).FirstOrDefaultAsync();

        // Wait for second batch of tasks
        await Task.WhenAll(decStatTask, incStatTask, typeDataTask, statsDataTask, abilityRecordsTask, evoCheckTask);

        var decStat = await decStatTask;
        var incStat = await incStatTask;
        var typeData = await typeDataTask;
        var statsData = await statsDataTask;
        var abilityRecords = await abilityRecordsTask;
        var evoCheck = await evoCheckTask;

        // Process stats
        var stats = statsData.Stats.ToList();
        var pokemonHp = stats[0];

        // Process nature
        var decStatName = decStat.Identifier.Capitalize().Replace("-", " ");
        var incStatName = incStat.Identifier.Capitalize().Replace("-", " ");

        // Initialize nature stat modifiers
        var natureStatDeltas = new Dictionary<string, double>
        {
            { "Attack", 1.0 },
            { "Defense", 1.0 },
            { "Special Attack", 1.0 },
            { "Special attack", 1.0 },
            { "Special Defense", 1.0 },
            { "Special defense", 1.0 },
            { "Speed", 1.0 }
        };

        var flavorMap = new Dictionary<string, string>
        {
            { "Attack", "spicy" },
            { "Defense", "sour" },
            { "Speed", "sweet" },
            { "Special Attack", "dry" },
            { "Special attack", "dry" },
            { "Special Defense", "bitter" },
            { "Special defense", "bitter" }
        };

        var dislikedFlavor = "";
        if (decStatName != incStatName)
        {
            natureStatDeltas[decStatName] = 0.9;
            natureStatDeltas[incStatName] = 1.1;
            dislikedFlavor = flavorMap[decStatName];
        }

        // Store base stats
        var baseStats = new Dictionary<string?, List<int>>
        {
            { pn, stats }
        };

        // Get type IDs
        var typeIds = typeData.Types.Select(t => (ElementType)t).ToList();

        // Process abilities
        var abIds = abilityRecords.Select(record => record.AbilityId).ToList();
        var abId = abIds.Intersect(abilityRecords.Select(x => x.AbilityId)).FirstOrDefault();

        // Process evolution
        var canStillEvolve = evoCheck != null;
        if (pn == "Floette-eternal") canStillEvolve = false;

        // Handle Shedinja special case
        if (pn == "Shedinja")
            pokemonHp = 1;
        else
            pokemonHp = (int)Math.Round((2 * pokemonHp + hpiv + pokemon.HpEv / 4.0) * plevel / 100 + plevel + 10);

        // Process mega evolution data if applicable
        var megaAbilityId = 0;
        List<ElementType> megaTypeIds = null;

        if (megaForm != null)
        {
            var megaDataTasks = await GetMegaData(megaForm, mongoService);
            if (megaDataTasks.megaAbilityId != 0)
            {
                megaAbilityId = megaDataTasks.megaAbilityId;
                megaTypeIds = megaDataTasks.megaTypeIds;
            }
        }

        // Process form stats
        if (extraForms.Count > 0) await LoadFormStats(extraForms, baseStats, mongoService);

        // Process moves
        var objectMoves = await ProcessMoves(pokemon.Moves.ToList(), mongoService);

        return new DuelPokemon(
            pid,
            pn,
            pokemon.PokemonName,
            sanitizedNick, // Use the sanitized nickname instead of the original
            baseStats,
            pokemonHp,
            hpiv,
            atkiv,
            defiv,
            spatkiv,
            spdefiv,
            speediv,
            pokemon.HpEv,
            pokemon.AttackEv,
            pokemon.DefenseEv,
            pokemon.SpecialAttackEv,
            pokemon.SpecialDefenseEv,
            pokemon.SpeedEv,
            plevel,
            natureStatDeltas,
            pokemon.Shiny.GetValueOrDefault(),
            pokemon.Radiant.GetValueOrDefault(),
            pokemon.Skin,
            typeIds,
            megaTypeIds,
            id,
            hitemData,
            happiness,
            objectMoves.ToList(),
            abId,
            megaAbilityId,
            formInfo.Weight ?? 20,
            gender,
            canStillEvolve,
            dislikedFlavor);
    }

    // Helper methods
    private static string? NormalizeFormName(string? pn, int plevel)
    {
        return pn switch
        {
            "Mimikyu-busted" => "Mimikyu",
            "Cramorant-gorging" or "Cramorant-gulping" => "Cramorant",
            "Eiscue-noice" => "Eiscue",
            "Darmanitan-zen" => "Darmanitan",
            "Darmanitan-zen-galar" => "Darmanitan-galar",
            "Aegislash-blade" => "Aegislash",
            not null when pn.StartsWith("Minior-") && (pn.EndsWith("red") || pn.EndsWith("orange") ||
                                                       pn.EndsWith("yellow") || pn.EndsWith("green") ||
                                                       pn.EndsWith("blue") || pn.EndsWith("indigo") ||
                                                       pn.EndsWith("violet")) => "Minior",
            "Wishiwashi" when plevel >= 20 => "Wishiwashi-school",
            "Wishiwashi-school" when plevel < 20 => "Wishiwashi",
            "Greninja-ash" => "Greninja",
            "Zygarde-complete" => "Zygarde",
            "Morpeko-hangry" => "Morpeko",
            "Cherrim-sunshine" => "Cherrim",
            not null when pn.StartsWith("Castform-") => "Castform",
            not null when pn.StartsWith("Arceus-") => "Arceus",
            not null when pn.StartsWith("Silvally-") => "Silvally",
            "Palafin-hero" => "Palafin",
            not null when pn.EndsWith("-mega-x") || pn.EndsWith("-mega-y") => pn[..^7],
            not null when pn.EndsWith("-mega") => pn[..^5],
            _ => pn
        };
    }

    private static List<string> GetExtraForms(string? pn)
    {
        return pn switch
        {
            "Mimikyu" => ["Mimikyu-busted"],
            "Cramorant" => ["Cramorant-gorging", "Cramorant-gulping"],
            "Eiscue" => ["Eiscue-noice"],
            "Darmanitan" => ["Darmanitan-zen"],
            "Darmanitan-galar" => ["Darmanitan-zen-galar"],
            "Aegislash" => ["Aegislash-blade"],
            "Minior" =>
            [
                "Minior-red", "Minior-orange", "Minior-yellow", "Minior-green",
                "Minior-blue", "Minior-indigo", "Minior-violet"
            ],
            "Wishiwashi" => ["Wishiwashi-school"],
            "Wishiwashi-school" => ["Wishiwashi"],
            "Greninja" => ["Greninja-ash"],
            "Zygarde" or "Zygarde-10" => ["Zygarde-complete"],
            "Morpeko" => ["Morpeko-hangry"],
            "Cherrim" => ["Cherrim-sunshine"],
            "Castform" => ["Castform-snowy", "Castform-rainy", "Castform-sunny"],
            "Arceus" => GetArceusFormsList(),
            "Silvally" => GetSilvallyFormsList(),
            "Palafin" => ["Palafin-hero"],
            _ => []
        };
    }

    private static List<string> GetArceusFormsList()
    {
        return
        [
            "Arceus-dragon", "Arceus-dark", "Arceus-ground", "Arceus-fighting",
            "Arceus-fire", "Arceus-ice", "Arceus-bug", "Arceus-steel",
            "Arceus-grass", "Arceus-psychic", "Arceus-fairy", "Arceus-flying",
            "Arceus-water", "Arceus-ghost", "Arceus-rock", "Arceus-poison",
            "Arceus-electric"
        ];
    }

    private static List<string> GetSilvallyFormsList()
    {
        return
        [
            "Silvally-psychic", "Silvally-fairy", "Silvally-flying", "Silvally-water",
            "Silvally-ghost", "Silvally-rock", "Silvally-poison", "Silvally-electric",
            "Silvally-dragon", "Silvally-dark", "Silvally-ground", "Silvally-fighting",
            "Silvally-fire", "Silvally-ice", "Silvally-bug", "Silvally-steel",
            "Silvally-grass"
        ];
    }

    private static async Task<int> GetBasePokemonId(string? pn, dynamic formInfo, IMongoService mongoService)
    {
        Log.Information($"Pokemon name is {pn}");
        if (!IsFormVariant(pn)) return formInfo.PokemonId;
        var name = pn.ToLower().Split('-')[0];
        var originalFormInfo = await mongoService.Forms.Find(f => f.Identifier == name).FirstOrDefaultAsync();
        if (originalFormInfo == null) return formInfo.PokemonId;
        return originalFormInfo.PokemonId;
    }

    private static bool IsFormVariant(string? pn)
    {
        return pn.Contains('-');
    }

    private static async Task<(int megaAbilityId, List<ElementType> megaTypeIds)> GetMegaData(string megaForm,
        IMongoService mongoService)
    {
        var megaFormInfo = await mongoService.Forms.Find(f => f.Identifier == megaForm.ToLower())
            .FirstOrDefaultAsync();

        if (megaFormInfo == null)
            return (0, null);

        var megaAbilityTask = mongoService.PokeAbilities.Find(pa => pa.PokemonId == megaFormInfo.PokemonId)
            .FirstOrDefaultAsync();
        var megaTypesTask = mongoService.PokemonTypes.Find(pt => pt.PokemonId == megaFormInfo.PokemonId)
            .FirstOrDefaultAsync();

        await Task.WhenAll(megaAbilityTask, megaTypesTask);

        var megaAbility = await megaAbilityTask;
        var megaTypes = await megaTypesTask;

        if (megaAbility == null)
            throw new InvalidOperationException("mega form missing ability in `poke_abilities`");

        if (megaTypes == null)
            throw new InvalidOperationException("mega form missing types in `ptypes`");

        return (megaAbility.AbilityId, megaTypes.Types.Select(x => (ElementType)x).ToList());
    }

    private static async Task LoadFormStats(List<string> forms, Dictionary<string, List<int>> baseStats,
        IMongoService mongoService)
    {
        var formTasks = forms.Select(formName => GetFormStats(formName, mongoService)).ToList();

        (string? formName, List<int> stats)[] results = await Task.WhenAll(formTasks);

        foreach (var (formName, stats) in results) 
        {
            if (formName != null) 
                baseStats[formName] = stats;
        }
    }

    private static async Task<(string formName, List<int> stats)> GetFormStats(string formName,
        IMongoService mongoService)
    {
        var formData = await mongoService.Forms.Find(f => f.Identifier == formName.ToLower()).FirstOrDefaultAsync();
        var formStats = await mongoService.PokemonStats.Find(ps => ps.PokemonId == formData.PokemonId)
            .FirstOrDefaultAsync();

        return (formName, formStats.Stats.ToList());
    }

    private static async Task<Move.Move[]> ProcessMoves(List<string> moves, IMongoService mongoService)
    {
        var moveTasks = moves.Select(moveName => CreateMove(moveName, mongoService)).ToList();

        return await Task.WhenAll(moveTasks);
    }

    private static async Task<Move.Move> CreateMove(string moveName, IMongoService mongoService)
    {
        ElementType? typeOverride = null;
        var moveIdentifier = moveName;

        if (moveName.StartsWith("hidden-power-"))
        {
            var element = moveName.Split('-')[2];
            moveIdentifier = "hidden-power";
            typeOverride = (ElementType)Enum.Parse(typeof(ElementType), element.ToUpper());
        }

        var dbMove = await mongoService.Moves.Find(m => m.Identifier == moveIdentifier).FirstOrDefaultAsync() ??
                     await mongoService.Moves.Find(m => m.Identifier == "tackle").FirstOrDefaultAsync();

        // Create game move from database move
        var gameMove = new Move.Move(dbMove);

        // Apply type override if needed
        if (typeOverride.HasValue) gameMove.Type = typeOverride.Value;

        return gameMove;
    }
}