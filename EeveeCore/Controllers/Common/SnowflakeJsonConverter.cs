using System.Text.Json;
using System.Text.Json.Serialization;

namespace EeveeCore.Controllers.Common;

/// <summary>
///     Writes snowflakes and Pokémon ids as JSON strings, and reads them from strings or numbers.
/// </summary>
public sealed class SnowflakeJsonConverter : JsonConverter<ulong>
{
    /// <inheritdoc />
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetUInt64();

        if (reader.TokenType == JsonTokenType.String && ulong.TryParse(reader.GetString(), out var value))
            return value;

        throw new JsonException("Expected an id as a string or number.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
