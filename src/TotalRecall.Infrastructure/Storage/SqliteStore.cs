using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// Tier-aware CRUD over the 6 content tables. Ports
/// <c>src-ts/db/entries.ts</c> to C# using <see cref="Microsoft.Data.Sqlite"/>
/// with parameterized queries. Table names are resolved from the F# Core
/// <see cref="Tier"/> / <see cref="ContentType"/> DUs via
/// <see cref="MigrationRunner.TableName"/>.
///
/// Two constructors are offered: a production path that opens a new
/// connection from a db path, and a test-friendly path that borrows an
/// already-opened connection and skips migrations. The borrowed-connection
/// form does NOT own disposal.
/// </summary>
public sealed class SqliteStore : ISqliteStore, IDisposable
{
    private readonly MsSqliteConnection _conn;
    private readonly bool _ownsConnection;

    /// <summary>
    /// Production constructor. Opens a new connection at <paramref name="dbPath"/>,
    /// loads sqlite-vec, applies pragmas, and runs all pending migrations.
    /// </summary>
    public SqliteStore(string dbPath)
    {
        _conn = SqliteConnection.Open(dbPath);
        _ownsConnection = true;
        MigrationRunner.RunMigrations(_conn);
    }

    /// <summary>
    /// Test-friendly constructor. Borrows an already-opened connection that
    /// the caller owns. Does NOT run migrations — the caller is expected to
    /// have already done so. Disposal is the caller's responsibility.
    /// </summary>
    public SqliteStore(MsSqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _conn = connection;
        _ownsConnection = false;
    }

