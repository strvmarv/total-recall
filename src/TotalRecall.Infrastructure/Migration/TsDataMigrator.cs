using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Migration;

/// <summary>
/// Concrete <see cref="IMigrateCommand"/>: performs the TS→.NET one-time data
/// migration. Because the TS and .NET content schemas are byte-identical
/// (same 6 content tables, same vec0 virtual tables, same FTS5 + triggers,
/// same telemetry tables — see <see cref="MigrationRunner"/>), "migration"
/// is really just a <em>re-embedding pass</em>: content rows are copied
/// verbatim, only the vec0 embeddings are regenerated with
/// <see cref="IEmbedder"/> (which now uses the canonical BERT tokenizer from
/// Core). Telemetry tables (<c>retrieval_events</c>, <c>compaction_log</c>,
/// <c>config_snapshots</c>, <c>import_log</c>, <c>benchmark_candidates</c>)
/// copy as-is without re-embedding.
/// </summary>
public sealed class TsDataMigrator : IMigrateCommand
{
    // Content tables in the canonical order. Mirrors
    // MigrationRunner.AllTablePairs string form.
    private static readonly string[] ContentTables =
    {
        "hot_memories", "warm_memories", "cold_memories",
        "hot_knowledge", "warm_knowledge", "cold_knowledge",
    };

    // Full column list for a content table, in insert order.
    private static readonly string[] ContentColumns =
    {
        "id", "content", "summary", "source", "source_tool", "project", "tags",
        "created_at", "updated_at", "last_accessed_at", "access_count",
        "decay_score", "parent_id", "collection_id", "metadata",
    };

    // (name, column list in insert order). All telemetry tables.
    private static readonly (string Name, string[] Cols)[] TelemetryTables =
    {
        ("retrieval_events", new[]
        {
            "id", "timestamp", "session_id", "query_text", "query_source",
            "query_embedding", "results", "result_count", "top_score",
            "top_tier", "top_content_type", "outcome_used", "outcome_signal",
            "config_snapshot_id", "latency_ms", "tiers_searched",
            "total_candidates_scanned",
        }),
        ("compaction_log", new[]
        {
            "id", "timestamp", "session_id", "source_tier", "target_tier",
            "source_entry_ids", "target_entry_id", "semantic_drift",
            "facts_preserved", "facts_in_original", "preservation_ratio",
            "decay_scores", "reason", "config_snapshot_id",
        }),
        ("config_snapshots", new[]
        {
            "id", "name", "timestamp", "config",
        }),
        ("import_log", new[]
        {
            "id", "timestamp", "source_tool", "source_path", "content_hash",
            "target_entry_id", "target_tier", "target_type",
        }),
        ("benchmark_candidates", new[]
        {
            "id", "query_text", "top_score", "top_result_content",
            "top_result_entry_id", "first_seen", "last_seen", "times_seen",
            "status",
        }),
    };

    private readonly IEmbedder _embedder;
    private readonly TextWriter? _progress;

