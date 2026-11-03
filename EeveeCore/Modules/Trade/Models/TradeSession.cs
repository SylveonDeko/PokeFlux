using System.Text.Json.Serialization;

namespace EeveeCore.Modules.Trade.Models;

/// <summary>
///     Represents the current status of a trade session.
/// </summary>
public enum TradeStatus
{
    /// <summary>Trade session is being set up</summary>
    Initializing,
    
    /// <summary>Users are adding items to the trade</summary>
    Active,
    
    /// <summary>Users are confirming the trade</summary>
    PendingConfirmation,
    
    /// <summary>Trade is being processed</summary>
    Processing,
    
    /// <summary>Trade completed successfully</summary>
    Completed,
    
    /// <summary>Trade was cancelled</summary>
    Cancelled,
    
    /// <summary>Trade failed due to an error</summary>
    Failed,
    
    /// <summary>Trade timed out</summary>
    TimedOut
}

/// <summary>
///     Represents a complete trade session between two users.
///     Manages the state, items, and flow of a trade from start to completion.
/// </summary>
public class TradeSession
{
    /// <summary>
    ///     Gets or sets the unique identifier for this trade session.
    /// </summary>
    public Guid SessionId { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     Gets or sets the Discord user ID of the first trader.
    /// </summary>
    public ulong Player1Id { get; set; }

    /// <summary>
    ///     Gets or sets the Discord user ID of the second trader.
    /// </summary>
    public ulong Player2Id { get; set; }

    /// <summary>
    ///     Gets or sets the current status of the trade session.
    /// </summary>
    public TradeStatus Status { get; set; } = TradeStatus.Initializing;

    /// <summary>
    ///     Gets or sets the list of items being traded.
    /// </summary>
    public List<TradeEntry> TradeEntries { get; set; } = new();

    /// <summary>
    ///     Gets or sets the timestamp when the trade session was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Gets or sets the timestamp when the trade session was last updated.
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Gets or sets the timestamp when the trade session will expire.
    /// </summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(6);

    /// <summary>
    ///     Gets or sets the message that shows this trade session.
    /// </summary>
    [JsonIgnore]
    public Message? TradeMessage { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether Player1 has confirmed the trade.
    /// </summary>
    public bool Player1Confirmed { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether Player2 has confirmed the trade.
    /// </summary>
    public bool Player2Confirmed { get; set; }

    /// <summary>
    ///     Gets or sets whether the trade is currently attempting to execute.
    /// </summary>
    public bool IsAttemptingTrade { get; set; }

    /// <summary>
    ///     Gets or sets the channel ID where the trade is taking place.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    ///     Gets or sets the guild ID where the trade is taking place.
    /// </summary>
    public ulong GuildId { get; set; }

    /// <summary>
    ///     Gets all trade entries offered by a specific user.
    /// </summary>
    /// <param name="userId">The user ID to filter by.</param>
    /// <returns>A collection of trade entries offered by the specified user.</returns>
    public IEnumerable<TradeEntry> GetEntriesBy(ulong userId)
    {
        return TradeEntries.Where(e => e.OfferedBy == userId);
    }

    /// <summary>
    ///     Gets all Pokemon being offered by a specific user.
    /// </summary>
    /// <param name="userId">The user ID to filter by.</param>
    /// <returns>A collection of Pokemon trade entries by the specified user.</returns>
    public IEnumerable<TradeEntry> GetPokemonBy(ulong userId)
    {
        return TradeEntries.Where(e => e.OfferedBy == userId && e.ItemType == TradeItemType.Pokemon);
    }

    /// <summary>
    ///     Gets the total credits being offered by a specific user.
    /// </summary>
    /// <param name="userId">The user ID to get credits for.</param>
    /// <returns>The total number of credits being offered by the user.</returns>
    public ulong GetCreditsBy(ulong userId)
    {
        return (ulong)TradeEntries
            .Where(e => e.OfferedBy == userId && e.ItemType == TradeItemType.Credits)
            .Sum(e => (long)e.Credits);
    }

    /// <summary>
    ///     Gets all tokens being offered by a specific user, grouped by type.
    /// </summary>
    /// <param name="userId">The user ID to get tokens for.</param>
    /// <returns>A dictionary mapping token types to quantities.</returns>
    public Dictionary<TokenType, int> GetTokensBy(ulong userId)
    {
        return TradeEntries
            .Where(e => e.OfferedBy == userId && e is { ItemType: TradeItemType.Tokens, TokenType: not null })
            .GroupBy(e => e.TokenType!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.TokenCount));
    }

    /// <summary>
    ///     Adds a trade entry to the session and updates the last modified time.
    /// </summary>
    /// <param name="entry">The trade entry to add.</param>
    public void AddEntry(TradeEntry entry)
    {
        TradeEntries.Add(entry);
        LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    ///     Removes a trade entry from the session and updates the last modified time.
    /// </summary>
    /// <param name="entryId">The ID of the trade entry to remove.</param>
    /// <returns>True if the entry was found and removed, false otherwise.</returns>
    public bool RemoveEntry(Guid entryId)
    {
        var entry = TradeEntries.FirstOrDefault(e => e.Id == entryId);
        if (entry == null) return false;

        TradeEntries.Remove(entry);
        LastUpdated = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    ///     Removes all entries of a specific type offered by a user.
    /// </summary>
    /// <param name="userId">The user ID whose entries to remove.</param>
    /// <param name="itemType">The type of items to remove.</param>
    /// <returns>The number of entries removed.</returns>
    public int RemoveEntriesBy(ulong userId, TradeItemType itemType)
    {
        var toRemove = TradeEntries
            .Where(e => e.OfferedBy == userId && e.ItemType == itemType)
            .ToList();

        foreach (var entry in toRemove)
        {
            TradeEntries.Remove(entry);
        }

        if (toRemove.Any())
        {
            LastUpdated = DateTime.UtcNow;
        }

        return toRemove.Count;
    }

    /// <summary>
    ///     Checks if the trade session has expired.
    /// </summary>
    /// <returns>True if the session has expired, false otherwise.</returns>
    public bool IsExpired()
    {
        return DateTime.UtcNow > ExpiresAt;
    }

    /// <summary>
    ///     Checks if the trade session has any items to trade.
    /// </summary>
    /// <returns>True if there are items in the trade, false if empty.</returns>
    public bool HasItems()
    {
        return TradeEntries.Any();
    }

    /// <summary>
    ///     Checks if both players have confirmed the trade.
    /// </summary>
    /// <returns>True if both players have confirmed, false otherwise.</returns>
    public bool IsBothConfirmed()
    {
        return Player1Confirmed && Player2Confirmed;
    }

    /// <summary>
    ///     Resets the confirmation status for both players.
    /// </summary>
    public void ResetConfirmations()
    {
        Player1Confirmed = false;
        Player2Confirmed = false;
        LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    ///     Sets the confirmation status for a specific player.
    /// </summary>
    /// <param name="userId">The user ID of the player.</param>
    /// <param name="confirmed">The confirmation status to set.</param>
    public void SetPlayerConfirmation(ulong userId, bool confirmed)
    {
        if (userId == Player1Id)
        {
            Player1Confirmed = confirmed;
        }
        else if (userId == Player2Id)
        {
            Player2Confirmed = confirmed;
        }
        
        LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    ///     Gets the other player's ID given one player's ID.
    /// </summary>
    /// <param name="playerId">The known player's ID.</param>
    /// <returns>The other player's ID, or null if the provided ID doesn't match either player.</returns>
    public ulong? GetOtherPlayer(ulong playerId)
    {
        if (playerId == Player1Id) return Player2Id;
        if (playerId == Player2Id) return Player1Id;
        return null;
    }

    /// <summary>
    ///     Checks if a user ID is one of the participants in this trade.
    /// </summary>
    /// <param name="userId">The user ID to check.</param>
    /// <returns>True if the user is a participant, false otherwise.</returns>
    public bool IsParticipant(ulong userId)
    {
        return userId == Player1Id || userId == Player2Id;
    }
}