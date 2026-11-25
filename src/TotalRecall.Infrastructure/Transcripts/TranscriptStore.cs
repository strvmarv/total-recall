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
public sealed partial class TranscriptStore : ITranscriptStore
{
    private const string CutoffKey = "ingest_not_before";
    private const int CheckpointRetries = 3;
    private const int CheckpointRetryDelayMs = 200;
    private const int CheckpointBusyTimeoutMs = 50;

    private readonly MsSqliteConnection _conn;
    private readonly string _location;
    private readonly string _path;
    private readonly string _userHome;

    private TranscriptStore(MsSqliteConnection conn, string path, bool isMain, string userHome)
    {
        _conn = conn;
        _path = path;
        _userHome = userHome;
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
    /// <param name="userHome">
    /// The home directory <see cref="ForgetProject"/> never records as a root; null means the UserProfile folder
    /// (an empty string when that is unknown, which skips only the home check).
    /// </param>
    /// <param name="busyTimeoutMs">
    /// A shorter (or longer) SQLite lock wait than the 5 s default, for a caller that must not block (the sync-on-read
    /// uses about 500 ms); null keeps the default.
    /// </param>
    public static TranscriptOpenResult Open(TranscriptLocation location, bool create, string? userHome = null, int? busyTimeoutMs = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        try
        {
            var path = Path.GetFullPath(location.Path);
            var home = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? string.Empty;
            return location.IsMainDatabase
                ? OpenMain(path, create, home, busyTimeoutMs)
                : OpenSeparate(path, create, home, busyTimeoutMs);
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

    private static TranscriptOpenResult OpenMain(string path, bool create, string userHome, int? busyTimeoutMs)
    {
        var (state, unknown) = Inspect(path);
        if (unknown is not null)
            return new TranscriptOpenResult(TranscriptOpenStatus.Failed, null, unknown);

        // An archive-only file at the main path is not a main database, with or without create: refuse before any write.
        if (state == FileState.ArchiveOnly)
            // Not LegacyDatabaseProbe.RefusalText: "start total-recall once" would make the migration guard take this
            // file for a TypeScript database and rename it to .ts-backup, hiding the archive.
            return new TranscriptOpenResult(TranscriptOpenStatus.Refused, null,
                $"{path} holds only a transcript archive, not a total-recall database; point TOTAL_RECALL_DB_PATH " +
                "at the main database (or set transcripts.db_path to this file)");

        if (!create)
        {
            return state == FileState.Archive
                ? Opened(OpenStore(path, isMain: true, runMainSchema: false, userHome, busyTimeoutMs))
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
                return Opened(OpenStore(path, isMain: true, runMainSchema: true, userHome, busyTimeoutMs));
            case FileState.Archive:
            case FileState.MainSchema:
                return Opened(OpenStore(path, isMain: true, runMainSchema: false, userHome, busyTimeoutMs));
            default:
                return new TranscriptOpenResult(TranscriptOpenStatus.Refused, null, LegacyDatabaseProbe.RefusalText);
        }
    }

    private static TranscriptOpenResult OpenSeparate(string path, bool create, string userHome, int? busyTimeoutMs)
    {
        var (state, unknown) = Inspect(path);
        if (unknown is not null)
            return new TranscriptOpenResult(TranscriptOpenStatus.Failed, null, unknown);

        if (!create)
        {
            return state is FileState.Archive or FileState.ArchiveOnly
                ? Opened(OpenStore(path, isMain: false, runMainSchema: false, userHome, busyTimeoutMs))
                : new TranscriptOpenResult(TranscriptOpenStatus.Absent, null, null);
        }

        switch (state)
        {
            case FileState.Missing:
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    PrivateFiles.EnsureDirectory(dir);
                PrivateFiles.CreateEmptyFile(path);
                return Opened(OpenStore(path, isMain: false, runMainSchema: false, userHome, busyTimeoutMs));
            case FileState.Empty:
                PrivateFiles.CreateEmptyFile(path); // keeps the (empty) content, tightens the mode
                return Opened(OpenStore(path, isMain: false, runMainSchema: false, userHome, busyTimeoutMs));
            case FileState.Archive:
            case FileState.ArchiveOnly:
                return Opened(OpenStore(path, isMain: false, runMainSchema: false, userHome, busyTimeoutMs));
            default:
                return new TranscriptOpenResult(TranscriptOpenStatus.Refused, null,
                    $"{path} is an existing database that does not hold the transcript archive; " +
                    "choose another transcripts.db_path");
        }
    }

    private static TranscriptOpenResult Opened(TranscriptStore store) =>
        new(TranscriptOpenStatus.Opened, store, null);

    private static TranscriptStore OpenStore(
        string path, bool isMain, bool runMainSchema, string userHome, int? busyTimeoutMs)
    {
        var conn = busyTimeoutMs is int ms
            ? SqliteOpener.Open(path, pooling: false, busyTimeoutMs: ms)
            : SqliteOpener.Open(path, pooling: false);
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
            return new TranscriptStore(conn, path, isMain, userHome);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    internal int ReadBusyTimeoutMs()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
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

        var byProjectAndKind = new List<ProjectKindCount>();
        using (var cmd = Command(tx,
                   "SELECT s.project_key, s.kind, count(DISTINCT s.id), count(c.id) " +
                   "FROM tx_session s LEFT JOIN tx_chunk c ON c.session_id = s.id " +
                   "GROUP BY s.project_key, s.kind ORDER BY count(c.id) DESC, s.project_key, s.kind"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                byProjectAndKind.Add(new ProjectKindCount(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
        }

        var cutoff = ReadCutoffText(tx);
        tx.Commit();

        return new TranscriptStatus(version, _location, sessions, chunks, byKind, forgottenSessions, forgottenProjects, cutoff)
        {
            ByProjectAndKind = byProjectAndKind,
        };
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
                ("$h", host), ("$s", hostSessionId), ("$at", TranscriptTime.Iso(DateTimeOffset.UtcNow)));
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
            // Each session's project_root (a null root records nothing: such a project is excluded by its key only).
            // A cwd outside its own root (a sibling worktree such as ../app-hotfix) is recorded too: once that worktree
            // is removed its sessions derive a path key and a root of their own, which neither the key nor the main
            // repository's root would keep out. A filesystem root, the home directory or an ancestor of it never is,
            // nor a cwd above its own root (recording it would exclude every sibling project beneath it).
            using (var cmd = Command(tx,
                       "SELECT DISTINCT project_root, cwd FROM tx_session WHERE project_key = $k AND project_root IS NOT NULL AND project_root <> '' ORDER BY project_root, cwd",
                       ("$k", projectKey)))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var root = r.GetString(0);
                    if (!roots.Contains(root, StringComparer.Ordinal))
                        roots.Add(root);

                    if (r.IsDBNull(1)) continue;
                    var cwd = r.GetString(1);
                    if (cwd.Length == 0 || IsUnder(cwd, root) || IsUnder(root, cwd)) continue;
                    var extra = ProjectIdentityRule.AcceptableCwdRoot(cwd, _userHome);
                    if (extra is not null && !roots.Contains(extra, StringComparer.Ordinal))
                        roots.Add(extra);
                }
            }

            Exec(tx, $"DELETE FROM tx_chunk WHERE session_id IN ({ProjectIds})", ("$k", projectKey));
            Exec(tx, "DELETE FROM tx_session WHERE project_key = $k", ("$k", projectKey));
            Exec(tx,
                "INSERT INTO tx_forgotten (kind, host, host_session_id, project_key, roots, at) " +
                "VALUES ('project', NULL, NULL, $k, $r, $at) " +
                "ON CONFLICT (project_key) DO UPDATE SET roots = excluded.roots, at = excluded.at",
                ("$k", projectKey), ("$r", WriteRoots(roots)), ("$at", TranscriptTime.Iso(DateTimeOffset.UtcNow)));
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
            if (TranscriptTime.TryParse(ReadCutoffText(tx), out var existing) && existing > cutoff)
                cutoff = existing;
            Exec(tx,
                "INSERT INTO tx_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                ("$k", CutoffKey), ("$v", TranscriptTime.Iso(cutoff)));
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
        return IsExcludedCore(null, host, hostSessionId, projectKey, cwd);
    }

    private bool IsExcludedCore(SqliteTransaction? tx, string host, string hostSessionId, string? projectKey, string? cwd)
    {
        if (Count(tx,
                "SELECT count(*) FROM tx_forgotten WHERE kind = 'session' AND host = $h AND host_session_id = $s",
                ("$h", host), ("$s", hostSessionId)) > 0)
            return true;

        if (projectKey is not null && Count(tx,
                "SELECT count(*) FROM tx_forgotten WHERE kind = 'project' AND project_key = $k",
                ("$k", projectKey)) > 0)
            return true;

        if (string.IsNullOrEmpty(cwd))
            return false;

        using var cmd = Command(tx, "SELECT roots FROM tx_forgotten WHERE kind = 'project' AND roots IS NOT NULL");
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

    // ------------------------------------------------------------------
    // Ingest
    // ------------------------------------------------------------------

    public SessionIngestState? GetIngestState(string host, string hostSessionId)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hostSessionId);
        return GetIngestStateCore(null, host, hostSessionId);
    }

    private SessionIngestState? GetIngestStateCore(SqliteTransaction? tx, string host, string hostSessionId)
    {
        using var cmd = Command(tx,
            "SELECT source_size, source_mtime, source_fingerprint, ingested_offset, exchange_count, settled_user_turns, " +
            "kind, project_key, project_key_source, project_root, cwd, title, started_at, ended_at, built_under_cutoff, " +
            "source_path FROM tx_session WHERE host = $h AND host_session_id = $s",
            ("$h", host), ("$s", hostSessionId));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        return new SessionIngestState(
            r.IsDBNull(0) ? null : r.GetInt64(0),
            r.IsDBNull(1) ? null : r.GetInt64(1),
            NullableStr(r, 2),
            r.GetInt64(3),
            (int)r.GetInt64(4),
            (int)r.GetInt64(5),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            NullableStr(r, 9),
            NullableStr(r, 10),
            NullableStr(r, 11),
            NullableStr(r, 12),
            NullableStr(r, 13),
            NullableStr(r, 14),
            NullableStr(r, 15));
    }

    public IngestCutoff? IngestNotBefore()
    {
        var raw = ReadCutoffText(null);
        if (raw is null)
            return null;
        // An unreadable value counts as no cutoff time (as IsBeforeCutoff treats it) but keeps its text, so a pass built
        // under it still compares equal.
        return new IngestCutoff(TranscriptTime.TryParse(raw, out var at) ? at : DateTimeOffset.MinValue, raw);
    }

    /// <summary>
    /// Rejects a pass the rebuild rule cannot apply, before any transaction: a negative first rebuilt unit, a chunk below
    /// it (it would survive the removal of vanished parts only by chance) or two chunks with the same unit and part.
    /// </summary>
    private static void ValidateChunks(SessionIngest ingest)
    {
        if (ingest.FirstRebuiltUnit < 0)
            throw new ArgumentException(
                $"FirstRebuiltUnit must not be negative (got {ingest.FirstRebuiltUnit}).", nameof(ingest));
        var seen = new HashSet<(int Start, int Part)>();
        foreach (var chunk in ingest.Chunks)
        {
            if (chunk.ExchangeStart < ingest.FirstRebuiltUnit)
                throw new ArgumentException(
                    $"Chunk at unit {chunk.ExchangeStart} is below FirstRebuiltUnit {ingest.FirstRebuiltUnit}.", nameof(ingest));
            if (!seen.Add((chunk.ExchangeStart, chunk.Part)))
                throw new ArgumentException(
                    $"Duplicate chunk at unit {chunk.ExchangeStart}, part {chunk.Part}.", nameof(ingest));
        }
    }

    public IngestOutcome ApplyIngest(SessionIngest ingest)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(ingest.Chunks);
        ValidateChunks(ingest);

        using var tx = _conn.BeginTransaction(deferred: false); // immediate: the checks and the writes see one state

        if (IsExcludedCore(tx, ingest.Host, ingest.HostSessionId, ingest.ProjectKey, ingest.Cwd))
            return new IngestOutcome(IngestOutcomeKind.Excluded);
        if (!string.Equals(ReadCutoffText(tx), ingest.BuiltUnderCutoff, StringComparison.Ordinal))
            return new IngestOutcome(IngestOutcomeKind.Stale);
        if (GetIngestStateCore(tx, ingest.Host, ingest.HostSessionId) != ingest.ExpectedPrior)
            return new IngestOutcome(IngestOutcomeKind.Conflict);

        // The session row first: the chunks reference it and foreign keys are on.
        Exec(tx,
            "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, project_root, cwd, " +
            "started_at, ended_at, kind, title, source_path, source_size, source_mtime, source_fingerprint, " +
            "ingested_offset, exchange_count, settled_user_turns, built_under_cutoff) VALUES " +
            "($host, $id, $key, $keysrc, $root, $cwd, $started, $ended, $kind, $title, $path, $size, $mtime, $fp, " +
            "$offset, $count, $turns, $cutoff) " +
            "ON CONFLICT (host, host_session_id) DO UPDATE SET project_key = excluded.project_key, " +
            "project_key_source = excluded.project_key_source, project_root = excluded.project_root, " +
            "cwd = excluded.cwd, started_at = excluded.started_at, ended_at = excluded.ended_at, " +
            "kind = excluded.kind, title = excluded.title, source_path = excluded.source_path, " +
            "source_size = excluded.source_size, source_mtime = excluded.source_mtime, " +
            "source_fingerprint = excluded.source_fingerprint, ingested_offset = excluded.ingested_offset, " +
            "exchange_count = excluded.exchange_count, settled_user_turns = excluded.settled_user_turns, " +
            "built_under_cutoff = excluded.built_under_cutoff",
            ("$host", ingest.Host), ("$id", ingest.HostSessionId), ("$key", ingest.ProjectKey),
            ("$keysrc", ingest.ProjectKeySource), ("$root", Db(ingest.ProjectRoot)), ("$cwd", Db(ingest.Cwd)),
            ("$started", Db(ingest.StartedAt)), ("$ended", Db(ingest.EndedAt)), ("$kind", ingest.Kind),
            ("$title", Db(ingest.Title)), ("$path", ingest.SourcePath), ("$size", ingest.SourceSize),
            ("$mtime", ingest.SourceMtime), ("$fp", Db(ingest.SourceFingerprint)), ("$offset", ingest.IngestedOffset),
            ("$count", ingest.ExchangeCount), ("$turns", ingest.SettledUserTurns), ("$cutoff", Db(ingest.BuiltUnderCutoff)));

        long sessionId;
        using (var cmd = Command(tx, "SELECT id FROM tx_session WHERE host = $h AND host_session_id = $s",
                   ("$h", ingest.Host), ("$s", ingest.HostSessionId)))
            sessionId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        int added = 0, changed = 0, unchanged = 0;
        var keep = new HashSet<(int Start, int Part)>();
        foreach (var chunk in ingest.Chunks)
        {
            keep.Add((chunk.ExchangeStart, chunk.Part));

            long? existingId = null;
            var same = false;
            using (var cmd = Command(tx,
                       "SELECT id, exchange_end, chunk_type, has_error, ts, redaction_version, content FROM tx_chunk " +
                       "WHERE session_id = $sid AND exchange_start = $start AND part = $part",
                       ("$sid", sessionId), ("$start", chunk.ExchangeStart), ("$part", chunk.Part)))
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    existingId = r.GetInt64(0);
                    same = r.GetInt64(1) == chunk.ExchangeEnd
                        && r.GetString(2) == chunk.ChunkType
                        && (r.GetInt64(3) != 0) == chunk.HasError
                        && string.Equals(NullableStr(r, 4), chunk.Ts, StringComparison.Ordinal)
                        && r.GetInt64(5) == chunk.RedactionVersion
                        && string.Equals(r.GetString(6), chunk.Content, StringComparison.Ordinal);
                }
            }

            if (existingId is null)
            {
                Exec(tx,
                    "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, has_error, ts, " +
                    "redaction_version, embedded, content) VALUES ($sid, $start, $end, $part, $type, $err, $ts, $rv, 0, $content)",
                    ChunkParameters(sessionId, chunk));
                added++;
            }
            else if (same)
            {
                unchanged++;
            }
            else
            {
                Exec(tx,
                    "UPDATE tx_chunk SET exchange_end = $end, chunk_type = $type, has_error = $err, ts = $ts, " +
                    "redaction_version = $rv, embedded = 0, content = $content " +
                    "WHERE session_id = $sid AND exchange_start = $start AND part = $part",
                    ChunkParameters(sessionId, chunk));
                changed++;
            }
        }

        var vanished = new List<long>();
        using (var cmd = Command(tx,
                   "SELECT id, exchange_start, part FROM tx_chunk WHERE session_id = $sid AND exchange_start >= $first",
                   ("$sid", sessionId), ("$first", ingest.FirstRebuiltUnit)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (!keep.Contains(((int)r.GetInt64(1), (int)r.GetInt64(2))))
                    vanished.Add(r.GetInt64(0));
            }
        }
        foreach (var id in vanished)
            Exec(tx, "DELETE FROM tx_chunk WHERE id = $id", ("$id", id));

        tx.Commit();
        return new IngestOutcome(IngestOutcomeKind.Applied, added, changed, unchanged, vanished.Count,
            Created: ingest.ExpectedPrior is null);
    }

    private static (string Name, object Value)[] ChunkParameters(long sessionId, IngestChunk c) => new (string, object)[]
    {
        ("$sid", sessionId), ("$start", c.ExchangeStart), ("$end", c.ExchangeEnd), ("$part", c.Part),
        ("$type", c.ChunkType), ("$err", c.HasError ? 1 : 0), ("$ts", Db(c.Ts)), ("$rv", c.RedactionVersion),
        ("$content", c.Content),
    };

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    public bool IsBeforeCutoff(DateTimeOffset timestamp) =>
        TranscriptTime.TryParse(ReadCutoffText(null), out var cutoff) && timestamp < cutoff;

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

    private static string? NullableStr(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

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

    public ITranscriptSearchReads BeginSearchReads() => new SqliteTranscriptSearchReads(_conn);

    public ITranscriptReads BeginReads() => new SqliteTranscriptReads(_conn);

    public void Dispose() => _conn.Dispose();
}
