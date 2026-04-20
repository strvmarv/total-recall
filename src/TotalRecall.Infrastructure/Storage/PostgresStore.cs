using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// Tier-aware CRUD over the two Postgres content tables (<c>memories</c> and
/// <c>knowledge</c>). Tier is stored as a column rather than encoded in the
/// table name, unlike <see cref="SqliteStore"/>'s six-table layout.
///
/// All inserts stamp <c>owner_id</c> from the value supplied at construction.
/// Tags are stored as JSONB; embeddings use the pgvector <c>vector</c> type.
/// </summary>
public sealed class PostgresStore : IStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _ownerId;
    private readonly int _hotMaxEntries;

    /// <summary>
    /// Constructs a <see cref="PostgresStore"/> that writes <paramref name="ownerId"/>
    /// into every row's <c>owner_id</c> column.
    /// </summary>
    /// <param name="dataSource">Configured Npgsql data source.</param>
    /// <param name="ownerId">Owner identifier stamped on every inserted row.</param>
    /// <param name="hotMaxEntries">Maximum hot memory entries before write-time eviction.</param>
    public PostgresStore(NpgsqlDataSource dataSource, string ownerId, int hotMaxEntries = 50)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        _dataSource = dataSource;
        _ownerId = ownerId;
        _hotMaxEntries = hotMaxEntries > 0 ? hotMaxEntries : 50;
    }

    // --- IStore -----------------------------------------------------

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var table = TableName(type);
        var tierStr = TierString(tier);
        var id = opts.Id ?? Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildInsertSql(table);
        BindInsertParameters(cmd, id, tierStr, opts, now, _ownerId);
        cmd.ExecuteNonQuery();

        if (tier == Tier.Hot && type == ContentType.Memory)
            EvictHotIfOverLimit(type);

        return id;
    }

    public string InsertWithEmbedding(
        Tier tier,
        ContentType type,
        InsertEntryOpts opts,
        ReadOnlyMemory<float> embedding)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var table = TableName(type);
        var tierStr = TierString(tier);
        var id = opts.Id ?? Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var conn = _dataSource.OpenConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = BuildInsertWithEmbeddingSql(table);
            BindInsertParameters(cmd, id, tierStr, opts, now, _ownerId);
            cmd.Parameters.Add(new NpgsqlParameter { ParameterName = "@embedding", Value = new Vector(embedding.ToArray()) });
            cmd.ExecuteNonQuery();

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        if (tier == Tier.Hot && type == ContentType.Memory)
            EvictHotIfOverLimit(type);

        return id;
    }

    private static string BuildInsertSql(string table) => $@"
INSERT INTO {table}
  (id, tier, content, summary, source, source_tool, project, tags,
   created_at, updated_at, last_accessed_at, access_count,
   decay_score, parent_id, collection_id, metadata, owner_id, scope, entry_type)
VALUES
  (@id, @tier, @content, @summary, @source, @source_tool, @project, @tags,
   @created_at, @updated_at, @last_accessed_at, @access_count,
   @decay_score, @parent_id, @collection_id, @metadata, @owner_id, @scope, @entry_type)";

    private static string BuildInsertWithEmbeddingSql(string table) => $@"
INSERT INTO {table}
  (id, tier, content, summary, source, source_tool, project, tags,
   created_at, updated_at, last_accessed_at, access_count,
   decay_score, parent_id, collection_id, metadata, owner_id, scope, entry_type, embedding)
