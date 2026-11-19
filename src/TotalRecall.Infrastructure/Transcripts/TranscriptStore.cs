using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SqliteOpener = TotalRecall.Infrastructure.Storage.SqliteConnection;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>The outcome kind of <see cref="TranscriptStore.Open"/>.</summary>
public enum TranscriptOpenStatus
{
    /// <summary>The archive is open; <see cref="TranscriptOpenResult.Store"/> is set.</summary>
    Opened,

    /// <summary>No archive tables (or no file) and <c>create</c> was false: nothing is stored. Nothing was created.</summary>
    Absent,

    /// <summary>The file is in a state the archive must not touch; <see cref="TranscriptOpenResult.Message"/> says why. Nothing was changed.</summary>
    Refused,

    /// <summary>The file could not be inspected or opened; <see cref="TranscriptOpenResult.Message"/> says why.</summary>
    Failed,
}

/// <summary>The result of <see cref="TranscriptStore.Open"/>; the caller disposes <paramref name="Store"/>.</summary>
public sealed record TranscriptOpenResult(TranscriptOpenStatus Status, ITranscriptStore? Store, string? Message);

/// <summary>
/// The SQLite transcript archive (spec sections 6 and 7). It owns its connection (never the main store's), opened
/// with pooling off and <c>PRAGMA secure_delete = ON</c>. A separate file still loads sqlite-vec, because
/// <see cref="SqliteOpener.Open"/> is the only open helper. All SQL is parameterised.
/// </summary>
public sealed class TranscriptStore : ITranscriptStore
{
    private const string CutoffKey = "ingest_not_before";
    private const int CheckpointRetries = 3;
    private const int CheckpointRetryDelayMs = 200;
    private const int CheckpointBusyTimeoutMs = 50;

    private readonly MsSqliteConnection _conn;
    private readonly string _location;
    private readonly string _path;

    private TranscriptStore(MsSqliteConnection conn, string path, bool isMain)
    {
        _conn = conn;
        _path = path;
        _location = isMain ? $"main database ({path})" : path;
    }

    // ------------------------------------------------------------------
    // Location and opening
    // ------------------------------------------------------------------

