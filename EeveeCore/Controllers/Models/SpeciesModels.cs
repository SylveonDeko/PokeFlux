using EeveeCore.Modules.Extras.Services;

namespace EeveeCore.Controllers.Models;

/// <summary>
///     What the game data holds about a species or form.
/// </summary>
public record SpeciesDetail
{
    /// <summary>The Pokédex number of the species whose data is shown.</summary>
    public required int SpeciesId { get; init; }

    /// <summary>The ID of this form, which differs from the species ID for megas and other forms.</summary>
    public required int FormId { get; init; }

    /// <summary>The lowercase identifier, such as <c>vulpix-alola</c>.</summary>
    public required string Identifier { get; init; }

    /// <summary>The titleized name, as the Pokédex shows it.</summary>
    public required string Name { get; init; }

    /// <summary>The form's name, such as <c>alola</c> or <c>mega-x</c>, or null for the base form.</summary>
    public required string? FormName { get; init; }

    /// <summary>Whether it is a mega evolution.</summary>
    public required bool Mega { get; init; }

    /// <summary>Whether the form only exists during battle.</summary>
    public required bool BattleOnly { get; init; }

    /// <summary>The absolute link of its normal sprite, or null.</summary>
    public required string? SpriteUrl { get; init; }

    /// <summary>The absolute link of its shiny sprite, or null.</summary>
    public required string? ShinySpriteUrl { get; init; }

    /// <summary>Its types.</summary>
    public required List<string> Types { get; init; }

    /// <summary>Its base stats.</summary>
    public required StatBlock BaseStats { get; init; }

    /// <summary>Its abilities in slot order.</summary>
    public required List<SpeciesAbility> Abilities { get; init; }

    /// <summary>The generation it was introduced in, or null.</summary>
    public required int? Generation { get; init; }

    /// <summary>The egg groups it breeds in.</summary>
    public required List<string> EggGroups { get; init; }

    /// <summary>The chance out of 100 of a female, or null when genderless.</summary>
    public required double? FemalePercent { get; init; }

    /// <summary>The catch rate out of 255, or null.</summary>
    public required int? CatchRate { get; init; }

    /// <summary>The friendship it starts with, or null.</summary>
    public required int? BaseHappiness { get; init; }

    /// <summary>The egg cycles its eggs take to hatch, or null.</summary>
    public required int? HatchCounter { get; init; }

    /// <summary>Whether it is a baby Pokémon.</summary>
    public required bool Baby { get; init; }

    /// <summary>Its weight in kilograms, or null.</summary>
    public required double? WeightKg { get; init; }

    /// <summary>The members of its evolution family, each after the species it evolves from; empty when it does not evolve.</summary>
    public required List<SpeciesEvolution> Evolution { get; init; }

    /// <summary>The other forms of its species, including the base form when this is a form.</summary>
    public required List<SpeciesForm> Forms { get; init; }

    /// <summary>The moves it can learn.</summary>
    public required List<MoveDetails> Learnset { get; init; }

    /// <summary>How many of this species the user owns.</summary>
    public required int Owned { get; init; }

    /// <summary>How many of the user's are shiny.</summary>
    public required int OwnedShiny { get; init; }
}

/// <summary>
///     An ability a species can have.
/// </summary>
/// <param name="Name">The ability's identifier.</param>
/// <param name="Hidden">Whether it is the hidden ability.</param>
public record SpeciesAbility(string Name, bool Hidden);

/// <summary>
///     A member of an evolution family.
/// </summary>
/// <param name="SpeciesId">The species ID.</param>
/// <param name="Identifier">The lowercase identifier.</param>
/// <param name="Name">The titleized name.</param>
/// <param name="SpriteUrl">The absolute link of its normal sprite, or null.</param>
/// <param name="EvolvesFrom">The species ID it evolves from, or null for the first stage.</param>
/// <param name="Method">How it evolves, or null for the first stage or when the data has no method.</param>
public record SpeciesEvolution(
    int SpeciesId,
    string Identifier,
    string Name,
    string? SpriteUrl,
    int? EvolvesFrom,
    EvolutionMethod? Method);

/// <summary>
///     The conditions of an evolution that the bot checks. Unset conditions are null.
/// </summary>
public record EvolutionMethod
{
    /// <summary>level-up, trade, use-item or shed.</summary>
    public required string Trigger { get; init; }

    /// <summary>The level it needs.</summary>
    public int? Level { get; init; }

    /// <summary>The item to use on it.</summary>
    public string? Item { get; init; }

    /// <summary>The item it must hold.</summary>
    public string? HeldItem { get; init; }

    /// <summary>The gender it must be, male or female.</summary>
    public string? Gender { get; init; }

    /// <summary>A move it must know.</summary>
    public string? KnownMove { get; init; }

    /// <summary>The friendship it needs.</summary>
    public int? Happiness { get; init; }

    /// <summary>How its Attack must compare to its Defense: higher, lower or equal.</summary>
    public string? AttackVsDefense { get; init; }

    /// <summary>The region the trainer must be in.</summary>
    public string? Region { get; init; }
}

/// <summary>
///     Another form of a species.
/// </summary>
/// <param name="Identifier">The lowercase identifier.</param>
/// <param name="Name">The titleized name.</param>
/// <param name="FormName">The form's name, or null for the base form.</param>
/// <param name="Mega">Whether it is a mega evolution.</param>
/// <param name="SpriteUrl">The absolute link of its normal sprite, or null.</param>
public record SpeciesForm(string Identifier, string Name, string? FormName, bool Mega, string? SpriteUrl);