VALUES
  (@id, @tier, @content, @summary, @source, @source_tool, @project, @tags,
   @created_at, @updated_at, @last_accessed_at, @access_count,
   @decay_score, @parent_id, @collection_id, @metadata, @owner_id, @scope, @entry_type, @embedding)";

    private static void BindInsertParameters(
        NpgsqlCommand cmd,
        string id,
        string tierStr,
        InsertEntryOpts opts,
        long now,
        string ownerId)
    {
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@tier", tierStr);
        cmd.Parameters.AddWithValue("@content", opts.Content);
        cmd.Parameters.Add(new NpgsqlParameter("@summary", NpgsqlDbType.Text)
            { Value = (object?)opts.Summary ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@source", NpgsqlDbType.Text)
            { Value = (object?)opts.Source ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@source_tool", NpgsqlDbType.Text)
        {
            Value = opts.SourceTool is not null
                ? SourceToolMapping.ToDbValue(opts.SourceTool)
                : (object)DBNull.Value,
        });
        cmd.Parameters.Add(new NpgsqlParameter("@project", NpgsqlDbType.Text)
            { Value = (object?)opts.Project ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@tags", NpgsqlDbType.Jsonb)
            { Value = TagsJson.Encode(opts.Tags) });
        cmd.Parameters.AddWithValue("@created_at", now);
        cmd.Parameters.AddWithValue("@updated_at", now);
        cmd.Parameters.AddWithValue("@last_accessed_at", now);
        cmd.Parameters.AddWithValue("@access_count", 0);
        cmd.Parameters.AddWithValue("@decay_score", 1.0);
        cmd.Parameters.Add(new NpgsqlParameter("@parent_id", NpgsqlDbType.Text)
            { Value = (object?)opts.ParentId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@collection_id", NpgsqlDbType.Text)
            { Value = (object?)opts.CollectionId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@metadata", NpgsqlDbType.Jsonb)
            { Value = opts.MetadataJson ?? "{}" });
        cmd.Parameters.AddWithValue("@owner_id", ownerId);
        cmd.Parameters.AddWithValue("@scope", opts.Scope ?? "");
        cmd.Parameters.AddWithValue(
            "@entry_type",
            EntryTypeMapping.ToDbValue(opts.EntryType ?? EntryType.Preference));
    }

    public Entry? Get(Tier tier, ContentType type, string id)
    {
        var table = TableName(type);
        var tierStr = TierString(tier);
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE tier = @tier AND id = @id";
        cmd.Parameters.AddWithValue("@tier", tierStr);
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return RowToEntry(reader);
    }

    public long? GetInternalKey(Tier tier, ContentType type, string id)
    {
        var table = TableName(type);
        var tierStr = TierString(tier);
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT internal_key FROM {table} WHERE tier = @tier AND id = @id";
        cmd.Parameters.AddWithValue("@tier", tierStr);
        cmd.Parameters.AddWithValue("@id", id);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : result is int i ? (long)i : null;
    }

    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var table = TableName(type);
        var tierStr = TierString(tier);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var setClauses = new List<string> { "updated_at = @updated_at" };
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("@updated_at", now);

        if (opts.Content is not null)
        {
            setClauses.Add("content = @content");
            cmd.Parameters.AddWithValue("@content", opts.Content);
        }
        if (opts.Summary is not null || opts.ClearSummary)
        {
            setClauses.Add("summary = @summary");
            cmd.Parameters.Add(new NpgsqlParameter("@summary", NpgsqlDbType.Text)
                { Value = (object?)opts.Summary ?? DBNull.Value });
        }
        if (opts.Tags is not null)
        {
            setClauses.Add("tags = @tags");
            cmd.Parameters.Add(new NpgsqlParameter("@tags", NpgsqlDbType.Jsonb)
                { Value = TagsJson.Encode(opts.Tags) });
        }
        if (opts.Project is not null || opts.ClearProject)
        {
            setClauses.Add("project = @project");
            cmd.Parameters.Add(new NpgsqlParameter("@project", NpgsqlDbType.Text)
                { Value = (object?)opts.Project ?? DBNull.Value });
        }
        if (opts.DecayScore.HasValue)
        {
            setClauses.Add("decay_score = @decay_score");
            cmd.Parameters.AddWithValue("@decay_score", opts.DecayScore.Value);
        }
        if (opts.MetadataJson is not null)
        {
            setClauses.Add("metadata = @metadata");
            cmd.Parameters.Add(new NpgsqlParameter("@metadata", NpgsqlDbType.Jsonb)
                { Value = opts.MetadataJson });
        }
        if (opts.Touch)
        {
            setClauses.Add("access_count = access_count + 1");
            setClauses.Add("last_accessed_at = @last_accessed_at");
            cmd.Parameters.AddWithValue("@last_accessed_at", now);
        }

        cmd.Parameters.AddWithValue("@tier", tierStr);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.CommandText =
            $"UPDATE {table} SET {string.Join(", ", setClauses)} WHERE tier = @tier AND id = @id";
        cmd.ExecuteNonQuery();
    }

    public void Delete(Tier tier, ContentType type, string id)
    {
        var table = TableName(type);
        var tierStr = TierString(tier);
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE tier = @tier AND id = @id";
        cmd.Parameters.AddWithValue("@tier", tierStr);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
    {
        var table = TableName(type);
        var tierStr = TierString(tier);
        var orderBy = ParseOrderBy(opts?.OrderBy);

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        var sql = new StringBuilder();
        sql.Append("SELECT * FROM ").Append(table)
           .Append(" WHERE tier = @tier");
        cmd.Parameters.AddWithValue("@tier", tierStr);

        if (opts?.Project is not null)
        {
            if (opts.IncludeGlobal)
                sql.Append(" AND (project = @project OR project IS NULL)");
            else
                sql.Append(" AND project = @project");
            cmd.Parameters.AddWithValue("@project", opts.Project);
        }

        if (opts?.ParentId is not null)
        {
            sql.Append(" AND parent_id = @parent_id");
            cmd.Parameters.AddWithValue("@parent_id", opts.ParentId);
        }

        if (opts?.Scopes is { Count: > 0 } scopes)
        {
            var scopeParams = new List<string>();
            for (var i = 0; i < scopes.Count; i++)
            {
                var paramName = $"@scope{i}";
                scopeParams.Add(paramName);
                cmd.Parameters.AddWithValue(paramName, scopes[i]);
            }
            sql.Append($" AND scope IN ({string.Join(", ", scopeParams)})");
        }

        sql.Append(" ORDER BY ").Append(orderBy);

        if (opts?.Limit is int limit)
        {
            sql.Append(" LIMIT @limit");
            cmd.Parameters.AddWithValue("@limit", limit);
        }

        cmd.CommandText = sql.ToString();
        return ReadAll(cmd);
    }

    public int Count(Tier tier, ContentType type)
    {
        var table = TableName(type);
        var tierStr = TierString(tier);
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE tier = @tier";
        cmd.Parameters.AddWithValue("@tier", tierStr);
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : result is int i ? i : 0;
    }

    public int CountKnowledgeCollections()
    {
        // Mirrors src-ts/tools/session-tools.ts: collections live in
        // cold knowledge only, NULL collection_ids excluded.
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(DISTINCT collection_id) FROM knowledge WHERE tier = @tier AND collection_id IS NOT NULL";
        cmd.Parameters.AddWithValue("@tier", TierString(Tier.Cold));
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : result is int i ? i : 0;
    }

    public IReadOnlyList<Entry> ListByMetadata(
        Tier tier,
        ContentType type,
        IReadOnlyDictionary<string, string> metadataFilter,
        ListEntriesOpts? opts = null)
    {
        ArgumentNullException.ThrowIfNull(metadataFilter);
        if (metadataFilter.Count == 0)
            throw new ArgumentException(
                "metadataFilter must contain at least one key-value pair",
                nameof(metadataFilter));

        var table = TableName(type);
        var tierStr = TierString(tier);
        var orderBy = ParseOrderBy(opts?.OrderBy);

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = new List<string> { "tier = @tier" };
        cmd.Parameters.AddWithValue("@tier", tierStr);

        var i = 0;
        foreach (var kv in metadataFilter)
        {
            if (!MetadataKeyRegex.IsMatch(kv.Key))
                throw new ArgumentException(
                    $"Invalid metadata key: {kv.Key}",
                    nameof(metadataFilter));
            var paramName = $"@mv{i}";
            // Use Postgres JSONB arrow operator to extract by key
            where.Add($"metadata->>${EscapeLiteral(kv.Key)} = {paramName}");
            cmd.Parameters.AddWithValue(paramName, kv.Value);
            i++;
        }

        var sql = new StringBuilder();
        sql.Append("SELECT * FROM ").Append(table)
           .Append(" WHERE ").Append(string.Join(" AND ", where))
           .Append(" ORDER BY ").Append(orderBy);

        if (opts?.Limit is int limit)
        {
            sql.Append(" LIMIT @limit");
            cmd.Parameters.AddWithValue("@limit", limit);
        }

        cmd.CommandText = sql.ToString();
        return ReadAll(cmd);
    }

    public void Move(
        Tier fromTier,
        ContentType fromType,
        Tier toTier,
        ContentType toType,
        string id)
    {
        // In Postgres the two tables share a tier column, so:
        // - same table: just UPDATE tier
        // - different tables: SELECT, INSERT into target, DELETE from source
        var fromTable = TableName(fromType);
        var toTable = TableName(toType);
        var fromTierStr = TierString(fromTier);
        var toTierStr = TierString(toTier);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var conn = _dataSource.OpenConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            if (fromTable == toTable)
            {
                // Same table: just flip the tier column
                using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText =
                    $"UPDATE {fromTable} SET tier = @to_tier, updated_at = @updated_at WHERE tier = @from_tier AND id = @id";
                updateCmd.Parameters.AddWithValue("@to_tier", toTierStr);
                updateCmd.Parameters.AddWithValue("@updated_at", now);
                updateCmd.Parameters.AddWithValue("@from_tier", fromTierStr);
                updateCmd.Parameters.AddWithValue("@id", id);
                var affected = updateCmd.ExecuteNonQuery();
                if (affected == 0)
                    throw new InvalidOperationException(
                        $"Entry {id} not found in {fromTable} tier={fromTierStr}");
            }
            else
            {
                // Different tables: read source, insert into target, delete source
                Entry? entry;
                using (var readCmd = conn.CreateCommand())
                {
                    readCmd.Transaction = tx;
                    readCmd.CommandText =
                        $"SELECT * FROM {fromTable} WHERE tier = @tier AND id = @id";
                    readCmd.Parameters.AddWithValue("@tier", fromTierStr);
                    readCmd.Parameters.AddWithValue("@id", id);
                    using var reader = readCmd.ExecuteReader();
                    entry = reader.Read() ? RowToEntry(reader) : null;
                }
                if (entry is null)
                    throw new InvalidOperationException(
                        $"Entry {id} not found in {fromTable} tier={fromTierStr}");

                using (var insertCmd = conn.CreateCommand())
                {
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = $@"
INSERT INTO {toTable}
  (id, tier, content, summary, source, source_tool, project, tags,
   created_at, updated_at, last_accessed_at, access_count,
   decay_score, parent_id, collection_id, metadata, owner_id, scope, entry_type)
VALUES
  (@id, @tier, @content, @summary, @source, @source_tool, @project, @tags,
   @created_at, @updated_at, @last_accessed_at, @access_count,
   @decay_score, @parent_id, @collection_id, @metadata, @owner_id, @scope, @entry_type)";

                    insertCmd.Parameters.AddWithValue("@id", entry.Id);
                    insertCmd.Parameters.AddWithValue("@tier", toTierStr);
                    insertCmd.Parameters.AddWithValue("@content", entry.Content);
                    insertCmd.Parameters.Add(new NpgsqlParameter("@summary", NpgsqlDbType.Text)
                        { Value = ToDbObject(entry.Summary) });
                    insertCmd.Parameters.Add(new NpgsqlParameter("@source", NpgsqlDbType.Text)
                        { Value = ToDbObject(entry.Source) });
                    insertCmd.Parameters.Add(new NpgsqlParameter("@source_tool", NpgsqlDbType.Text)
                    {
                        Value = FSharpOption<SourceTool>.get_IsSome(entry.SourceTool)
                            ? SourceToolMapping.ToDbValue(entry.SourceTool.Value)
                            : (object)DBNull.Value,
                    });
                    insertCmd.Parameters.Add(new NpgsqlParameter("@project", NpgsqlDbType.Text)
                        { Value = ToDbObject(entry.Project) });
                    insertCmd.Parameters.Add(new NpgsqlParameter("@tags", NpgsqlDbType.Jsonb)
                        { Value = TagsJson.EncodeFList(entry.Tags) });
                    insertCmd.Parameters.AddWithValue("@created_at", entry.CreatedAt);
                    insertCmd.Parameters.AddWithValue("@updated_at", now);
                    insertCmd.Parameters.AddWithValue("@last_accessed_at", entry.LastAccessedAt);
                    insertCmd.Parameters.AddWithValue("@access_count", entry.AccessCount);
                    insertCmd.Parameters.AddWithValue("@decay_score", entry.DecayScore);
                    insertCmd.Parameters.Add(new NpgsqlParameter("@parent_id", NpgsqlDbType.Text)
                        { Value = ToDbObject(entry.ParentId) });
                    insertCmd.Parameters.Add(new NpgsqlParameter("@collection_id", NpgsqlDbType.Text)
                        { Value = ToDbObject(entry.CollectionId) });
                    insertCmd.Parameters.Add(new NpgsqlParameter("@metadata", NpgsqlDbType.Jsonb)
                        { Value = entry.MetadataJson });
                    insertCmd.Parameters.AddWithValue("@owner_id", _ownerId);
                    insertCmd.Parameters.AddWithValue("@scope", entry.Scope);
                    insertCmd.Parameters.AddWithValue(
                        "@entry_type", EntryTypeMapping.ToDbValue(entry.EntryType));
                    insertCmd.ExecuteNonQuery();
                }

                using (var delCmd = conn.CreateCommand())
                {
                    delCmd.Transaction = tx;
                    delCmd.CommandText =
                        $"DELETE FROM {fromTable} WHERE tier = @tier AND id = @id";
                    delCmd.Parameters.AddWithValue("@tier", fromTierStr);
                    delCmd.Parameters.AddWithValue("@id", id);
                    delCmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // --- write-time hot eviction -----------------------------------------

    private void EvictHotIfOverLimit(ContentType type)
    {
        if (Count(Tier.Hot, type) <= _hotMaxEntries) return;

        var table = TableName(type);
        var tierStr = TierString(Tier.Hot);
        string? lowestId;
        using (var conn = _dataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT id FROM {table} WHERE tier = @tier ORDER BY decay_score ASC LIMIT 1";
            cmd.Parameters.AddWithValue("@tier", tierStr);
            lowestId = cmd.ExecuteScalar() as string;
        }
        if (lowestId is null) return;

        try
        {
            Move(Tier.Hot, type, Tier.Warm, type, lowestId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"total-recall: hot eviction failed for {lowestId}: {ex.Message}");
        }
    }

    // --- row deserialization ---------------------------------------------

    private static Entry RowToEntry(NpgsqlDataReader reader)
    {
        string id = reader.GetString(reader.GetOrdinal("id"));
        string content = reader.GetString(reader.GetOrdinal("content"));
        var summary = ReadNullableString(reader, "summary");
        var source = ReadNullableString(reader, "source");
        var sourceToolStr = ReadNullableStringRaw(reader, "source_tool");
        var sourceTool = SourceToolMapping.Parse(sourceToolStr);
        var project = ReadNullableString(reader, "project");
        // tags is stored as JSONB; Npgsql returns it as a string when GetString is used
        var tagsStr = ReadNullableStringRaw(reader, "tags") ?? "[]";
        var tags = TagsJson.Decode(tagsStr);
        long createdAt = reader.GetInt64(reader.GetOrdinal("created_at"));
        long updatedAt = reader.GetInt64(reader.GetOrdinal("updated_at"));
        long lastAccessedAt = reader.GetInt64(reader.GetOrdinal("last_accessed_at"));
        int accessCount = reader.GetInt32(reader.GetOrdinal("access_count"));
        double decayScore = reader.GetDouble(reader.GetOrdinal("decay_score"));
        var parentId = ReadNullableString(reader, "parent_id");
        var collectionId = ReadNullableString(reader, "collection_id");
        var scope = ReadNullableStringRaw(reader, "scope") ?? "";
        var entryTypeStr = ReadNullableStringRaw(reader, "entry_type");
        var entryType = EntryTypeMapping.ParseOrDefault(entryTypeStr);
        var metadataJson = ReadNullableStringRaw(reader, "metadata") ?? "{}";

        return new Entry(
            id,
            content,
            summary,
            source,
            sourceTool,
            project,
            ListModule.OfSeq(tags),
            createdAt,
            updatedAt,
            lastAccessedAt,
            accessCount,
            decayScore,
            parentId,
            collectionId,
            scope,
            entryType,
            metadataJson);
    }

    private static FSharpOption<string> ReadNullableString(NpgsqlDataReader reader, string name)
    {
        var ord = reader.GetOrdinal(name);
        return reader.IsDBNull(ord)
            ? FSharpOption<string>.None
            : FSharpOption<string>.Some(reader.GetString(ord));
    }

    private static string? ReadNullableStringRaw(NpgsqlDataReader reader, string name)
    {
        var ord = reader.GetOrdinal(name);
        return reader.IsDBNull(ord) ? null : reader.GetString(ord);
    }

    private static object ToDbObject(FSharpOption<string> opt) =>
        FSharpOption<string>.get_IsSome(opt) ? opt.Value : (object)DBNull.Value;

    private static IReadOnlyList<Entry> ReadAll(NpgsqlCommand cmd)
    {
        var results = new List<Entry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(RowToEntry(reader));
        return results;
    }

    // --- table / tier helpers --------------------------------------------

    /// <summary>Maps <see cref="ContentType"/> to the Postgres table name.</summary>
    private static string TableName(ContentType type)
    {
        if (type.IsMemory) return "memories";
        if (type.IsKnowledge) return "knowledge";
        throw new ArgumentOutOfRangeException(nameof(type));
    }

    /// <summary>Maps <see cref="Tier"/> to its column string value.</summary>
    private static string TierString(Tier tier)
    {
        if (tier.IsHot) return "hot";
        if (tier.IsWarm) return "warm";
        if (tier.IsCold) return "cold";
        throw new ArgumentOutOfRangeException(nameof(tier));
    }

    // --- order-by whitelist ----------------------------------------------

    private static readonly HashSet<string> AllowedOrderColumns = new(StringComparer.Ordinal)
    {
        "created_at",
        "updated_at",
        "last_accessed_at",
        "access_count",
        "decay_score",
        "content",
    };

    private static string ParseOrderBy(string? orderBy)
    {
        var input = string.IsNullOrWhiteSpace(orderBy) ? "created_at DESC" : orderBy;
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException("Invalid orderBy clause", nameof(orderBy));

        var column = parts[0];
        if (!AllowedOrderColumns.Contains(column))
            throw new ArgumentException($"Invalid orderBy column: {column}", nameof(orderBy));

        string direction;
        if (parts.Length == 1)
        {
            direction = "DESC";
        }
        else if (parts.Length == 2)
        {
            if (parts[1].Equals("ASC", StringComparison.OrdinalIgnoreCase))
                direction = "ASC";
            else if (parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase))
                direction = "DESC";
            else
                throw new ArgumentException($"Invalid orderBy direction: {parts[1]}", nameof(orderBy));
        }
        else
        {
            throw new ArgumentException($"Invalid orderBy format: {orderBy}", nameof(orderBy));
        }
        return $"{column} {direction}";
    }

    private static readonly Regex MetadataKeyRegex =
        new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Wraps a metadata key in single-quoted SQL literal syntax for use in
    /// the JSONB <c>->></c> operator. Keys are already validated against
    /// <see cref="MetadataKeyRegex"/> (alphanumeric + underscore only),
    /// so no further escaping is needed — but the quotes are required for
    /// proper SQL syntax.
    /// </summary>
    private static string EscapeLiteral(string key) => $"'{key}'";
}
