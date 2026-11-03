namespace EeveeCore.Controllers.Models;

/// <summary>
///     A page of items.
/// </summary>
/// <param name="Items">The items on this page.</param>
/// <param name="Total">How many items there are in all.</param>
/// <param name="Page">The page number, from 1.</param>
/// <param name="PageSize">Items per page.</param>
/// <typeparam name="T">The item type.</typeparam>
public record AdminPage<T>(List<T> Items, int Total, int Page, int PageSize);

/// <summary>
///     A user's ID with their trainer nickname.
/// </summary>
/// <param name="Id">The user's Fluxer ID.</param>
/// <param name="Name">The trainer nickname, or null.</param>
public record AdminUserRef(ulong Id, string? Name);

/// <summary>
///     Fraud detection counts over a period.
/// </summary>
/// <param name="Days">Days looked back.</param>
/// <param name="TotalDetections">Every detection.</param>
/// <param name="BlockedTrades">Detections that blocked the trade.</param>
/// <param name="HighRisk">Detections with a risk score of 0.6 or more.</param>
/// <param name="MediumRisk">Detections from 0.4 up to 0.6.</param>
/// <param name="LowRisk">Detections below 0.4.</param>
/// <param name="AltAccountFlags">Alt account trading detections.</param>
/// <param name="RmtFlags">Real money trading detections.</param>
/// <param name="NewbieExploitFlags">Newbie exploitation detections.</param>
public record AdminFraudStats(
    int Days,
    int TotalDetections,
    int BlockedTrades,
    int HighRisk,
    int MediumRisk,
    int LowRisk,
    int AltAccountFlags,
    int RmtFlags,
    int NewbieExploitFlags);

/// <summary>
///     A fraud detection in a list.
/// </summary>
/// <param name="Id">The detection ID.</param>
/// <param name="Primary">The user the detection is about.</param>
/// <param name="Secondary">The other user of the trade, or null.</param>
/// <param name="RiskScore">The risk score, from 0 to 1.</param>
/// <param name="FraudType">The fraud type, such as <c>AltAccountTrading</c>.</param>
/// <param name="InvestigationStatus">The status, such as <c>Pending</c>.</param>
/// <param name="TradeBlocked">Whether the trade was blocked.</param>
/// <param name="DetectedAt">When the detection was made.</param>
public record AdminDetectionSummary(
    int Id,
    AdminUserRef Primary,
    AdminUserRef? Secondary,
    double RiskScore,
    string FraudType,
    string InvestigationStatus,
    bool TradeBlocked,
    DateTime DetectedAt);

