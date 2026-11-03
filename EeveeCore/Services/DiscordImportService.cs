using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace EeveeCore.Services;

/// <summary>
///     Copies a player's game data from the Discord EeveeCore database into their PokeFlux account, once per
///     Discord account and once per Fluxer account.
/// </summary>
/// <param name="credentials">The bot credentials, holding both connection strings.</param>
public sealed class DiscordImportService(BotCredentials credentials) : INService
{
    private const int MaxPartyNameLength = 20;

    private static readonly string[] SummedUserColumns =
    [
        "redeems", "evpoints", "upvotepoints", "mewcoins", "skin_tokens", "mystery_token", "event_skin_tokens",
        "vip_tokens", "slime", "crystal_slime", "voucher", "os_rep", "raffle"
    ];

    private static readonly string[] HighestUserColumns =
        ["fishing_exp", "fishing_level", "fishing_level_cap", "luck", "daycarelimit", "marketlimit"];

    private static readonly string[] EitherUserColumns = ["bike", "ancient_unlocked"];

    private static readonly string[] SummedJsonUserColumns = ["inventory", "items", "holidayinv", "tokens", "skins"];

    private static readonly string[] SkippedPokemonColumns = ["id", "ditto_id"];

    /// <summary>
    ///     Gets whether importing is set up.
    /// </summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(credentials.DiscordImportConnectionString);

