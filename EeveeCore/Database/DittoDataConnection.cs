using LinqToDB;
using LinqToDB.Data;
using EeveeCore.Database.Linq.Models.Art;
using EeveeCore.Database.Linq.Models.Bot;
using EeveeCore.Database.Linq.Models.Game;
using EeveeCore.Database.Linq.Models.Pokemon;
using User = EeveeCore.Database.Linq.Models.Bot.User;

namespace EeveeCore.Database;

/// <summary>
///     Represents the main LinqToDB data connection for the EeveeCore Pokémon bot system.
///     This class provides access to all database entities through ITable properties,
///     organized into logical categories for Pokémon, Bot, Art, and Game models.
/// </summary>
public class DittoDataConnection : DataConnection
{
    /// <summary>
    ///     Initializes a new instance of the DittoDataConnection using the specified connection string.
    /// </summary>
    public DittoDataConnection(string connectionString) 
        : base(ConfigureDataOptions(connectionString))
    {
        // Configure connection options
        (this as IDataContext).CloseAfterUse = true;
    }

    /// <summary>
    ///     Initializes a new instance of the DittoDataConnection using DataOptions.
    /// </summary>
    /// <param name="options">The data options for configuration.</param>
    public DittoDataConnection(DataOptions options) 
        : base(options)
    {
        (this as IDataContext).CloseAfterUse = true;
    }

    /// <summary>
    ///     Configures the DataOptions with PostgreSQL provider
    /// </summary>
    private static DataOptions ConfigureDataOptions(string connectionString)
    {
        return new DataOptions()
            .UsePostgreSQL(connectionString);
    }

    #region Pokemon Tables

    /// <summary>
    ///     Represents player achievements in the game
    /// </summary>
    public ITable<Achievement> Achievements => this.GetTable<Achievement>();

    /// <summary>
    ///     Represents deceased radiant Pokémon
    /// </summary>
    public ITable<DeadRadiant> DeadRadiants => this.GetTable<DeadRadiant>();

    /// <summary>
    ///     Represents deceased regular Pokémon
    /// </summary>
    public ITable<DeadPokemon> DeadPokemon => this.GetTable<DeadPokemon>();

    /// <summary>
    ///     Represents Pokémon eggs in the game
    /// </summary>
    public ITable<Eggs> Eggs => this.GetTable<Eggs>();

    /// <summary>
    ///     Represents the egg hatchery system
    /// </summary>
    public ITable<EggHatchery> EggHatcheries => this.GetTable<EggHatchery>();

    /// <summary>
    ///     Represents Pokémon in the hatchery
    /// </summary>
    public ITable<HatcheryPokemon> HatcheryPokemon => this.GetTable<HatcheryPokemon>();

    /// <summary>
    ///     Represents honey items used to attract Pokémon
    /// </summary>
    public ITable<Honey> Honey => this.GetTable<Honey>();

    /// <summary>
    ///     Represents mother Pokémon used for breeding
    /// </summary>
    public ITable<Mother> Mothers => this.GetTable<Mother>();

    /// <summary>
    ///     Represents individual Pokémon parties
    /// </summary>
    public ITable<Party> Parties => this.GetTable<Party>();

    /// <summary>
    ///     Represents Pokémon in the game
    /// </summary>
    public ITable<Pokemon> UserPokemon => this.GetTable<Pokemon>();

    /// <summary>
    ///     Represents Pokémon statistics
    /// </summary>
    public ITable<PokemonStats> PokemonStats => this.GetTable<PokemonStats>();

    /// <summary>
    ///     Represents total Pokémon counts and statistics
    /// </summary>
    public ITable<PokemonTotal> PokemonTotals => this.GetTable<PokemonTotal>();

    /// <summary>
    ///     Gets the user-Pokémon ownership relationships.
    /// </summary>
    public ITable<UserPokemonOwnership> UserPokemonOwnerships => this.GetTable<UserPokemonOwnership>();

    /// <summary>
    ///     Gets the invalid Pokémon references that were detected during migration.
    /// </summary>
    public ITable<InvalidPokemonReference> InvalidPokemonReferences => this.GetTable<InvalidPokemonReference>();

    /// <summary>
    ///     Gets the milestone progress tracking for achievements.
    /// </summary>
    public ITable<MilestoneProgress> MilestoneProgress => this.GetTable<MilestoneProgress>();

    /// <summary>
    ///     Gets the user loyalty and daily streak tracking.
    /// </summary>
    public ITable<UserLoyalty> UserLoyalty => this.GetTable<UserLoyalty>();

    #endregion

    #region Bot Tables

    /// <summary>
    ///     Represents bot announcements
    /// </summary>
    public ITable<Announce> Announcements => this.GetTable<Announce>();

    /// <summary>
    ///     Represents banned users from the bot
    /// </summary>
    public ITable<BotBan> BotBans => this.GetTable<BotBan>();

    /// <summary>
    ///     Represents staff actions taken through the dashboard
    /// </summary>
    public ITable<StaffAuditEntry> StaffAuditLog => this.GetTable<StaffAuditEntry>();

    /// <summary>
    ///     Represents calendar events
    /// </summary>
    public ITable<Cal> Calendar => this.GetTable<Cal>();

    /// <summary>
    ///     Represents community information
    /// </summary>
    public ITable<Community> Communities => this.GetTable<Community>();

