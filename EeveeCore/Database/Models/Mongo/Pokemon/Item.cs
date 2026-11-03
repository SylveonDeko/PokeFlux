using EeveeCore.Common.Mongo;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EeveeCore.Database.Models.Mongo.Pokemon;

/// <summary>
///     Represents an item in the game with its properties and effects.
/// </summary>
public class Item
{
    /// <summary>
    ///     The identifiers of the item categories that <see cref="CategoryId" /> refers to, from PokeAPI's item data.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> CategoryIdentifiers = new Dictionary<int, string>
    {
        [1] = "stat-boosts", [2] = "effort-drop", [3] = "medicine", [4] = "other", [5] = "in-a-pinch",
        [6] = "picky-healing", [7] = "type-protection", [8] = "baking-only", [9] = "collectibles",
        [10] = "evolution", [11] = "spelunking", [12] = "held-items", [13] = "choice", [14] = "effort-training",
        [15] = "bad-held-items", [16] = "training", [17] = "plates", [18] = "species-specific",
        [19] = "type-enhancement", [20] = "event-items", [21] = "gameplay", [22] = "plot-advancement",
        [23] = "unused", [24] = "loot", [25] = "all-mail", [26] = "vitamins", [27] = "healing",
        [28] = "pp-recovery", [29] = "revival", [30] = "status-cures", [32] = "mulch", [33] = "special-balls",
        [34] = "standard-balls", [35] = "dex-completion", [36] = "scarves", [37] = "all-machines",
        [38] = "flutes", [39] = "apricorn-balls", [40] = "apricorn-box", [41] = "data-cards", [42] = "jewels",
        [43] = "miracle-shooter", [44] = "mega-stones", [45] = "memories", [46] = "z-crystals"
    };

    /// <summary>
    ///     Gets or sets the MongoDB document identifier.
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    /// <summary>
    ///     Gets or sets the numeric identifier of the item.
    /// </summary>
    [BsonElement("id")]
    public int ItemId { get; set; }

    /// <summary>
    ///     Gets or sets the string identifier or name of the item.
    /// </summary>
    [BsonElement("identifier")]
    public string? Identifier { get; set; }

    /// <summary>
    ///     Gets or sets the category identifier this item belongs to.
    /// </summary>
    [BsonElement("category_id")]
    public int CategoryId { get; set; }

    /// <summary>
    ///     Gets or sets the purchase cost of the item in the game's currency.
    /// </summary>
    [BsonElement("cost")]
    public int Cost { get; set; }

    /// <summary>
    ///     Gets or sets the power of this item when flung in battle, if applicable.
    /// </summary>
    [BsonElement("fling_power")]
    [BsonSerializer(typeof(NullableIntSerializer))]
    public int? FlingPower { get; set; }

    /// <summary>
    ///     Gets or sets the effect identifier when this item is flung in battle, if applicable.
    /// </summary>
    [BsonElement("fling_effect_id")]
    [BsonSerializer(typeof(NullableIntSerializer))]
    public int? FlingEffectId { get; set; }
}