    // --- ISqliteStore -----------------------------------------------------

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var table = MigrationRunner.TableName(tier, type);
        var id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = BuildInsertSql(table);
        BindInsertParameters(cmd, id, opts, now);
        cmd.ExecuteNonQuery();
        return id;
    }

    public string InsertWithEmbedding(
        Tier tier,
        ContentType type,
        InsertEntryOpts opts,
        ReadOnlyMemory<float> embedding)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var table = MigrationRunner.TableName(tier, type);
        var vecTable = MigrationRunner.VecTableName(tier, type);
        var id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var tx = _conn.BeginTransaction();
        try
        {
            using (var insertCmd = _conn.CreateCommand())
            {
                insertCmd.Transaction = tx;
                insertCmd.CommandText = BuildInsertSql(table);
                BindInsertParameters(insertCmd, id, opts, now);
                insertCmd.ExecuteNonQuery();
            }

            long rowid;
            using (var rowidCmd = _conn.CreateCommand())
            {
                rowidCmd.Transaction = tx;
                rowidCmd.CommandText = "SELECT last_insert_rowid()";
                rowid = (long)rowidCmd.ExecuteScalar()!;
            }

            using (var vecCmd = _conn.CreateCommand())
            {
                vecCmd.Transaction = tx;
                vecCmd.CommandText =
                    $"INSERT INTO {vecTable} (rowid, embedding) VALUES ($rowid, $embedding)";
                vecCmd.Parameters.AddWithValue("$rowid", rowid);
                vecCmd.Parameters.AddWithValue(
                    "$embedding",
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(embedding.Span).ToArray());
                vecCmd.ExecuteNonQuery();
            }

            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static string BuildInsertSql(string table) => $@"
INSERT INTO {table}
  (id, content, summary, source, source_tool, project, tags,
   created_at, updated_at, last_accessed_at, access_count,
   decay_score, parent_id, collection_id, metadata)
VALUES
  ($id, $content, $summary, $source, $source_tool, $project, $tags,
   $created_at, $updated_at, $last_accessed_at, $access_count,
   $decay_score, $parent_id, $collection_id, $metadata)";

    private static void BindInsertParameters(
        Microsoft.Data.Sqlite.SqliteCommand cmd,
        string id,
        InsertEntryOpts opts,
        long now)
    {
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$content", opts.Content);
        cmd.Parameters.AddWithValue("$summary", (object?)opts.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (object?)opts.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$source_tool",
            opts.SourceTool is not null
                ? SourceToolMapping.ToDbValue(opts.SourceTool)
                : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$project", (object?)opts.Project ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", TagsJson.Encode(opts.Tags));
        cmd.Parameters.AddWithValue("$created_at", now);
        cmd.Parameters.AddWithValue("$updated_at", now);
        cmd.Parameters.AddWithValue("$last_accessed_at", now);
        cmd.Parameters.AddWithValue("$access_count", 0);
        cmd.Parameters.AddWithValue("$decay_score", 1.0);
        cmd.Parameters.AddWithValue("$parent_id", (object?)opts.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$collection_id", (object?)opts.CollectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata", opts.MetadataJson ?? "{}");
    }

    public Entry? Get(Tier tier, ContentType type, string id)
    {
        var table = MigrationRunner.TableName(tier, type);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return RowToEntry(reader);
    }

    public long? GetRowid(Tier tier, ContentType type, string id)
    {
        var table = MigrationRunner.TableName(tier, type);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT rowid FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : null;
    }

    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var table = MigrationRunner.TableName(tier, type);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var setClauses = new List<string> { "updated_at = $updated_at" };
        using var cmd = _conn.CreateCommand();
        cmd.Parameters.AddWithValue("$updated_at", now);

        if (opts.Content is not null)
        {
            setClauses.Add("content = $content");
            cmd.Parameters.AddWithValue("$content", opts.Content);
        }
        if (opts.Summary is not null || opts.ClearSummary)
        {
            setClauses.Add("summary = $summary");
            cmd.Parameters.AddWithValue("$summary", (object?)opts.Summary ?? DBNull.Value);
        }
        if (opts.Tags is not null)
        {
            setClauses.Add("tags = $tags");
            cmd.Parameters.AddWithValue("$tags", TagsJson.Encode(opts.Tags));
        }
        if (opts.Project is not null || opts.ClearProject)
        {
            setClauses.Add("project = $project");
            cmd.Parameters.AddWithValue("$project", (object?)opts.Project ?? DBNull.Value);
        }
        if (opts.DecayScore.HasValue)
        {
            setClauses.Add("decay_score = $decay_score");
            cmd.Parameters.AddWithValue("$decay_score", opts.DecayScore.Value);
        }
        if (opts.MetadataJson is not null)
        {
            setClauses.Add("metadata = $metadata");
            cmd.Parameters.AddWithValue("$metadata", opts.MetadataJson);
        }
        if (opts.Touch)
        {
            setClauses.Add("access_count = access_count + 1");
            setClauses.Add("last_accessed_at = $last_accessed_at");
            cmd.Parameters.AddWithValue("$last_accessed_at", now);
        }

        cmd.Parameters.AddWithValue("$id", id);
        cmd.CommandText = $"UPDATE {table} SET {string.Join(", ", setClauses)} WHERE id = $id";
        cmd.ExecuteNonQuery();
    }

    public void Delete(Tier tier, ContentType type, string id)
    {
        var table = MigrationRunner.TableName(tier, type);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
    {
        var table = MigrationRunner.TableName(tier, type);
        var orderBy = ParseOrderBy(opts?.OrderBy);

        using var cmd = _conn.CreateCommand();
        var sql = new StringBuilder();
        sql.Append("SELECT * FROM ").Append(table);

        if (opts?.Project is not null)
        {
            if (opts.IncludeGlobal)
                sql.Append(" WHERE project = $project OR project IS NULL");
            else
                sql.Append(" WHERE project = $project");
            cmd.Parameters.AddWithValue("$project", opts.Project);
        }

        sql.Append(" ORDER BY ").Append(orderBy);

        if (opts?.Limit is int limit)
        {
            sql.Append(" LIMIT $limit");
            cmd.Parameters.AddWithValue("$limit", limit);
        }

        cmd.CommandText = sql.ToString();
        return ReadAll(cmd);
    }

    public int Count(Tier tier, ContentType type)
    {
        var table = MigrationRunner.TableName(tier, type);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }

    public int CountKnowledgeCollections()
    {
        // Mirrors src-ts/tools/session-tools.ts: collections live in
        // cold_knowledge only, NULL collection_ids excluded.
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(DISTINCT collection_id) FROM cold_knowledge WHERE collection_id IS NOT NULL";
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
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

        var table = MigrationRunner.TableName(tier, type);
        var orderBy = ParseOrderBy(opts?.OrderBy);

        using var cmd = _conn.CreateCommand();
        var where = new List<string>();
        var i = 0;
        foreach (var kv in metadataFilter)
        {
            if (!MetadataKeyRegex.IsMatch(kv.Key))
                throw new ArgumentException(
                    $"Invalid metadata key: {kv.Key}",
                    nameof(metadataFilter));
            var paramName = $"$mv{i}";
            where.Add($"json_extract(metadata, '$.{kv.Key}') = {paramName}");
            cmd.Parameters.AddWithValue(paramName, kv.Value);
            i++;
        }

        var sql = new StringBuilder();
        sql.Append("SELECT * FROM ").Append(table)
           .Append(" WHERE ").Append(string.Join(" AND ", where))
           .Append(" ORDER BY ").Append(orderBy);

        if (opts?.Limit is int limit)
        {
            sql.Append(" LIMIT $limit");
            cmd.Parameters.AddWithValue("$limit", limit);
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
        var fromTable = MigrationRunner.TableName(fromTier, fromType);
        var toTable = MigrationRunner.TableName(toTier, toType);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var tx = _conn.BeginTransaction();
        try
        {
            // Read the source row. We read directly from SQL (rather than
            // calling Get) so the read is enlisted in the transaction.
            Entry? entry;
            using (var readCmd = _conn.CreateCommand())
            {
                readCmd.Transaction = tx;
                readCmd.CommandText = $"SELECT * FROM {fromTable} WHERE id = $id";
                readCmd.Parameters.AddWithValue("$id", id);
                using var reader = readCmd.ExecuteReader();
                entry = reader.Read() ? RowToEntry(reader) : null;
            }
            if (entry is null)
                throw new InvalidOperationException(
                    $"Entry {id} not found in {fromTable}");

            using (var insertCmd = _conn.CreateCommand())
            {
                insertCmd.Transaction = tx;
                insertCmd.CommandText = $@"
INSERT INTO {toTable}
  (id, content, summary, source, source_tool, project, tags,
   created_at, updated_at, last_accessed_at, access_count,
   decay_score, parent_id, collection_id, metadata)
VALUES
  ($id, $content, $summary, $source, $source_tool, $project, $tags,
   $created_at, $updated_at, $last_accessed_at, $access_count,
   $decay_score, $parent_id, $collection_id, $metadata)";

                insertCmd.Parameters.AddWithValue("$id", entry.Id);
                insertCmd.Parameters.AddWithValue("$content", entry.Content);
                insertCmd.Parameters.AddWithValue("$summary", ToDbString(entry.Summary));
                insertCmd.Parameters.AddWithValue("$source", ToDbString(entry.Source));
                insertCmd.Parameters.AddWithValue(
                    "$source_tool",
                    FSharpOption<SourceTool>.get_IsSome(entry.SourceTool)
                        ? SourceToolMapping.ToDbValue(entry.SourceTool.Value)
                        : (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue("$project", ToDbString(entry.Project));
                insertCmd.Parameters.AddWithValue("$tags", TagsJson.EncodeFList(entry.Tags));
                insertCmd.Parameters.AddWithValue("$created_at", entry.CreatedAt);
                insertCmd.Parameters.AddWithValue("$updated_at", now);
                insertCmd.Parameters.AddWithValue("$last_accessed_at", entry.LastAccessedAt);
                insertCmd.Parameters.AddWithValue("$access_count", entry.AccessCount);
                insertCmd.Parameters.AddWithValue("$decay_score", entry.DecayScore);
                insertCmd.Parameters.AddWithValue("$parent_id", ToDbString(entry.ParentId));
                insertCmd.Parameters.AddWithValue("$collection_id", ToDbString(entry.CollectionId));
                insertCmd.Parameters.AddWithValue("$metadata", entry.MetadataJson);

                insertCmd.ExecuteNonQuery();
            }

            using (var delCmd = _conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = $"DELETE FROM {fromTable} WHERE id = $id";
                delCmd.Parameters.AddWithValue("$id", id);
                delCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // --- row deserialization ---------------------------------------------

    /// <summary>
    /// Internal so sibling Infrastructure classes (HierarchicalIndex, etc.)
    /// can deserialize raw rows without going through the
    /// <see cref="ISqliteStore"/> CRUD interface — needed for queries that
    /// filter on <c>parent_id</c> or <c>json_extract(metadata, ...)</c>,
    /// which are not part of the standard CRUD surface.
    /// </summary>
    internal static Entry RowToEntry(SqliteDataReader reader)
    {
        string id = reader.GetString(reader.GetOrdinal("id"));
        string content = reader.GetString(reader.GetOrdinal("content"));
        var summary = ReadNullableString(reader, "summary");
        var source = ReadNullableString(reader, "source");
        var sourceToolStr = ReadNullableStringRaw(reader, "source_tool");
        var sourceTool = SourceToolMapping.Parse(sourceToolStr);
        var project = ReadNullableString(reader, "project");
        var tagsStr = ReadNullableStringRaw(reader, "tags") ?? "[]";
        var tags = TagsJson.Decode(tagsStr);
        long createdAt = reader.GetInt64(reader.GetOrdinal("created_at"));
        long updatedAt = reader.GetInt64(reader.GetOrdinal("updated_at"));
        long lastAccessedAt = reader.GetInt64(reader.GetOrdinal("last_accessed_at"));
        int accessCount = reader.GetInt32(reader.GetOrdinal("access_count"));
        double decayScore = reader.GetDouble(reader.GetOrdinal("decay_score"));
        var parentId = ReadNullableString(reader, "parent_id");
        var collectionId = ReadNullableString(reader, "collection_id");
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
            metadataJson);
    }

    private static FSharpOption<string> ReadNullableString(SqliteDataReader reader, string name)
    {
        var ord = reader.GetOrdinal(name);
        return reader.IsDBNull(ord)
            ? FSharpOption<string>.None
            : FSharpOption<string>.Some(reader.GetString(ord));
    }

    private static string? ReadNullableStringRaw(SqliteDataReader reader, string name)
    {
        var ord = reader.GetOrdinal(name);
        return reader.IsDBNull(ord) ? null : reader.GetString(ord);
    }

    private static object ToDbString(FSharpOption<string> opt) =>
        FSharpOption<string>.get_IsSome(opt) ? opt.Value : (object)DBNull.Value;

    private IReadOnlyList<Entry> ReadAll(SqliteCommand cmd)
    {
        var results = new List<Entry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(RowToEntry(reader));
        return results;
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

    // --- disposal ---------------------------------------------------------

    public void Dispose()
    {
        if (_ownsConnection)
            _conn.Dispose();
    }
}

/// <summary>
/// Bidirectional mapping between the F# <see cref="SourceTool"/> DU and the
/// string values stored in the <c>source_tool</c> column. Throws on unknown
/// strings — a silent default would mask data corruption.
/// </summary>
internal static class SourceToolMapping
{
    public static string ToDbValue(SourceTool tool)
    {
        if (tool.IsClaudeCode) return "claude-code";
        if (tool.IsCopilotCli) return "copilot-cli";
        if (tool.IsOpencode) return "opencode";
        if (tool.IsCursor) return "cursor";
        if (tool.IsCline) return "cline";
        if (tool.IsHermes) return "hermes";
        if (tool.IsManualSource) return "manual";
        throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown SourceTool");
    }

    public static FSharpOption<SourceTool> Parse(string? value)
    {
        if (value is null) return FSharpOption<SourceTool>.None;
        SourceTool tool = value switch
        {
            "claude-code" => SourceTool.ClaudeCode,
            "copilot-cli" => SourceTool.CopilotCli,
            "opencode" => SourceTool.Opencode,
            "cursor" => SourceTool.Cursor,
            "cline" => SourceTool.Cline,
            "hermes" => SourceTool.Hermes,
            "manual" => SourceTool.ManualSource,
            _ => throw new ArgumentException(
                $"Unknown source_tool value: {value}", nameof(value)),
        };
        return FSharpOption<SourceTool>.Some(tool);
    }
}

/// <summary>
/// Hand-rolled JSON codec for the <c>tags</c> column. We only ever need
/// <c>string[]</c>, so a dedicated 20-line codec is simpler (and cleaner for
/// AOT) than pulling in a source-generated
/// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>.
/// </summary>
internal static class TagsJson
{
    public static string Encode(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0) return "[]";
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < tags.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendEscaped(sb, tags[i]);
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static string EncodeFList(FSharpList<string> tags)
    {
        if (tags is null || ListModule.IsEmpty(tags)) return "[]";
        var sb = new StringBuilder();
        sb.Append('[');
        var first = true;
        foreach (var t in tags)
        {
            if (!first) sb.Append(',');
            AppendEscaped(sb, t);
            first = false;
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static List<string> Decode(string json)
    {
        var result = new List<string>();
        var span = json.AsSpan().Trim();
        if (span.Length == 0 || span[0] != '[')
            throw new FormatException("Invalid tags JSON: expected array");
        var i = 1;

        // Outer state machine: after the opening '[', we expect either an
        // element or ']'. After each element we expect either ',' or ']'.
        // After a ',' we strictly expect an element (no trailing commas).
        // Tracking this explicitly rejects malformed input like ["a""b"]
        // and ["a",].
        bool expectSeparator = false;
        bool afterComma = false;
        while (true)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            if (i >= span.Length)
                throw new FormatException("Invalid tags JSON: unterminated array");

            if (!expectSeparator)
            {
                if (span[i] == ']')
                {
                    if (afterComma)
                        throw new FormatException("Invalid tags JSON: trailing comma");
                    return result;
                }
                if (span[i] != '"')
                    throw new FormatException("Invalid tags JSON: expected string");
                i++; // consume opening quote
                var sb = new StringBuilder();
                while (i < span.Length && span[i] != '"')
                {
                    if (span[i] == '\\' && i + 1 < span.Length)
                    {
                        var esc = span[i + 1];
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (i + 5 >= span.Length)
                                    throw new FormatException("Invalid \\u escape");
                                var hex = span.Slice(i + 2, 4);
                                var unit = (char)int.Parse(hex, System.Globalization.NumberStyles.HexNumber);
                                if (char.IsHighSurrogate(unit))
                                {
                                    // Require immediately-following \uXXXX that is a low surrogate.
                                    if (i + 11 >= span.Length ||
                                        span[i + 6] != '\\' || span[i + 7] != 'u')
                                        throw new FormatException("invalid \\u escape sequence: unpaired surrogate");
                                    var hex2 = span.Slice(i + 8, 4);
                                    var unit2 = (char)int.Parse(hex2, System.Globalization.NumberStyles.HexNumber);
                                    if (!char.IsLowSurrogate(unit2))
                                        throw new FormatException("invalid \\u escape sequence: unpaired surrogate");
                                    sb.Append(unit);
                                    sb.Append(unit2);
                                    i += 10; // consume first \uXXXX body (4) + second \uXXXX (6)
                                }
                                else if (char.IsLowSurrogate(unit))
                                {
                                    throw new FormatException("invalid \\u escape sequence: unpaired surrogate");
                                }
                                else
                                {
                                    sb.Append(unit);
                                    i += 4;
                                }
                                break;
                            default:
                                throw new FormatException($"Invalid escape \\{esc}");
                        }
                        i += 2;
                    }
                    else
                    {
                        sb.Append(span[i]);
                        i++;
                    }
                }
                if (i >= span.Length)
                    throw new FormatException("Invalid tags JSON: unterminated string");
                i++; // consume closing quote
                result.Add(sb.ToString());
                expectSeparator = true;
                afterComma = false;
            }
            else
            {
                if (span[i] == ']') return result;
                if (span[i] == ',')
                {
                    i++;
                    expectSeparator = false;
                    afterComma = true;
                    continue;
                }
                throw new FormatException("Invalid tags JSON: expected ',' or ']'");
            }
        }
    }

    private static void AppendEscaped(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