    /// <summary>
    ///     Represents disabled channels
    /// </summary>
    public ITable<DisabledChannel> DisabledChannels => this.GetTable<DisabledChannel>();

    /// <summary>
    ///     Represents EeveeCore-specific donations
    /// </summary>
    public ITable<EeveeCoreDonation> EeveeCoreDonations => this.GetTable<EeveeCoreDonation>();

    /// <summary>
    ///     Represents general donations
    /// </summary>
    public ITable<Donation> Donations => this.GetTable<Donation>();

    /// <summary>
    ///     Represents new user registrations
    /// </summary>
    public ITable<NewUser> NewUsers => this.GetTable<NewUser>();

    /// <summary>
    ///     Represents server configurations
    /// </summary>
    public ITable<Server> Servers => this.GetTable<Server>();

    /// <summary>
    ///     Represents sky-related logs
    /// </summary>
    public ITable<SkyLog> SkyLogs => this.GetTable<SkyLog>();

    /// <summary>
    ///     Represents authentication tokens
    /// </summary>
    public ITable<Token> Tokens => this.GetTable<Token>();

    /// <summary>
    ///     Represents bot updates
    /// </summary>
    public ITable<Update> Updates => this.GetTable<Update>();

    /// <summary>
    ///     Represents bot users
    /// </summary>
    public ITable<User> Users => this.GetTable<User>();

    /// <summary>
    ///     Represents voucher requests
    /// </summary>
    public ITable<VoucherRequest> VoucherRequests => this.GetTable<VoucherRequest>();

    /// <summary>
    ///     Represents active users
    /// </summary>
    public ITable<ActiveUser> ActiveUsers => this.GetTable<ActiveUser>();

    /// <summary>
    ///     Represents inactive users
    /// </summary>
    public ITable<InactiveUser> InactiveUsers => this.GetTable<InactiveUser>();

    /// <summary>
    ///     Represents user-defined filter groups for Pokemon collections
    /// </summary>
    public ITable<UserFilterGroup> UserFilterGroups => this.GetTable<UserFilterGroup>();

    /// <summary>
    ///     Represents individual filter criteria within user filter groups
    /// </summary>
    public ITable<UserFilterCriteria> UserFilterCriteria => this.GetTable<UserFilterCriteria>();

    #endregion

    #region Art Tables

    /// <summary>
    ///     Represents artists
    /// </summary>
    public ITable<Artist> Artists => this.GetTable<Artist>();

    /// <summary>
    ///     Represents artist consent records
    /// </summary>
    public ITable<ArtistConsent> ArtistConsents => this.GetTable<ArtistConsent>();

    #endregion

    #region Game Tables

    /// <summary>
    ///     Represents the chest store
    /// </summary>
    public ITable<ChestStore> ChestStore => this.GetTable<ChestStore>();

    /// <summary>
    ///     Represents current events
    /// </summary>
    public ITable<CurrentEvent> CurrentEvents => this.GetTable<CurrentEvent>();

    /// <summary>
    ///     Represents gifts
    /// </summary>
    public ITable<Gift> Gifts => this.GetTable<Gift>();

    /// <summary>
    ///     Represents gyms
    /// </summary>
    public ITable<Gym> Gyms => this.GetTable<Gym>();

    /// <summary>
    ///     Represents gym activity logs
    /// </summary>
    public ITable<GymLog> GymLogs => this.GetTable<GymLog>();

    /// <summary>
    ///     Represents Halloween event data
    /// </summary>
    public ITable<Halloween> Halloween => this.GetTable<Halloween>();

    /// <summary>
    ///     Represents leveling system data
    /// </summary>
    public ITable<LevelingData> LevelingData => this.GetTable<LevelingData>();

    /// <summary>
    ///     Represents the marketplace
    /// </summary>
    public ITable<Market> Market => this.GetTable<Market>();

    /// <summary>
    ///     Represents the Patreon store
    /// </summary>
    public ITable<PatreonStore> PatreonStore => this.GetTable<PatreonStore>();

    /// <summary>
    ///     Represents the redeem store
    /// </summary>
    public ITable<RedeemStore> RedeemStore => this.GetTable<RedeemStore>();

    /// <summary>
    ///     Represents tournament teams
    /// </summary>
    public ITable<TournamentTeam> TournamentTeams => this.GetTable<TournamentTeam>();

    /// <summary>
    ///     Represents trade logs
    /// </summary>
    public ITable<TradeLog> TradeLogs => this.GetTable<TradeLog>();

    /// <summary>
    ///     Represents active Pokemon spawns in Discord channels
    /// </summary>
    public ITable<ActiveSpawn> ActiveSpawns => this.GetTable<ActiveSpawn>();

    /// <summary>
    ///     Represents suspicious trade analytics data
    /// </summary>
    public ITable<SuspiciousTradeAnalytics> SuspiciousTradeAnalytics => this.GetTable<SuspiciousTradeAnalytics>();

    /// <summary>
    ///     Represents user trade relationships for fraud detection
    /// </summary>
    public ITable<UserTradeRelationship> UserTradeRelationships => this.GetTable<UserTradeRelationship>();

    /// <summary>
    ///     Represents fraud detection incidents
    /// </summary>
    public ITable<TradeFraudDetection> TradeFraudDetections => this.GetTable<TradeFraudDetection>();

    #endregion
}