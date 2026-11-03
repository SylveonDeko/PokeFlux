using EeveeCore.Database.Models.Mongo.Discord;
using EeveeCore.Database.Models.Mongo.Game;
using EeveeCore.Database.Models.Mongo.Pokemon;
using MongoDB.Driver;
using Pokemon_Type = EeveeCore.Database.Models.Mongo.Pokemon.Type;
using Guild = EeveeCore.Database.Models.Mongo.Discord.Guild;
using User = EeveeCore.Database.Models.Mongo.Discord.User;

namespace EeveeCore.Services.Impl;

/// <summary>
///     Implementation of the IMongoService interface.
///     Provides access to MongoDB collections used throughout the application.
/// </summary>
/// <param name="client">The MongoDB client used to access the database.</param>
/// <param name="credentials">The bot credentials, whose Mongo connection string may name the database.</param>
public class MongoService(IMongoClient client, BotCredentials credentials) : IMongoService
{
    /// <summary>
    ///     The MongoDB database containing all collections: the one named in the connection string, or pokemon.
    /// </summary>
    private readonly IMongoDatabase _database =
        client.GetDatabase(MongoUrl.Create(credentials.MongoConfig.ConnectionString).DatabaseName ?? "pokemon");

    // Pokemon Data
    /// <inheritdoc />
    public IMongoCollection<Ability> Abilities => _database.GetCollection<Ability>("abilities");

    /// <inheritdoc />
    public IMongoCollection<Form> Forms => _database.GetCollection<Form>("forms");

    /// <inheritdoc />
    public IMongoCollection<Move> Moves => _database.GetCollection<Move>("moves");

    /// <inheritdoc />
    public IMongoCollection<Nature> Natures => _database.GetCollection<Nature>("natures");

    /// <inheritdoc />
    public IMongoCollection<PokemonFile> PFile => _database.GetCollection<PokemonFile>("pfile");

    /// <inheritdoc />
    public IMongoCollection<PokemonAbility> PokeAbilities => _database.GetCollection<PokemonAbility>("poke_abilities");

    /// <inheritdoc />
    public IMongoCollection<PokemonMoves> PokemonMoves => _database.GetCollection<PokemonMoves>("pokemon_moves");

    /// <inheritdoc />
    public IMongoCollection<PokemonStats> PokemonStats => _database.GetCollection<PokemonStats>("pokemon_stats");

    /// <inheritdoc />
    public IMongoCollection<PokemonTypes> PokemonTypes => _database.GetCollection<PokemonTypes>("ptypes");

    /// <inheritdoc />
    public IMongoCollection<StatType> StatTypes => _database.GetCollection<StatType>("stat_types");

    /// <inheritdoc />
    public IMongoCollection<Pokemon_Type> Types => _database.GetCollection<Pokemon_Type>("types");

    /// <inheritdoc />
    public IMongoCollection<TypeEffectiveness> TypeEffectiveness =>
        _database.GetCollection<TypeEffectiveness>("type_effectiveness");

    // Game Data
    /// <inheritdoc />
    public IMongoCollection<Booster> Boosters => _database.GetCollection<Booster>("boosters");

    /// <inheritdoc />
    public IMongoCollection<CurrentRadiant> CurrentRadiants =>
        _database.GetCollection<CurrentRadiant>("current_radiants");

    /// <inheritdoc />
    public IMongoCollection<EggGroup> EggGroups => _database.GetCollection<EggGroup>("egg_groups");

    /// <inheritdoc />
    public IMongoCollection<EggGroupInfo> EggGroupsInfo => _database.GetCollection<EggGroupInfo>("egg_groups_info");

    /// <inheritdoc />
    public IMongoCollection<Evolution> Evolution => _database.GetCollection<Evolution>("evofile");

    /// <inheritdoc />
    public IMongoCollection<Filter> Filters => _database.GetCollection<Filter>("filters");

    /// <inheritdoc />
    public IMongoCollection<Gift> Gifts => _database.GetCollection<Gift>("gifts");

    /// <inheritdoc />
    public IMongoCollection<Gym> Gyms => _database.GetCollection<Gym>("gyms");

    /// <inheritdoc />
    public IMongoCollection<Item> Items => _database.GetCollection<Item>("items");

    /// <inheritdoc />
    public IMongoCollection<Levels> Levels => _database.GetCollection<Levels>("levels");

    /// <inheritdoc />
    public IMongoCollection<Milestone> Milestones => _database.GetCollection<Milestone>("milestones");

    /// <inheritdoc />
    public IMongoCollection<Mission> Missions => _database.GetCollection<Mission>("missions2");

    /// <inheritdoc />
    public IMongoCollection<Month> Months => _database.GetCollection<Month>("month");

    /// <inheritdoc />
    public IMongoCollection<RadiantPlaceholder> RadiantPlaceholders =>
        _database.GetCollection<RadiantPlaceholder>("radiant_placeholder_pokes");

    /// <inheritdoc />
    public IMongoCollection<ShopItem> Shop => _database.GetCollection<ShopItem>("shop");

    /// <inheritdoc />
    public IMongoCollection<UserProgress> UserProgress => _database.GetCollection<UserProgress>("user_progress");

    // Discord Data
    /// <inheritdoc />
    public IMongoCollection<Guild> Guilds => _database.GetCollection<Guild>("guilds");

    /// <inheritdoc />
    public IMongoCollection<User> Users => _database.GetCollection<User>("users");
}