/// <summary>
///     Recent high-risk detections.
/// </summary>
/// <param name="Hours">Hours looked back.</param>
/// <param name="MinRiskScore">The lowest risk score listed.</param>
/// <param name="Items">The detections on this page, riskiest first.</param>
/// <param name="Total">How many detections there are in all.</param>
/// <param name="Page">The page number, from 1.</param>
/// <param name="PageSize">Detections per page.</param>
public record AdminDetectionList(
    int Hours,
    double MinRiskScore,
    List<AdminDetectionSummary> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
///     The trade analysis behind a detection.
/// </summary>
/// <param name="SenderTotalValue">The value the sender gave.</param>
/// <param name="ReceiverTotalValue">The value the receiver gave.</param>
/// <param name="ValueRatio">How many times larger one side was.</param>
/// <param name="ValueImbalanceScore">The value imbalance score, from 0 to 1.</param>
/// <param name="Flags">The flags raised, such as <c>Alt Account</c>.</param>
public record AdminTradeAnalysis(
    decimal SenderTotalValue,
    decimal ReceiverTotalValue,
    double ValueRatio,
    double ValueImbalanceScore,
    List<string> Flags);

/// <summary>
///     A fraud detection case.
/// </summary>
/// <param name="Summary">The detection's list fields.</param>
/// <param name="ConfidenceLevel">The confidence, from 0 to 1.</param>
/// <param name="AutomatedAction">What the bot did, such as <c>BlockTrade</c>.</param>
/// <param name="TriggeredRules">The rules that fired, as stored.</param>
/// <param name="AdminNotes">Notes left by staff, or null.</param>
/// <param name="Analysis">The trade analysis, or null.</param>
public record AdminDetectionDetail(
    AdminDetectionSummary Summary,
    double ConfidenceLevel,
    string AutomatedAction,
    string TriggeredRules,
    string? AdminNotes,
    AdminTradeAnalysis? Analysis);

/// <summary>
///     The trade relationship between two users.
/// </summary>
/// <param name="User1">The user with the lower ID.</param>
/// <param name="User2">The user with the higher ID.</param>
/// <param name="TotalTrades">Trades between them.</param>
/// <param name="FirstTrade">When they first traded.</param>
/// <param name="LastTrade">When they last traded.</param>
/// <param name="TradesPerDay">The trading frequency score.</param>
/// <param name="User1GivenValue">The value <paramref name="User1" /> gave.</param>
/// <param name="User2GivenValue">The value <paramref name="User2" /> gave.</param>
/// <param name="ValueImbalanceRatio">How many times larger one side was.</param>
/// <param name="BalancedTrades">Trades of roughly equal value.</param>
/// <param name="RiskScore">The relationship risk score, from 0 to 1.</param>
/// <param name="AccountAgeDifferenceDays">The difference between the account ages.</param>
/// <param name="SuspiciousTiming">Whether the trade timing looks scripted.</param>
/// <param name="Whitelisted">Whether staff whitelisted them.</param>
/// <param name="Flags">The flags raised, such as <c>Potential Alts</c>.</param>
/// <param name="AdminNotes">Notes left by staff, or null.</param>
/// <param name="LastUpdated">When the relationship was last updated.</param>
public record AdminRelationship(
    AdminUserRef User1,
    AdminUserRef User2,
    int TotalTrades,
    DateTime FirstTrade,
    DateTime LastTrade,
    double TradesPerDay,
    decimal User1GivenValue,
    decimal User2GivenValue,
    double ValueImbalanceRatio,
    int BalancedTrades,
    double RiskScore,
    int AccountAgeDifferenceDays,
    bool SuspiciousTiming,
    bool Whitelisted,
    List<string> Flags,
    string? AdminNotes,
    DateTime LastUpdated);

/// <summary>
///     Two users and a reason, for whitelisting their relationship.
/// </summary>
/// <param name="User1">One user.</param>
/// <param name="User2">The other user.</param>
/// <param name="Reason">Why staff whitelisted them.</param>
public record AdminWhitelistRequest(ulong User1, ulong User2, string? Reason);

/// <summary>
///     A trade ban.
/// </summary>
/// <param name="Reason">Why the user is banned.</param>
/// <param name="MarketBan">Whether to also ban them from the market. True by default, like the command.</param>
public record AdminTradeBanRequest(string? Reason, bool MarketBan = true);

/// <summary>
///     A reason for a staff action.
/// </summary>
/// <param name="Reason">Why staff took the action.</param>
public record AdminReasonRequest(string? Reason);

/// <summary>
///     A user found by a search.
/// </summary>
/// <param name="Id">The user's Fluxer ID.</param>
/// <param name="Name">The trainer nickname, or null.</param>
/// <param name="StaffRank">The staff rank.</param>
/// <param name="TradeBanned">Whether they are trade banned.</param>
/// <param name="MarketBanned">Whether they are market banned.</param>
public record AdminUserResult(ulong Id, string? Name, string StaffRank, bool TradeBanned, bool MarketBanned);

/// <summary>
///     A ban and why it was given.
/// </summary>
/// <param name="Active">Whether the ban is in place.</param>
/// <param name="Reason">The reason, or null.</param>
/// <param name="Date">When it was last changed, or null.</param>
public record AdminBanStatus(bool Active, string? Reason, DateTime? Date);

/// <summary>
///     A trade or gift from the trade log.
/// </summary>
/// <param name="Id">The log ID.</param>
/// <param name="Command">What made the entry, such as <c>gift_credits</c>.</param>
/// <param name="Time">When it happened, or null.</param>
/// <param name="Sender">The user who sent.</param>
/// <param name="Receiver">The user who received.</param>
/// <param name="SenderCredits">Credits the sender gave.</param>
/// <param name="ReceiverCredits">Credits the receiver gave.</param>
/// <param name="SenderRedeems">Redeems the sender gave.</param>
/// <param name="ReceiverRedeems">Redeems the receiver gave.</param>
/// <param name="SenderPokemon">How many Pokémon the sender gave.</param>
/// <param name="ReceiverPokemon">How many Pokémon the receiver gave.</param>
/// <param name="SenderTokens">Type tokens the sender gave, by type name.</param>
/// <param name="ReceiverTokens">Type tokens the receiver gave, by type name.</param>
public record AdminTradeLogEntry(
    int Id,
    string Command,
    DateTime? Time,
    AdminUserRef Sender,
    AdminUserRef Receiver,
    long SenderCredits,
    long ReceiverCredits,
    long SenderRedeems,
    long ReceiverRedeems,
    int SenderPokemon,
    int ReceiverPokemon,
    Dictionary<string, int> SenderTokens,
    Dictionary<string, int> ReceiverTokens);

/// <summary>
///     A market listing as staff see it.
/// </summary>
/// <param name="ListingId">The listing ID.</param>
/// <param name="Price">The price in credits.</param>
/// <param name="Status"><c>active</c> while listed, then <c>sold</c> or <c>removed</c>.</param>
/// <param name="Seller">The seller.</param>
/// <param name="Buyer">The buyer, or null unless sold.</param>
/// <param name="ListedAt">When it was listed.</param>
/// <param name="Pokemon">The Pokémon, or null when it no longer exists.</param>
public record AdminListing(
    ulong ListingId,
    int Price,
    string Status,
    AdminUserRef Seller,
    AdminUserRef? Buyer,
    DateTime ListedAt,
    PokemonCard? Pokemon);

/// <summary>
///     What staff see about a user.
/// </summary>
/// <param name="Id">The user's Fluxer ID.</param>
/// <param name="Name">The trainer nickname, or null.</param>
/// <param name="Credits">The credit balance.</param>
/// <param name="Redeems">The redeem balance.</param>
/// <param name="PokemonCount">How many Pokémon they own.</param>
/// <param name="StaffRank">The staff rank.</param>
/// <param name="Region">The region.</param>
/// <param name="TradeLocked">Whether the trade lock flag is set.</param>
/// <param name="BotBanned">Whether they are banned from the bot.</param>
/// <param name="TradeBan">The trade ban.</param>
/// <param name="MarketBan">The market ban.</param>
/// <param name="RecentTrades">The newest trade log entries, gifts included.</param>
/// <param name="RecentListings">The newest listings they made.</param>
/// <param name="RecentPurchases">The newest listings they bought.</param>
/// <param name="Detections">The newest fraud detections involving them.</param>
public record AdminUserDetail(
    ulong Id,
    string? Name,
    long Credits,
    long Redeems,
    int PokemonCount,
    string StaffRank,
    string Region,
    bool TradeLocked,
    bool BotBanned,
    AdminBanStatus TradeBan,
    AdminBanStatus MarketBan,
    List<AdminTradeLogEntry> RecentTrades,
    List<AdminListing> RecentListings,
    List<AdminListing> RecentPurchases,
    List<AdminDetectionSummary> Detections);

/// <summary>
///     A staff action taken through the dashboard.
/// </summary>
/// <param name="Id">The entry ID.</param>
/// <param name="Staff">The staff member, with the name they had then.</param>
/// <param name="Action">The action, such as <c>tradeban</c>.</param>
/// <param name="Target">The user acted on, or null.</param>
/// <param name="Details">More about the action, or null.</param>
/// <param name="Reason">The reason given.</param>
/// <param name="CreatedAt">When it happened.</param>
public record AdminAuditEntry(
    long Id,
    AdminUserRef Staff,
    string Action,
    AdminUserRef? Target,
    string? Details,
    string Reason,
    DateTime CreatedAt);

/// <summary>
///     Whether spawn storm mode is on.
/// </summary>
/// <param name="Enabled">Whether a Pokémon spawns on every message.</param>
/// <param name="Message">What changed, after a toggle.</param>
public record AdminSpawnStorm(bool Enabled, string? Message = null);

/// <summary>
///     A server's spawn debug output.
/// </summary>
/// <param name="Lines">The lines the debug command prints.</param>
public record AdminSpawnDebug(List<string> Lines);
