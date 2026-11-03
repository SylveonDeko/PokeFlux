using LinqToDB.Mapping;

namespace EeveeCore.Database.Linq.Models.Game;

/// <summary>
///     Represents a log entry for a trade between users in the EeveeCore Pokémon bot system.
///     This class records the details of trading transactions, including the items exchanged.
/// </summary>
[Table("trade_logs")]
public class TradeLog
{
    /// <summary>
    ///     Gets or sets the unique identifier for this trade.
    /// </summary>
    [PrimaryKey]
    [Identity]
    [Column("t_id")]
    public int TradeId { get; set; }

    /// <summary>
    ///     Gets or sets the command used to initiate the trade.
    /// </summary>
    [Column("command")]
    [NotNull]
    public string Command { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the date and time when the trade occurred.
    /// </summary>
    [Column("time")]
    public DateTime? Time { get; set; }

    #region Sender

    /// <summary>
    ///     Gets or sets the Discord user ID of the user who initiated the trade.
    /// </summary>
    [Column("sender")]
    [NotNull]
    public ulong SenderId { get; set; }

    /// <summary>
    ///     Internal storage for sender Pokemon as long[] for PostgreSQL compatibility
    /// </summary>
    [Column("sender_pokes", DbType = "bigint[]")]
    internal long[]? _senderPokemon { get; set; }

    /// <summary>
    ///     Gets or sets the array of Pokémon IDs that the sender traded away.
    /// </summary>
    [NotColumn]
    public ulong[]? SenderPokemon 
    { 
        get => _senderPokemon?.Select(x => (ulong)x).ToArray();
        set => _senderPokemon = value?.Select(x => (long)x).ToArray();
    }

    /// <summary>
    ///     Gets or sets the amount of MewCoins that the sender included in the trade.
    /// </summary>
    [Column("sender_credits")]
    public long? SenderCredits { get; set; }

    /// <summary>
    ///     Gets or sets the number of redeem tokens that the sender included in the trade.
    /// </summary>
    [Column("sender_redeems")]
    [NotNull]
    public ulong SenderRedeems { get; set; }

    /// <summary>
    ///     Gets or sets the type tokens the sender gave, as a JSON map of type name to count.
    /// </summary>
    [Column("sender_tokens", DbType = "jsonb")]
    public string? SenderTokens { get; set; }

    #endregion

    #region Receiver

    /// <summary>
    ///     Gets or sets the Discord user ID of the user who received the trade.
    /// </summary>
    [Column("receiver")]
    [NotNull]
    public ulong ReceiverId { get; set; }

    /// <summary>
    ///     Internal storage for receiver Pokemon as long[] for PostgreSQL compatibility
    /// </summary>
    [Column("receiver_pokes", DbType = "bigint[]")]
    internal long[]? _receiverPokemon { get; set; }

    /// <summary>
    ///     Gets or sets the array of Pokémon IDs that the receiver traded away.
    /// </summary>
    [NotColumn]
    public ulong[]? ReceiverPokemon 
    { 
        get => _receiverPokemon?.Select(x => (ulong)x).ToArray();
        set => _receiverPokemon = value?.Select(x => (long)x).ToArray();
    }

    /// <summary>
    ///     Gets or sets the amount of MewCoins that the receiver included in the trade.
    /// </summary>
    [Column("receiver_credits")]
    public long? ReceiverCredits { get; set; }

    /// <summary>
    ///     Gets or sets the number of redeem tokens that the receiver included in the trade.
    /// </summary>
    [Column("receiver_redeems")]
    [NotNull]
    public ulong ReceiverRedeems { get; set; }

    /// <summary>
    ///     Gets or sets the type tokens the receiver gave, as a JSON map of type name to count.
    /// </summary>
    [Column("receiver_tokens", DbType = "jsonb")]
    public string? ReceiverTokens { get; set; }

    #endregion
}