    /// <summary>
    ///     Describes what an import would bring over.
    /// </summary>
    /// <param name="discordId">The Discord account to import from.</param>
    /// <param name="fluxerId">The Fluxer account to import into.</param>
    public async Task<DiscordImportPreview> PreviewAsync(ulong discordId, ulong fluxerId)
    {
        await using var source = await OpenSourceAsync();
        await using var target = await OpenTargetAsync();
        var d = (long)discordId;
        var f = (long)fluxerId;

        var discordImported = await ScalarAsync<bool>(target,
            "SELECT EXISTS (SELECT 1 FROM discord_imports WHERE discord_id = @d)", ("d", d));
        var fluxerImported = await ScalarAsync<bool>(target,
            "SELECT EXISTS (SELECT 1 FROM discord_imports WHERE fluxer_id = @f)", ("f", f));
        var started = await ScalarAsync<bool>(target, "SELECT EXISTS (SELECT 1 FROM users WHERE u_id = @f)", ("f", f));
        var current = await ScalarAsync<long>(target,
            "SELECT count(*) FROM user_pokemon_ownership WHERE user_id = @f", ("f", f));

        long credits = 0, redeems = 0, items = 0;
        var hasData = false;
        await using (var command = Command(source, "SELECT mewcoins, redeems, items::text, inventory::text FROM users WHERE u_id = @d", ("d", d)))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                hasData = true;
                credits = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                redeems = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                items = CountItems(reader.IsDBNull(2) ? null : reader.GetString(2))
                        + CountItems(reader.IsDBNull(3) ? null : reader.GetString(3));
            }
        }

        long pokemon = 0, shiny = 0, radiant = 0;
        await using (var command = Command(source,
                         """
                         SELECT count(*), count(*) FILTER (WHERE p.shiny), count(*) FILTER (WHERE p.radiant)
                         FROM user_pokemon_ownership o JOIN pokes p ON p.id = o.pokemon_id
                         WHERE o.user_id = @d
                         """, ("d", d)))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                pokemon = reader.GetInt64(0);
                shiny = reader.GetInt64(1);
                radiant = reader.GetInt64(2);
            }
        }

        var parties = await ScalarAsync<long>(source,
            "SELECT count(*) FROM partys WHERE u_id = @d AND num_nonnulls(slot1, slot2, slot3, slot4, slot5, slot6) > 0",
            ("d", d));
        var milestones = await ScalarAsync<long>(source, "SELECT count(*) FROM milestone_progress WHERE u_id = @d",
            ("d", d));

        return new DiscordImportPreview(discordImported, fluxerImported, started, hasData, current, pokemon, shiny,
            radiant, parties, credits, redeems, items, milestones);
    }

    /// <summary>
    ///     Copies the Discord account's data into the Fluxer account in one transaction.
    /// </summary>
    /// <param name="discordId">The Discord account to import from.</param>
    /// <param name="fluxerId">The Fluxer account to import into.</param>
    /// <returns>Whether it worked, a message for the player, and what was copied.</returns>
    public async Task<(bool Success, string Message, DiscordImportResult? Result)> ImportAsync(ulong discordId,
        ulong fluxerId)
    {
        await using var source = await OpenSourceAsync();
        await using var target = await OpenTargetAsync();
        var d = (long)discordId;
        var f = (long)fluxerId;

        if (!await ScalarAsync<bool>(source, "SELECT EXISTS (SELECT 1 FROM users WHERE u_id = @d)", ("d", d)))
            return (false, "That Discord account has never played EeveeCore.", null);

        await using var transaction = await target.BeginTransactionAsync();

        await using (var command = Command(target, "SELECT u_id FROM users WHERE u_id = @f FOR UPDATE", ("f", f)))
        {
            if (await command.ExecuteScalarAsync() is null)
                return (false, "You have not started! Start with the start command first.", null);
        }

        try
        {
            await ExecuteAsync(target, "INSERT INTO discord_imports (discord_id, fluxer_id) VALUES (@d, @f)", ("d", d),
                ("f", f));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (false, "This Discord account or your PokeFlux account has already been used for an import.", null);
        }

        var pokemon = await CopyPokemonAsync(source, target, d, f);
        var parties = await CopyPartiesAsync(source, target, d, f);
        await MergeUserAsync(source, target, d, f);
        await MergeRowAsync(source, target, "achievements", "u_id", d, f, ["easter_completed"], []);
        await MergeRowAsync(source, target, "eggs", "u_id", d, f, [], []);
        await MergeRowAsync(source, target, "gyms", "u_id", d, f, GymTimestampColumns, ["cooldown"]);
        await MergeRowAsync(source, target, "halloween", "u_id", d, f, [], ["username"]);
        await MergeRowAsync(source, target, "user_loyalty", "u_id", d, f, ["daily_streak", "last_login"],
            ["created_at", "updated_at"]);
        var milestones = await CopyMilestonesAsync(source, target, d, f);
        var dittobitties = await CopyRowsAsync(source, target, "user_dittobitties", "user_id", d, f);

        var result = new DiscordImportResult(pokemon, parties, milestones, dittobitties);
        await ExecuteAsync(target,
            """
            UPDATE discord_imports SET pokemon = @p, parties = @parties, milestones = @m, details = @details
            WHERE discord_id = @d
            """,
            ("p", pokemon), ("parties", parties), ("m", milestones),
            ("details", new NpgsqlParameter { Value = JsonSerializer.Serialize(result), NpgsqlDbType = NpgsqlDbType.Jsonb }),
            ("d", d));

        await transaction.CommitAsync();
        return (true, $"Brought over {pokemon:N0} Pokémon from Discord.", result);
    }

    private static readonly string[] GymTimestampColumns =
    [
        "theblazingbowl_ts", "theunderwaterarena_ts", "fieldofflowers_ts", "prismpalace_ts", "thebasicbungalow_ts",
        "starterstation_ts", "floatingisland_ts", "stablessstadium_ts", "triangulartower_ts", "monotypemarket_ts",
        "generationalgallery_ts", "bansaucearena_ts", "physicalfortress_ts", "specialshack_ts",
        "reversedimension_ts", "elite4_ts"
    ];

    private static async Task<int> CopyPokemonAsync(NpgsqlConnection source, NpgsqlConnection target, long d, long f)
    {
        var targetColumns = await ColumnsAsync(target, "pokes");
        var sourceColumns = (await ColumnsAsync(source, "pokes")).Select(c => c.Name).ToHashSet();
        var columns = targetColumns.Select(c => c.Name)
            .Where(c => !SkippedPokemonColumns.Contains(c) && sourceColumns.Contains(c)).ToList();
        var quoted = string.Join(", ", columns.Select(Quote));

        await ExecuteAsync(target,
            $"CREATE TEMP TABLE imp_pokes ON COMMIT DROP AS SELECT 0::bigint AS old_id, 0::integer AS position, {quoted} FROM pokes WITH NO DATA");

        await using (var reader = await source.BeginRawBinaryCopyAsync(
                         $"COPY (SELECT p.id::bigint, o.position::integer, {string.Join(", ", columns.Select(c => "p." + Quote(c)))} FROM user_pokemon_ownership o JOIN pokes p ON p.id = o.pokemon_id WHERE o.user_id = {d} ORDER BY o.position) TO STDOUT (FORMAT BINARY)"))
        await using (var writer = await target.BeginRawBinaryCopyAsync(
                         $"COPY imp_pokes (old_id, position, {quoted}) FROM STDIN (FORMAT BINARY)"))
        {
            await reader.CopyToAsync(writer);
        }

        await ExecuteAsync(target, "ALTER TABLE imp_pokes ADD COLUMN new_id bigint");
        await ExecuteAsync(target,
            "UPDATE imp_pokes SET new_id = nextval(COALESCE(pg_get_serial_sequence('public.pokes', 'id'), 'public.pokes_id_seq'))");

        var values = columns.Select(c => c switch
        {
            "owner" => "@f",
            "caught_by" => "CASE WHEN caught_by = @d THEN @f ELSE caught_by END",
            "market_enlist" => "false",
            "price" => "0",
            _ => Quote(c)
        });
        await ExecuteAsync(target,
            $"INSERT INTO pokes (id, {quoted}) SELECT new_id, {string.Join(", ", values)} FROM imp_pokes ORDER BY position",
            ("d", d), ("f", f));

        return await ExecuteAsync(target,
            """
            INSERT INTO user_pokemon_ownership (user_id, pokemon_id, position)
            SELECT @f, new_id,
                   (SELECT COALESCE(MAX(position), -1) + 1 FROM user_pokemon_ownership WHERE user_id = @f)
                   + row_number() OVER (ORDER BY position) - 1
            FROM imp_pokes
            """, ("f", f));
    }

    private static async Task<int> CopyPartiesAsync(NpgsqlConnection source, NpgsqlConnection target, long d, long f)
    {
        var parties = new List<(string Name, long?[] Slots)>();
        await using (var command = Command(source,
                         "SELECT name, is_current_party, slot1, slot2, slot3, slot4, slot5, slot6 FROM partys WHERE u_id = @d ORDER BY p_id",
                         ("d", d)))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.GetBoolean(1) ? "discord" : (reader.IsDBNull(0) ? "discord" : reader.GetString(0));
                var slots = Enumerable.Range(2, 6).Select(i => reader.IsDBNull(i) ? (long?)null : reader.GetInt64(i))
                    .ToArray();
                parties.Add((name.Trim().ToLowerInvariant(), slots));
            }
        }

        if (parties.Count == 0)
            return 0;

        var mapped = new Dictionary<long, long>();
        await using (var command = Command(target, "SELECT old_id, new_id FROM imp_pokes"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                mapped[reader.GetInt64(0)] = reader.GetInt64(1);
        }

        var taken = new HashSet<string>();
        await using (var command = Command(target, "SELECT name FROM partys WHERE u_id = @f AND name IS NOT NULL", ("f", f)))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                taken.Add(reader.GetString(0).ToLowerInvariant());
        }

        var copied = 0;
        foreach (var (name, slots) in parties)
        {
            var newSlots = slots.Select(s => s is { } id && mapped.TryGetValue(id, out var newId) ? newId : (long?)null)
                .ToArray();
            if (newSlots.All(s => s is null))
                continue;

            var unique = UniqueName(string.IsNullOrEmpty(name) ? "discord" : name, taken);
            taken.Add(unique);
            await ExecuteAsync(target,
                """
                INSERT INTO partys (u_id, name, slot1, slot2, slot3, slot4, slot5, slot6, quick, is_current_party)
                VALUES (@f, @name, @s1, @s2, @s3, @s4, @s5, @s6, false, false)
                """,
                ("f", f), ("name", unique), ("s1", Db(newSlots[0])), ("s2", Db(newSlots[1])), ("s3", Db(newSlots[2])),
                ("s4", Db(newSlots[3])), ("s5", Db(newSlots[4])), ("s6", Db(newSlots[5])));
            copied++;
        }

        return copied;
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        var trimmed = name.Length > MaxPartyNameLength ? name[..MaxPartyNameLength] : name;
        if (!taken.Contains(trimmed))
            return trimmed;

        for (var i = 2;; i++)
        {
            var suffix = i.ToString();
            var candidate = (name.Length + suffix.Length > MaxPartyNameLength
                ? name[..(MaxPartyNameLength - suffix.Length)]
                : name) + suffix;
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private static async Task MergeUserAsync(NpgsqlConnection source, NpgsqlConnection target, long d, long f)
    {
        var columns = SummedUserColumns.Concat(HighestUserColumns).Concat(EitherUserColumns)
            .Concat(["level", "current_xp", "type_tokens", "titles"]).ToList();
        var select =
            $"SELECT {string.Join(", ", columns.Select(Quote))}, {string.Join(", ", SummedJsonUserColumns.Select(c => Quote(c) + "::text"))} FROM users WHERE u_id = @id";

        var from = await ReadRowAsync(source, select, d);
        var into = await ReadRowAsync(target, select, f);
        if (from is null || into is null)
            return;

        var updates = new Dictionary<string, object?>();
        foreach (var column in SummedUserColumns)
            updates[column] = Add(into[column], from[column]);
        foreach (var column in HighestUserColumns)
            updates[column] = Highest(into[column], from[column]);
        foreach (var column in EitherUserColumns)
            updates[column] = Either(into[column], from[column]);

        var fromLevel = Convert.ToInt64(from["level"] ?? 0);
        var intoLevel = Convert.ToInt64(into["level"] ?? 0);
        if (fromLevel > intoLevel || (fromLevel == intoLevel && Convert.ToInt64(from["current_xp"] ?? 0) >
                                      Convert.ToInt64(into["current_xp"] ?? 0)))
        {
            updates["level"] = from["level"];
            updates["current_xp"] = from["current_xp"];
        }

        updates["type_tokens"] = AddArrays(into["type_tokens"] as int[], from["type_tokens"] as int[]);
        updates["titles"] = UnionArrays(into["titles"] as string[], from["titles"] as string[]);

        var assignments = updates.Keys.Select(k => $"{Quote(k)} = @{k}").ToList();
        var parameters = updates.Select(u => (u.Key, (object?)u.Value)).ToList();
        foreach (var column in SummedJsonUserColumns)
        {
            assignments.Add($"{Quote(column)} = @{column}");
            parameters.Add((column, new NpgsqlParameter
            {
                Value = MergeJson(into[column] as string, from[column] as string),
                NpgsqlDbType = NpgsqlDbType.Jsonb
            }));
        }

        parameters.Add(("f", f));
        await ExecuteAsync(target, $"UPDATE users SET {string.Join(", ", assignments)} WHERE u_id = @f",
            parameters.ToArray());
    }

    private static async Task MergeRowAsync(NpgsqlConnection source, NpgsqlConnection target, string table,
        string key, long d, long f, string[] highestColumns, string[] keptColumns)
    {
        var targetColumns = await ColumnsAsync(target, table);
        var sourceColumns = (await ColumnsAsync(source, table)).Select(c => c.Name).ToHashSet();
        var columns = targetColumns.Where(c => c.Name != key && sourceColumns.Contains(c.Name)).ToList();
        if (columns.Count == 0)
            return;

        var select = $"SELECT {string.Join(", ", columns.Select(c => Quote(c.Name) + (c.Type == "jsonb" ? "::text" : "")))} FROM {Quote(table)} WHERE {Quote(key)} = @id";
        var from = await ReadRowAsync(source, select, d);
        if (from is null)
            return;

        var into = await ReadRowAsync(target, select, f);
        if (into is null)
        {
            var names = columns.Select(c => Quote(c.Name)).Prepend(Quote(key));
            var values = columns.Select(c => "@" + Param(c.Name)).Prepend("@f");
            var parameters = columns.Select(c => (Param(c.Name), (object?)Typed(c, from[c.Name]))).Append(("f", f));
            await ExecuteAsync(target,
                $"INSERT INTO {Quote(table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", values)})",
                parameters.ToArray());
            return;
        }

        var updates = new List<(string Name, object? Value)>();
        foreach (var column in columns)
        {
            if (keptColumns.Contains(column.Name))
                continue;

            var mine = into[column.Name];
            var theirs = from[column.Name];
            object? merged = column.Type switch
            {
                "boolean" => Either(mine, theirs),
                "integer" or "bigint" or "smallint" or "real" or "double precision" or "numeric" =>
                    highestColumns.Contains(column.Name) ? Highest(mine, theirs) : Add(mine, theirs),
                "date" or "timestamp without time zone" or "timestamp with time zone" => Highest(mine, theirs),
                _ => mine
            };
            updates.Add((column.Name, merged));
        }

        if (updates.Count == 0)
            return;

        var byName = columns.ToDictionary(c => c.Name);
        await ExecuteAsync(target,
            $"UPDATE {Quote(table)} SET {string.Join(", ", updates.Select(u => $"{Quote(u.Name)} = @{Param(u.Name)}"))} WHERE {Quote(key)} = @f",
            updates.Select(u => (Param(u.Name), (object?)Typed(byName[u.Name], u.Value))).Append(("f", f)).ToArray());
    }

    private static async Task<int> CopyMilestonesAsync(NpgsqlConnection source, NpgsqlConnection target, long d, long f)
    {
        var rows = new List<(string Type, int Value, DateTime? CompletedAt)>();
        await using (var command = Command(source,
                         "SELECT achievement_type, milestone_value, completed_at FROM milestone_progress WHERE u_id = @d",
                         ("d", d)))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2)));
        }

        var copied = 0;
        foreach (var (type, value, completedAt) in rows)
            copied += await ExecuteAsync(target,
                """
                INSERT INTO milestone_progress (u_id, achievement_type, milestone_value, completed_at)
                SELECT @f, @type, @value, COALESCE(@at, now())
                WHERE NOT EXISTS (SELECT 1 FROM milestone_progress
                                  WHERE u_id = @f AND achievement_type = @type AND milestone_value = @value)
                """,
                ("f", f), ("type", type), ("value", value),
                ("at", new NpgsqlParameter { Value = (object?)completedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Timestamp }));
        return copied;
    }

    private static async Task<int> CopyRowsAsync(NpgsqlConnection source, NpgsqlConnection target, string table,
        string key, long d, long f)
    {
        var targetColumns = await ColumnsAsync(target, table);
        var sourceColumns = (await ColumnsAsync(source, table)).Select(c => c.Name).ToHashSet();
        var columns = targetColumns.Where(c => c.Name != key && c.Name != "id" && sourceColumns.Contains(c.Name)).ToList();
        var select = $"SELECT {string.Join(", ", columns.Select(c => Quote(c.Name) + (c.Type == "jsonb" ? "::text" : "")))} FROM {Quote(table)} WHERE {Quote(key)} = @id";

        var rows = new List<Dictionary<string, object?>>();
        await using (var command = Command(source, select, ("id", d)))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                rows.Add(columns.Select((c, i) => (c.Name, Value: reader.IsDBNull(i) ? null : reader.GetValue(i)))
                    .ToDictionary(x => x.Name, x => x.Value));
        }

        foreach (var row in rows)
            await ExecuteAsync(target,
                $"INSERT INTO {Quote(table)} ({Quote(key)}, {string.Join(", ", columns.Select(c => Quote(c.Name)))}) VALUES (@f, {string.Join(", ", columns.Select(c => "@" + Param(c.Name)))})",
                columns.Select(c => (Param(c.Name), (object?)Typed(c, row[c.Name]))).Append(("f", f)).ToArray());

        return rows.Count;
    }

    private static object? Add(object? mine, object? theirs) => (mine, theirs) switch
    {
        (null, _) => theirs,
        (_, null) => mine,
        (int a, int b) => (int)Math.Min((long)a + b, int.MaxValue),
        (short a, short b) => (short)Math.Min(a + b, short.MaxValue),
        (long a, long b) => a + b,
        (float a, float b) => a + b,
        (double a, double b) => a + b,
        (decimal a, decimal b) => a + b,
        _ => mine
    };

    private static object? Highest(object? mine, object? theirs) => (mine, theirs) switch
    {
        (null, _) => theirs,
        (_, null) => mine,
        (IComparable a, _) when a.GetType() == theirs.GetType() => a.CompareTo(theirs) >= 0 ? mine : theirs,
        _ => mine
    };

    private static object? Either(object? mine, object? theirs) =>
        mine is true || theirs is true ? true : mine ?? theirs;

    private static int[]? AddArrays(int[]? mine, int[]? theirs)
    {
        if (mine is null || theirs is null)
            return mine ?? theirs;

        var merged = new int[Math.Max(mine.Length, theirs.Length)];
        for (var i = 0; i < merged.Length; i++)
            merged[i] = (i < mine.Length ? mine[i] : 0) + (i < theirs.Length ? theirs[i] : 0);
        return merged;
    }

    private static string[]? UnionArrays(string[]? mine, string[]? theirs) =>
        mine is null || theirs is null ? mine ?? theirs : mine.Concat(theirs.Where(t => !mine.Contains(t))).ToArray();

    private static string MergeJson(string? mine, string? theirs)
    {
        var into = ParseObject(mine);
        var from = ParseObject(theirs);
        MergeInto(into, from);
        return into.ToJsonString();
    }

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static void MergeInto(JsonObject into, JsonObject from)
    {
        foreach (var (key, value) in from.ToList())
        {
            if (value is null)
                continue;

            if (!into.TryGetPropertyValue(key, out var existing) || existing is null)
            {
                into[key] = value.DeepClone();
                continue;
            }

            if (existing is JsonObject nested && value is JsonObject incoming)
                MergeInto(nested, incoming);
            else if (existing is JsonValue a && value is JsonValue b && a.TryGetValue<decimal>(out var x) &&
                     b.TryGetValue<decimal>(out var y))
                into[key] = x + y == Math.Floor(x + y) && Math.Abs(x + y) <= long.MaxValue
                    ? JsonValue.Create((long)(x + y))
                    : JsonValue.Create(x + y);
        }
    }

    private static long CountItems(string? json)
    {
        long total = 0;
        foreach (var (_, value) in ParseObject(json))
            if (value is JsonValue v && v.TryGetValue<decimal>(out var count) && count > 0)
                total += (long)count;
        return total;
    }

    private static object? Typed(Column column, object? value) => column.Type == "jsonb"
        ? new NpgsqlParameter { Value = value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb }
        : value;

    private static object Db(long? value) => value is { } v ? v : DBNull.Value;

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string Param(string column) => "c_" + new string(column.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    private async Task<NpgsqlConnection> OpenSourceAsync()
    {
        var connection = new NpgsqlConnection(credentials.DiscordImportConnectionString
                                              ?? throw new InvalidOperationException("Importing from Discord is not set up."));
        await connection.OpenAsync();
        await using var command = Command(connection, "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY");
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private async Task<NpgsqlConnection> OpenTargetAsync()
    {
        var connection = new NpgsqlConnection(credentials.PostgresConfig.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private sealed record Column(string Name, string Type);

    private static async Task<List<Column>> ColumnsAsync(NpgsqlConnection connection, string table)
    {
        var columns = new List<Column>();
        await using var command = Command(connection,
            """
            SELECT column_name, data_type FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @t ORDER BY ordinal_position
            """, ("t", table));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(new Column(reader.GetString(0), reader.GetString(1) == "ARRAY" ? "array" : reader.GetString(1)));
        return columns;
    }

    private static async Task<Dictionary<string, object?>?> ReadRowAsync(NpgsqlConnection connection, string sql, long id)
    {
        await using var command = Command(connection, sql, ("id", id));
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
        if (!await reader.ReadAsync())
            return null;

        var row = new Dictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters)
        {
            if (value is NpgsqlParameter parameter)
            {
                parameter.ParameterName = name;
                command.Parameters.Add(parameter);
            }
            else
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        return command;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>
///     What an import from Discord would bring over.
/// </summary>
/// <param name="DiscordAlreadyImported">Whether the Discord account has already been imported.</param>
/// <param name="FluxerAlreadyImported">Whether the Fluxer account has already received an import.</param>
/// <param name="Started">Whether the Fluxer account has started.</param>
/// <param name="HasDiscordData">Whether the Discord account ever played.</param>
/// <param name="CurrentPokemon">How many Pokémon the Fluxer account has now.</param>
/// <param name="Pokemon">How many Pokémon would come over.</param>
/// <param name="Shiny">How many of them are shiny.</param>
/// <param name="Radiant">How many of them are radiant.</param>
/// <param name="Parties">How many parties would come over.</param>
/// <param name="Credits">The credits that would be added.</param>
/// <param name="Redeems">The redeems that would be added.</param>
/// <param name="Items">How many items would be added.</param>
/// <param name="Milestones">How many achievement milestones the Discord account reached.</param>
public record DiscordImportPreview(
    bool DiscordAlreadyImported,
    bool FluxerAlreadyImported,
    bool Started,
    bool HasDiscordData,
    long CurrentPokemon,
    long Pokemon,
    long Shiny,
    long Radiant,
    long Parties,
    long Credits,
    long Redeems,
    long Items,
    long Milestones);

/// <summary>
///     What an import brought over.
/// </summary>
/// <param name="Pokemon">The Pokémon copied.</param>
/// <param name="Parties">The parties copied.</param>
/// <param name="Milestones">The milestones added.</param>
/// <param name="Dittobitties">The Dittobitties copied.</param>
public record DiscordImportResult(int Pokemon, int Parties, int Milestones, int Dittobitties);
