using System.Globalization;
using EeveeCore.Modules.Trade.Models;
using TokenType = EeveeCore.Modules.Trade.Models.TokenType;

namespace EeveeCore.Modules.Trade;

/// <summary>
///     Parses the arguments of trade and gift commands.
/// </summary>
internal static class TradeInput
{
    /// <summary>
    ///     Every token type, for error messages.
    /// </summary>
    public static string TokenTypeList => string.Join(", ", Enum.GetNames<TokenType>());

    /// <summary>
    ///     Parses an amount that may end in <c>k</c> for thousands or <c>m</c> for millions.
    /// </summary>
    /// <param name="input">The amount as typed.</param>
    /// <param name="amount">The parsed amount.</param>
    /// <returns>Whether the input was a valid amount.</returns>
    public static bool TryParseAmount(string input, out ulong amount)
    {
        amount = 0;
        var text = input.Trim().ToLowerInvariant().Replace(",", "").Replace("_", "").Replace(" ", "");
        var multiplier = 1m;
        if (text.EndsWith('k'))
        {
            multiplier = 1_000m;
            text = text[..^1];
        }
        else if (text.EndsWith('m'))
        {
            multiplier = 1_000_000m;
            text = text[..^1];
        }

        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0)
            return false;

        var total = decimal.Floor(value * multiplier);
        if (total > ulong.MaxValue)
            return false;

        amount = (ulong)total;
        return true;
    }

    /// <summary>
    ///     Parses Pokémon positions separated by spaces or commas, dropping duplicates.
    /// </summary>
    /// <param name="input">The positions as typed.</param>
    /// <returns>The positions in the order given.</returns>
    public static List<ulong> ParsePositions(string input)
    {
        var positions = new List<ulong>();
        foreach (var part in input.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (ulong.TryParse(part, out var position) && !positions.Contains(position))
                positions.Add(position);
        }

        return positions;
    }

    /// <summary>
    ///     Parses a token type by name, ignoring case.
    /// </summary>
    /// <param name="input">The type as typed.</param>
    /// <param name="tokenType">The parsed token type.</param>
    /// <returns>Whether the input named a token type.</returns>
    public static bool TryParseToken(string input, out TokenType tokenType) =>
        TokenTypeExtensions.TryParse(input.Trim(), out tokenType) && Enum.IsDefined(tokenType);

    /// <summary>
    ///     Replaces slash command mentions in a service message with the server prefix.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="prefix">The server prefix.</param>
    public static string WithPrefix(string? message, string prefix) =>
        (message ?? "").Replace("`/", $"`{prefix}");
}