    public TsDataMigrator(IEmbedder embedder, TextWriter? progress = null)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        _embedder = embedder;
        _progress = progress;
    }

    public Task<MigrationResult> MigrateAsync(
        string sourceDbPath,
        string targetDbPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceDbPath);
        ArgumentNullException.ThrowIfNull(targetDbPath);

        int migrated = 0;
        try
        {
            if (!File.Exists(sourceDbPath))
            {
                return Task.FromResult(new MigrationResult(
                    Success: false,
                    EntriesMigrated: 0,
                    ErrorMessage: $"source database not found: {sourceDbPath}"));
            }

            if (File.Exists(targetDbPath))
            {
                return Task.FromResult(new MigrationResult(
                    Success: false,
                    EntriesMigrated: 0,
                    ErrorMessage: $"target database already exists: {targetDbPath}"));
            }

            using var source = new MsSqliteConnection(
                $"Data Source={sourceDbPath};Mode=ReadOnly;Pooling=False");
            source.Open();

            using var target = Storage.SqliteConnection.Open(targetDbPath);
            MigrationRunner.RunMigrations(target);

            // Content tables: re-embed each row.
            foreach (var table in ContentTables)
            {
                ct.ThrowIfCancellationRequested();

                if (!TableExists(source, table))
                {
                    return Task.FromResult(new MigrationResult(
                        Success: false,
                        EntriesMigrated: migrated,
                        ErrorMessage: $"source database missing expected content table '{table}'"));
                }

                int rowsInTable = CopyContentTable(source, target, table, ct, ref migrated);
                _progress?.WriteLine(
                    $"  {table}: {rowsInTable} entries");
            }

            // Telemetry: verbatim copy (no re-embedding — retrieval_events
            // has a query_embedding BLOB but it's historical evidence and
            // doesn't need to match the new tokenizer's space).
            foreach (var (name, cols) in TelemetryTables)
            {
                ct.ThrowIfCancellationRequested();
                if (!TableExists(source, name))
                {
                    // Skip missing telemetry tables rather than failing:
                    // benchmark_candidates in particular was added in a
                    // later TS schema migration.
                    continue;
                }
                int n = CopyVerbatim(source, target, name, cols);
                if (n > 0)
                {
                    _progress?.WriteLine($"  {name}: {n} rows");
                }
            }

            return Task.FromResult(new MigrationResult(
                Success: true,
                EntriesMigrated: migrated,
                ErrorMessage: null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new MigrationResult(
                Success: false,
                EntriesMigrated: migrated,
                ErrorMessage: ex.Message));
        }
    }

    // --- content tables ---------------------------------------------------

    private int CopyContentTable(
        MsSqliteConnection source,
        MsSqliteConnection target,
        string table,
        CancellationToken ct,
        ref int totalMigrated)
    {
        var vecTable = table + "_vec";

        // Build the SELECT once.
        var colList = string.Join(", ", ContentColumns);
        using var selectCmd = source.CreateCommand();
        selectCmd.CommandText = $"SELECT {colList} FROM {table}";

        // Buffer rows first so we can close the reader before opening the
        // insert transaction (Microsoft.Data.Sqlite disallows concurrent
        // commands on one connection; here we use two connections anyway,
        // but buffering also keeps the code simple).
        var rows = new List<object?[]>();
        using (var reader = selectCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var row = new object?[ContentColumns.Length];
                for (int i = 0; i < ContentColumns.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }
        }

        if (rows.Count == 0)
        {
            return 0;
        }

        // Prepare INSERT statements (reused across rows under a transaction).
        var paramNames = new string[ContentColumns.Length];
        for (int i = 0; i < ContentColumns.Length; i++)
        {
            paramNames[i] = "$" + ContentColumns[i];
        }

        const int BatchSize = 100;
        int inTable = 0;

        for (int start = 0; start < rows.Count; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + BatchSize, rows.Count);

            using var tx = target.BeginTransaction();

            using var insertContent = target.CreateCommand();
            insertContent.Transaction = tx;
            insertContent.CommandText =
                $"INSERT INTO {table} ({colList}) VALUES ({string.Join(", ", paramNames)})";
            foreach (var pn in paramNames)
            {
                var p = insertContent.CreateParameter();
                p.ParameterName = pn;
                insertContent.Parameters.Add(p);
            }

            using var lastRowidCmd = target.CreateCommand();
            lastRowidCmd.Transaction = tx;
            lastRowidCmd.CommandText = "SELECT last_insert_rowid()";

            using var insertVec = target.CreateCommand();
            insertVec.Transaction = tx;
            insertVec.CommandText =
                $"INSERT INTO {vecTable} (rowid, embedding) VALUES ($rowid, $embedding)";
            var rowidParam = insertVec.CreateParameter();
            rowidParam.ParameterName = "$rowid";
            insertVec.Parameters.Add(rowidParam);
            var embeddingParam = insertVec.CreateParameter();
            embeddingParam.ParameterName = "$embedding";
            insertVec.Parameters.Add(embeddingParam);

            for (int r = start; r < end; r++)
            {
                var row = rows[r];
                for (int i = 0; i < ContentColumns.Length; i++)
                {
                    insertContent.Parameters[i].Value = row[i] ?? DBNull.Value;
                }
                insertContent.ExecuteNonQuery();

                // Fetch the rowid SQLite just assigned to the content row.
                var newRowid = (long)lastRowidCmd.ExecuteScalar()!;

                // Re-embed content. Column index 1 == "content".
                var contentText = row[1] as string ?? string.Empty;
                var vec = _embedder.Embed(contentText);

                rowidParam.Value = newRowid;
                embeddingParam.Value = FloatsToBytes(vec);
                insertVec.ExecuteNonQuery();

                inTable++;
                totalMigrated++;
            }

            tx.Commit();
        }

        return inTable;
    }

    // --- telemetry --------------------------------------------------------

    private static int CopyVerbatim(
        MsSqliteConnection source,
        MsSqliteConnection target,
        string table,
        string[] cols)
    {
        var colList = string.Join(", ", cols);
        using var selectCmd = source.CreateCommand();
        selectCmd.CommandText = $"SELECT {colList} FROM {table}";

        var rows = new List<object?[]>();
        using (var reader = selectCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var row = new object?[cols.Length];
                for (int i = 0; i < cols.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }
        }

        if (rows.Count == 0)
        {
            return 0;
        }

        var paramNames = new string[cols.Length];
        for (int i = 0; i < cols.Length; i++)
        {
            paramNames[i] = "$p" + i.ToString(CultureInfo.InvariantCulture);
        }

        using var tx = target.BeginTransaction();
        using var insertCmd = target.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText =
            $"INSERT INTO {table} ({colList}) VALUES ({string.Join(", ", paramNames)})";
        foreach (var pn in paramNames)
        {
            var p = insertCmd.CreateParameter();
            p.ParameterName = pn;
            insertCmd.Parameters.Add(p);
        }

        foreach (var row in rows)
        {
            for (int i = 0; i < cols.Length; i++)
            {
                insertCmd.Parameters[i].Value = row[i] ?? DBNull.Value;
            }
            insertCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return rows.Count;
    }

    // --- helpers ----------------------------------------------------------

    private static bool TableExists(MsSqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
        var p = cmd.CreateParameter();
        p.ParameterName = "$n";
        p.Value = name;
        cmd.Parameters.Add(p);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Reinterpret a <c>float[]</c> as its raw IEEE 754 little-endian byte
    /// layout. vec0 expects exactly <c>dim * 4</c> bytes (1536 for 384-dim
    /// MiniLM vectors). Mirrors <see cref="VectorSearch"/>'s helper.
    /// </summary>
    private static byte[] FloatsToBytes(float[] v) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<float>(v)).ToArray();
}
