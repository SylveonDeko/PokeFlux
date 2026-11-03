using EeveeCore.Database.Models.Mongo.Discord;
using EeveeCore.Database.Models.Mongo.Game;
using EeveeCore.Database.Models.Mongo.Pokemon;
using MongoDB.Driver;
using Pokemon_Type = EeveeCore.Database.Models.Mongo.Pokemon.Type;
using Guild = EeveeCore.Database.Models.Mongo.Discord.Guild;
using User = EeveeCore.Database.Models.Mongo.Discord.User;

namespace EeveeCore.Services.Impl;

/// <summary>
///     Provides access to MongoDB collections used throughout the application.
///     Organizes collections into Pokemon data, game data, and Discord data categories.
/// </summary>
public interface IMongoService
{
    // Pokemon Data
    /// <summary>
    ///     Collection of Pokemon abilities and their effects.
    /// </summary>
    IMongoCollection<Ability> Abilities { get; }

    /// <summary>
    ///     Collection of Pokemon forms and variants.
    /// </summary>
    IMongoCollection<Form> Forms { get; }

    /// <summary>
    ///     Collection of Pokemon moves and their effects.
    /// </summary>
    IMongoCollection<Move> Moves { get; }

    /// <summary>
    ///     Collection of Pokemon natures and their stat effects.
    /// </summary>
    IMongoCollection<Nature> Natures { get; }

    /// <summary>
    ///     Collection of Pokemon species data.
    /// </summary>
    IMongoCollection<PokemonFile> PFile { get; }

    /// <summary>
    ///     Collection mapping Pokemon to their possible abilities.
    /// </summary>
    IMongoCollection<PokemonAbility> PokeAbilities { get; }

    /// <summary>
    ///     Collection mapping Pokemon to their learnable moves.
    /// </summary>
    IMongoCollection<PokemonMoves> PokemonMoves { get; }

    /// <summary>
    ///     Collection of Pokemon base stats.
    /// </summary>
    IMongoCollection<PokemonStats> PokemonStats { get; }

    /// <summary>
    ///     Collection mapping Pokemon to their types.
    /// </summary>
    IMongoCollection<PokemonTypes> PokemonTypes { get; }

    /// <summary>
    ///     Collection of stat types and their properties.
    /// </summary>
    IMongoCollection<StatType> StatTypes { get; }

    /// <summary>
    ///     Collection of Pokemon types.
    /// </summary>
    IMongoCollection<Pokemon_Type> Types { get; }

    /// <summary>
    ///     Collection of type effectiveness multipliers.
    /// </summary>
    IMongoCollection<TypeEffectiveness> TypeEffectiveness { get; }

    // Game Data
    /// <summary>
    ///     Collection of booster packs and their contents.
    /// </summary>
    IMongoCollection<Booster> Boosters { get; }

    /// <summary>
    ///     Collection of currently available radiant Pokemon.
    /// </summary>
    IMongoCollection<CurrentRadiant> CurrentRadiants { get; }

    /// <summary>
    ///     Collection of egg groups for breeding.
    /// </summary>
    IMongoCollection<EggGroup> EggGroups { get; }

    /// <summary>
    ///     Collection of detailed information about egg groups.
    /// </summary>
    IMongoCollection<EggGroupInfo> EggGroupsInfo { get; }

    /// <summary>
    ///     Collection of Pokemon evolution data.
    /// </summary>
    IMongoCollection<Evolution> Evolution { get; }

    /// <summary>
    ///     Collection of search filters for Pokemon.
    /// </summary>
    IMongoCollection<Filter> Filters { get; }

    /// <summary>
    ///     Collection of gift items and their properties.
    /// </summary>
    IMongoCollection<Gift> Gifts { get; }

    /// <summary>
    ///     Collection of gym data for battles.
    /// </summary>
    IMongoCollection<Gym> Gyms { get; }

    /// <summary>
    ///     Collection of game items and their effects.
    /// </summary>
    IMongoCollection<Item> Items { get; }

    /// <summary>
    ///     Collection of level-up data and experience requirements.
    /// </summary>
    IMongoCollection<Levels> Levels { get; }

    /// <summary>
    ///     Collection of achievement milestones.
    /// </summary>
    IMongoCollection<Milestone> Milestones { get; }

    /// <summary>
    ///     Collection of missions for users to complete.
    /// </summary>
    IMongoCollection<Mission> Missions { get; }

    /// <summary>
    ///     Collection of monthly event data.
    /// </summary>
    IMongoCollection<Month> Months { get; }

    /// <summary>
    ///     Collection of placeholder data for radiant Pokemon.
    /// </summary>
    IMongoCollection<RadiantPlaceholder> RadiantPlaceholders { get; }

    /// <summary>
    ///     Collection of items available in the shop.
    /// </summary>
    IMongoCollection<ShopItem> Shop { get; }

    /// <summary>
    ///     Collection of user progress data for the game.
    /// </summary>
    IMongoCollection<UserProgress> UserProgress { get; }

    // Discord Data
    /// <summary>
    ///     Collection of Discord guild configurations.
    /// </summary>
    IMongoCollection<Guild> Guilds { get; }

    /// <summary>
    ///     Collection of Discord user data.
    /// </summary>
    IMongoCollection<User> Users { get; }
}