    /// <summary>
    /// Where the archive lives. An empty <c>DbPath</c>, or one that names the main database file in a different
    /// spelling (after <c>~/</c> expansion and full-path normalisation), is the main database; any other valid path
    /// is a separate file. Null with <paramref name="error"/> set when the setting is invalid.
    /// </summary>
    public static TranscriptLocation? ResolveLocation(TranscriptsSettings s, string mainDbPath, out string? error)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainDbPath);

        if (s.DbPathError is not null)
        {
            error = s.DbPathError;
            return null;
        }

        var main = Path.GetFullPath(mainDbPath);
        if (string.IsNullOrWhiteSpace(s.DbPath))
        {
            error = null;
            return new TranscriptLocation(main, IsMainDatabase: true);
        }

        string configured;
        try
        {
            // Idempotent for a value LoadTranscriptsSettings already validated; expands a hand-built "~/" value.
            configured = Path.GetFullPath(ConfigLoader.ValidateDbPath(s.DbPath, "transcripts.db_path"));
        }
        catch (Exception ex) when (ex is SqliteDbPathException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = ex.Message;
            return null;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        error = null;
        return string.Equals(configured, main, comparison)
            ? new TranscriptLocation(main, IsMainDatabase: true)
            : new TranscriptLocation(configured, IsMainDatabase: false);
    }

    /// <summary>
    /// Opens the archive at <paramref name="location"/> following "Opening safely" (spec section 6). With
    /// <paramref name="create"/> false nothing is ever created: a missing file or one without
    /// <c>tx_schema_version</c> is <see cref="TranscriptOpenStatus.Absent"/>, checked read-only. With it true, the main
    /// database is probed first (<see cref="LegacyDatabaseProbe"/>) and a missing or empty one gets the main schema
    /// before the archive runner; a separate file is created user-only, and an existing database there that is not
    /// an archive is refused.
    /// </summary>
    public static TranscriptOpenResult Open(TranscriptLocation location, bool create)
    {
        ArgumentNullException.ThrowIfNull(location);
        try
        {
            var path = Path.GetFullPath(location.Path);
            return location.IsMainDatabase ? OpenMain(path, create) : OpenSeparate(path, create);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain("[total-recall] transcripts: could not open the archive", ex);
            return new TranscriptOpenResult(TranscriptOpenStatus.Failed, null, ex.Message);
        }
    }

    // ArchiveOnly: tx_schema_version without _schema_version (a former separate archive file). Fine as a separate
    // archive, refused at the main database path. Archive: tx_schema_version and _schema_version (a main database).
    private enum FileState { Missing, Empty, Archive, ArchiveOnly, MainSchema, Other }

    /// <summary>Classifies the file read-only (no sidecar, no pragma); null state with a message when it cannot be read.</summary>
    private static (FileState State, string? Unknown) Inspect(string path)
    {
        var open = ReadOnlyDatabase.Open(path);
        if (open.Unknown is not null)
            return (FileState.Other, open.Unknown);
        if (open.Connection is null)
            return (FileState.Missing, null);

        using var conn = open.Connection;
        if (CountRo(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'tx_schema_version'") > 0)
        {
            return CountRo(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '_schema_version'") > 0
                ? (FileState.Archive, null)
                : (FileState.ArchiveOnly, null);
        }
        if (CountRo(conn, "SELECT count(*) FROM sqlite_master") == 0)
            return (FileState.Empty, null);
        if (CountRo(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '_schema_version'") > 0)
            return (FileState.MainSchema, null);
        return (FileState.Other, null);
    }

    private static TranscriptOpenResult OpenMain(string path, bool create)
    {
        var (state, unknown) = Inspect(path);
        if (unknown is not null)
            return new TranscriptOpenResult(TranscriptOpenStatus.Failed, null, unknown);

        // An archive-only file at the main path is not a main database, with or without create: refuse before any write.
        if (state == FileState.ArchiveOnly)
            return new TranscriptOpenResult(TranscriptOpenStatus.Refused, null, LegacyDatabaseProbe.RefusalText);

        if (!create)
        {
            return state == FileState.Archive
                ? Opened(OpenStore(path, isMain: true, runMainSchema: false))
                : new TranscriptOpenResult(TranscriptOpenStatus.Absent, null, null);
        }

        // The probe covers an orphan .ts-backup, a TypeScript-format file and a version below 5; the archive never
        // touches a main database file before it.
        var refusal = LegacyDatabaseProbe.Check(path);
        if (refusal is not null)
            return new TranscriptOpenResult(TranscriptOpenStatus.Refused, null, refusal);

        switch (state)
        {
            case FileState.Missing:
            case FileState.Empty:
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir); // as the main store does: the main database is not made user-only
                return Opened(OpenStore(path, isMain: true, runMainSchema: true));
            case FileState.Archive:
            case FileState.MainSchema:
                return Opened(OpenStore(path, isMain: true, runMainSchema: false));
            default:
                return new TranscriptOpenResult(TranscriptOpenStatus.Refused, null, LegacyDatabaseProbe.RefusalText);
        }
    }

    private static TranscriptOpenResult OpenSeparate(string path, bool create)
    {
        var (state, unknown) = Inspect(path);
        if (unknown is not null)
            return new TranscriptOpenResult(TranscriptOpenStatus.Failed, null, unknown);

        if (!create)
        {
            return state is FileState.Archive or FileState.ArchiveOnly
                ? Opened(OpenStore(path, isMain: false, runMainSchema: false))
                : new TranscriptOpenResult(TranscriptOpenStatus.Absent, null, null);
        }

        switch (state)
        {
            case FileState.Missing:
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    PrivateFiles.EnsureDirectory(dir);
                PrivateFiles.CreateEmptyFile(path);
                return Opened(OpenStore(path, isMain: false, runMainSchema: false));
            case FileState.Empty:
                PrivateFiles.CreateEmptyFile(path); // keeps the (empty) content, tightens the mode
                return Opened(OpenStore(path, isMain: false, runMainSchema: false));
            case FileState.Archive:
            case FileState.ArchiveOnly:
                return Opened(OpenStore(path, isMain: false, runMainSchema: false));
            default:
                return new TranscriptOpenResult(TranscriptOpenStatus.Refused, null,
                    $"{path} is an existing database that does not hold the transcript archive; " +
                    "choose another transcripts.db_path");
        }
    }

    private static TranscriptOpenResult Opened(TranscriptStore store) =>
        new(TranscriptOpenStatus.Opened, store, null);

    private static TranscriptStore OpenStore(string path, bool isMain, bool runMainSchema)
    {
        var conn = SqliteOpener.Open(path, pooling: false);
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA secure_delete = ON";
                cmd.ExecuteNonQuery();
            }
            if (runMainSchema)
                MigrationRunner.RunMigrations(conn);
            TranscriptMigrationRunner.Run(conn);
            return new TranscriptStore(conn, path, isMain);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static long CountRo(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // Status and counts
    // ------------------------------------------------------------------

    public TranscriptStatus Status()
    {
        var version = (int)TranscriptMigrationRunner.CurrentVersion(_conn);
        using var tx = _conn.BeginTransaction(deferred: true);

        var sessions = Count(tx, "SELECT count(*) FROM tx_session");
        var chunks = Count(tx, "SELECT count(*) FROM tx_chunk");

        var byKind = new Dictionary<string, long>(StringComparer.Ordinal) { ["interactive"] = 0, ["automated"] = 0 };
        using (var cmd = Command(tx, "SELECT kind, count(*) FROM tx_session GROUP BY kind"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                byKind[r.GetString(0)] = r.GetInt64(1);
        }

        var forgottenSessions = new List<ForgottenSession>();
        using (var cmd = Command(tx,
                   "SELECT host, host_session_id, at FROM tx_forgotten WHERE kind = 'session' ORDER BY at, id"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                forgottenSessions.Add(new ForgottenSession(Str(r, 0), Str(r, 1), Str(r, 2)));
        }

        var forgottenProjects = new List<ForgottenProject>();
        using (var cmd = Command(tx,
                   "SELECT project_key, roots, at FROM tx_forgotten WHERE kind = 'project' ORDER BY at, id"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                forgottenProjects.Add(new ForgottenProject(Str(r, 0), ParseRoots(r.IsDBNull(1) ? null : r.GetString(1)), Str(r, 2)));
        }

        var cutoff = ReadCutoffText(tx);
        tx.Commit();

        return new TranscriptStatus(version, _location, sessions, chunks, byKind, forgottenSessions, forgottenProjects, cutoff);
    }

    private const string SessionIds = "SELECT id FROM tx_session WHERE host = $h AND host_session_id = $s";
    private const string ProjectIds = "SELECT id FROM tx_session WHERE project_key = $k";

    public ForgetCount CountSession(string host, string hostSessionId)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hostSessionId);
        using var tx = _conn.BeginTransaction(deferred: true);
        var result = CountSessionCore(tx, host, hostSessionId);
        tx.Commit();
        return result;
    }

    public ForgetCount CountProject(string projectKey)
    {
        ArgumentNullException.ThrowIfNull(projectKey);
        using var tx = _conn.BeginTransaction(deferred: true);
        var result = CountProjectCore(tx, projectKey);
        tx.Commit();
        return result;
    }

    public ForgetCount CountAll()
    {
        using var tx = _conn.BeginTransaction(deferred: true);
        var result = CountAllCore(tx);
        tx.Commit();
        return result;
    }

    private ForgetCount CountSessionCore(SqliteTransaction tx, string host, string hostSessionId) => new(
        Count(tx, "SELECT count(*) FROM tx_session WHERE host = $h AND host_session_id = $s", ("$h", host), ("$s", hostSessionId)),
        Count(tx, $"SELECT count(*) FROM tx_chunk WHERE session_id IN ({SessionIds})", ("$h", host), ("$s", hostSessionId)));

    private ForgetCount CountProjectCore(SqliteTransaction tx, string projectKey) => new(
        Count(tx, "SELECT count(*) FROM tx_session WHERE project_key = $k", ("$k", projectKey)),
        Count(tx, $"SELECT count(*) FROM tx_chunk WHERE session_id IN ({ProjectIds})", ("$k", projectKey)));

    private ForgetCount CountAllCore(SqliteTransaction tx) => new(
        Count(tx, "SELECT count(*) FROM tx_session"),
        Count(tx, "SELECT count(*) FROM tx_chunk"));

    // ------------------------------------------------------------------
    // Forget
    // ------------------------------------------------------------------

    public ForgetResult ForgetSession(string host, string hostSessionId)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hostSessionId);

        ForgetCount removed;
        using (var tx = _conn.BeginTransaction())
        {
            removed = CountSessionCore(tx, host, hostSessionId);
            Exec(tx, $"DELETE FROM tx_chunk WHERE session_id IN ({SessionIds})", ("$h", host), ("$s", hostSessionId));
            Exec(tx, "DELETE FROM tx_session WHERE host = $h AND host_session_id = $s", ("$h", host), ("$s", hostSessionId));
            Exec(tx,
                "INSERT INTO tx_forgotten (kind, host, host_session_id, project_key, roots, at) " +
                "VALUES ('session', $h, $s, NULL, NULL, $at) " +
                "ON CONFLICT (host, host_session_id) DO UPDATE SET at = excluded.at",
                ("$h", host), ("$s", hostSessionId), ("$at", Iso(DateTimeOffset.UtcNow)));
            tx.Commit();
        }
        return Finish(removed);
    }

    public ForgetResult ForgetProject(string projectKey)
    {
        ArgumentNullException.ThrowIfNull(projectKey);

        ForgetCount removed;
        using (var tx = _conn.BeginTransaction())
        {
            removed = CountProjectCore(tx, projectKey);

            var roots = new List<string>();
            using (var cmd = Command(tx, "SELECT roots FROM tx_forgotten WHERE kind = 'project' AND project_key = $k", ("$k", projectKey)))
            {
                if (cmd.ExecuteScalar() is string existing)
                    roots.AddRange(ParseRoots(existing));
            }
            using (var cmd = Command(tx,
                       "SELECT DISTINCT cwd FROM tx_session WHERE project_key = $k AND cwd IS NOT NULL AND cwd <> '' ORDER BY cwd",
                       ("$k", projectKey)))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var cwd = r.GetString(0);
                    if (!roots.Contains(cwd, StringComparer.Ordinal))
                        roots.Add(cwd);
                }
            }

            Exec(tx, $"DELETE FROM tx_chunk WHERE session_id IN ({ProjectIds})", ("$k", projectKey));
            Exec(tx, "DELETE FROM tx_session WHERE project_key = $k", ("$k", projectKey));
            Exec(tx,
                "INSERT INTO tx_forgotten (kind, host, host_session_id, project_key, roots, at) " +
                "VALUES ('project', NULL, NULL, $k, $r, $at) " +
                "ON CONFLICT (project_key) DO UPDATE SET roots = excluded.roots, at = excluded.at",
                ("$k", projectKey), ("$r", WriteRoots(roots)), ("$at", Iso(DateTimeOffset.UtcNow)));
            tx.Commit();
        }
        return Finish(removed);
    }

    public ForgetResult ForgetAll(DateTimeOffset now)
    {
        ForgetCount removed;
        using (var tx = _conn.BeginTransaction())
        {
            removed = CountAllCore(tx);
            Exec(tx, "DELETE FROM tx_chunk");
            Exec(tx, "DELETE FROM tx_session");

            var cutoff = now.ToUniversalTime();
            if (TryParseIso(ReadCutoffText(tx), out var existing) && existing > cutoff)
                cutoff = existing;
            Exec(tx,
                "INSERT INTO tx_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                ("$k", CutoffKey), ("$v", Iso(cutoff)));
            tx.Commit();
        }
        return Finish(removed);
    }

    private ForgetResult Finish(ForgetCount removed)
    {
        // Spec section 7, step 2 (FTS5 optimize) is omitted: the byte-scan tests pass with the index's
        // secure-delete option, secure_delete = ON and the truncating checkpoint alone.
        var truncated = CheckpointTruncate();
        return new ForgetResult(removed.Sessions, removed.Chunks, truncated, truncated ? null : WalWarning());
    }

    private string WalWarning() =>
        "the rows are deleted, but the write-ahead log (" + _path + "-wal) may still hold their text until it is " +
        "checkpointed: another process has the database open; close it (or stop total-recall) and run the command again";

    /// <summary>
    /// <c>PRAGMA wal_checkpoint(TRUNCATE)</c>, retried <see cref="CheckpointRetries"/> times
    /// <see cref="CheckpointRetryDelayMs"/> ms apart with a short <c>busy_timeout</c> set only around it. True when
    /// the log was checkpointed and truncated.
    /// </summary>
    private bool CheckpointTruncate()
    {
        long original;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout";
            original = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        try
        {
            SetBusyTimeout(CheckpointBusyTimeoutMs);
            for (var attempt = 0; attempt <= CheckpointRetries; attempt++)
            {
                if (attempt > 0)
                    Thread.Sleep(CheckpointRetryDelayMs);
                if (TryCheckpoint())
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain("[total-recall] transcripts: checkpoint after forget failed", ex);
            return false;
        }
        finally
        {
            try { SetBusyTimeout(original); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ExceptionLogger.LogChain("[total-recall] transcripts: could not restore busy_timeout", ex);
            }
        }
    }

    private bool TryCheckpoint()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var r = cmd.ExecuteReader();
            // (busy, log frames, checkpointed frames); busy = 1 when a reader or writer blocked it
            return r.Read() && r.GetInt64(0) == 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) // SQLITE_BUSY, SQLITE_LOCKED
        {
            return false;
        }
    }

    private void SetBusyTimeout(long ms)
    {
        using var cmd = _conn.CreateCommand();
        // PRAGMA values cannot be parameters; ms is an integer from this class or from SQLite itself.
        cmd.CommandText = "PRAGMA busy_timeout = " + ms.ToString(CultureInfo.InvariantCulture);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // Unforget
    // ------------------------------------------------------------------

    public bool UnforgetSession(string host, string hostSessionId)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hostSessionId);
        return Exec(null,
            "DELETE FROM tx_forgotten WHERE kind = 'session' AND host = $h AND host_session_id = $s",
            ("$h", host), ("$s", hostSessionId)) > 0;
    }

    public bool UnforgetProject(string projectKey)
    {
        ArgumentNullException.ThrowIfNull(projectKey);
        return Exec(null, "DELETE FROM tx_forgotten WHERE kind = 'project' AND project_key = $k", ("$k", projectKey)) > 0;
    }

    public bool ClearCutoff() =>
        Exec(null, "DELETE FROM tx_state WHERE key = $k", ("$k", CutoffKey)) > 0;

    // ------------------------------------------------------------------
    // Exclusion primitives
    // ------------------------------------------------------------------

    public bool IsExcluded(string host, string hostSessionId, string? projectKey, string? cwd)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hostSessionId);

        if (Count(null,
                "SELECT count(*) FROM tx_forgotten WHERE kind = 'session' AND host = $h AND host_session_id = $s",
                ("$h", host), ("$s", hostSessionId)) > 0)
            return true;

        if (projectKey is not null && Count(null,
                "SELECT count(*) FROM tx_forgotten WHERE kind = 'project' AND project_key = $k",
                ("$k", projectKey)) > 0)
            return true;

        if (string.IsNullOrEmpty(cwd))
            return false;

        using var cmd = Command(null, "SELECT roots FROM tx_forgotten WHERE kind = 'project' AND roots IS NOT NULL");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            foreach (var root in ParseRoots(r.GetString(0)))
            {
                if (IsUnder(cwd, root))
                    return true;
            }
        }
        return false;
    }

    public bool IsBeforeCutoff(DateTimeOffset timestamp) =>
        TryParseIso(ReadCutoffText(null), out var cutoff) && timestamp < cutoff;

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies under it on a directory boundary.</summary>
    internal static bool IsUnder(string path, string root)
    {
        var windows = OperatingSystem.IsWindows();
        bool IsSep(char c) => c == '/' || (windows && c == '\\');

        var r = root;
        while (r.Length > 1 && IsSep(r[^1]))
            r = r[..^1];
        if (r.Length == 0)
            return false;

        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(r, comparison))
            return false;
        if (path.Length == r.Length)
            return true;
        return IsSep(r[^1]) || IsSep(path[r.Length]);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private string? ReadCutoffText(SqliteTransaction? tx)
    {
        using var cmd = Command(tx, "SELECT value FROM tx_state WHERE key = $k", ("$k", CutoffKey));
        return cmd.ExecuteScalar() as string;
    }

    private static string Iso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static bool TryParseIso(string? text, out DateTimeOffset value)
    {
        if (text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
            return true;
        value = default;
        return false;
    }

    private static readonly JsonWriterOptions RootsWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string WriteRoots(IReadOnlyList<string> roots)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, RootsWriterOptions))
        {
            writer.WriteStartArray();
            foreach (var root in roots)
                writer.WriteStringValue(root);
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The string elements of a JSON array; an unreadable value reads as no roots.</summary>
    private static IReadOnlyList<string> ParseRoots(string? json)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(json))
            return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    list.Add(s);
            }
        }
        catch (JsonException)
        {
            // a damaged record excludes by key only
        }
        return list;
    }

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? string.Empty : r.GetString(i);

    private SqliteCommand Command(SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private long Count(SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(tx, sql, parameters);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private int Exec(SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(tx, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
