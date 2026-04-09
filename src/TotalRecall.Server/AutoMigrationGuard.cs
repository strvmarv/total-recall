using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Migration;

namespace TotalRecall.Server;

/// <summary>
/// Startup guard that detects a legacy TS-format database and drives a
/// one-time migration via <see cref="IMigrateCommand"/>. Idempotent across
/// successful AND failed prior attempts: if a previous run made it past the
/// rename but never finished migrating, this guard resumes from the
/// <c>.ts-backup</c> file rather than dead-ending on a refusal-to-overwrite.
/// </summary>
/// <remarks>
/// State machine (the cell shows the action this guard takes):
///
/// <code>
///                          | backupPath absent           | backupPath present
///   -----------------------+-----------------------------+----------------------------------
///   dbPath absent          | NoOldDbFound                | resume migrate from backup
///   dbPath = NetMigrated   | AlreadyMigrated             | AlreadyMigrated (orphan backup
///                          |                             |   left in place — harmless)
///   dbPath = TsFormat      | rename db→backup, migrate   | move db aside as .failed-migration-
///                          |                             |   &lt;ts&gt;, resume migrate from backup
///   dbPath = PartialNet*   | abort with manual-recovery  | move db aside as .failed-migration-
///                          |   instructions              |   &lt;ts&gt;, resume migrate from backup
///   dbPath = EmptyFile     | NoOldDbFound (file is just  | resume migrate from backup
///                          |   a SQLite header w/ no     |
///                          |   tables — rolled-back init)|
/// </code>
///
/// All retried-failure paths are non-destructive — nothing is ever deleted.
/// Suspect or partial files are renamed to <c>{dbPath}.failed-migration-{utc}</c>
/// so a user with unique data in them can still recover by hand.
///
/// On successful migration the marker key
/// <c>migration_from_ts_complete</c> is written to <c>_meta</c>; subsequent
/// runs short-circuit at the marker check and never touch the rename logic.
/// </remarks>
public sealed class AutoMigrationGuard
{
    /// <summary>
    /// Single source of truth lives on <see cref="MigrationRunner"/> so both
    /// the guard and the fresh-init schema path reference the same literal.
    /// </summary>
    public const string MarkerKey = TotalRecall.Infrastructure.Storage.MigrationRunner.MigrationCompleteMarkerKey;
    public const string DbFileName = "total-recall.db";
    public const string BackupSuffix = ".ts-backup";

    /// <summary>
    /// Canonical .NET content tables. Used by <see cref="InspectDbFormat"/>
    /// to count rows and decide whether a partial .NET DB is empty (safe to
    /// move aside) or populated (move aside with a louder log line).
    /// Mirrors <see cref="TotalRecall.Infrastructure.Storage.Schema"/>.TableName.
    /// </summary>
    private static readonly string[] ContentTableNames =
    {
        "hot_memories",  "warm_memories",  "cold_memories",
        "hot_knowledge", "warm_knowledge", "cold_knowledge",
    };

    private readonly IMigrateCommand _migrateCommand;
    private readonly TextWriter _stderr;
    private readonly Func<string, bool> _fileExists;
    private readonly Action<string, string> _moveFile;

    public AutoMigrationGuard(
        IMigrateCommand migrateCommand,
        TextWriter? stderr = null,
        Func<string, bool>? fileExists = null,
        Action<string, string>? moveFile = null)
    {
        _migrateCommand = migrateCommand ?? throw new ArgumentNullException(nameof(migrateCommand));
        _stderr = stderr ?? Console.Error;
        _fileExists = fileExists ?? File.Exists;
        _moveFile = moveFile ?? File.Move;
    }

    /// <summary>
    /// Inspect the current state of the data directory and run the
    /// TS→.NET migration if and only if the state machine demands it.
    /// <paramref name="dbPath"/> is the fully resolved file path (honoring
    /// <c>TOTAL_RECALL_DB_PATH</c>); the caller is responsible for ensuring
    /// its parent directory exists.
    /// </summary>
    public async Task<GuardResult> CheckAndMigrateAsync(
        string dbPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        var backupPath = dbPath + BackupSuffix;

        var dbFormat = InspectDbFormat(dbPath);
        var backupExists = _fileExists(backupPath);

        // (1) Steady state: dbPath is a fully-migrated .NET DB. Done.
        //     This case is hit on every normal startup after the first
        //     migration completes, so it must be cheap and side-effect-free.
        if (dbFormat == DbFormat.NetMigrated)
        {
            return GuardResult.AlreadyMigrated;
        }

        // (2) Nothing at dbPath. If a backup exists, the previous attempt
        //     completed the rename but never finished the migration —
        //     resume from the backup. Otherwise this is a fresh install.
        if (dbFormat == DbFormat.NotPresent)
        {
            if (backupExists)
            {
                _stderr.WriteLine(
                    "total-recall: detected orphan .ts-backup with no live database — " +
                    $"resuming migration from {Path.GetFileName(backupPath)}");
                return await RunMigrationFromBackupAsync(backupPath, dbPath, ct).ConfigureAwait(false);
            }
            return GuardResult.NoOldDbFound;
        }

        // (3) dbPath is an empty SQLite file (header only, no tables).
        //     This shape comes from a rolled-back transaction — e.g. a
        //     prior MigrationRunner.RunMigrations call that threw inside
        //     its outer transaction, leaving the file but no schema.
        //     Treat exactly like the "nothing here" case.
        if (dbFormat == DbFormat.EmptyFile)
        {
            if (backupExists)
            {
                _stderr.WriteLine(
                    "total-recall: detected empty SQLite shell at " +
                    $"{Path.GetFileName(dbPath)} (rolled-back partial init) — " +
                    $"resuming migration from {Path.GetFileName(backupPath)}");
                // The empty shell would block the migrator's File.Move-style
                // create, so move it aside non-destructively.
                if (!TryMoveAside(dbPath, "empty-shell")) return GuardResult.MigrationFailed;
                return await RunMigrationFromBackupAsync(backupPath, dbPath, ct).ConfigureAwait(false);
            }
            return GuardResult.NoOldDbFound;
        }

        // (4) Populated TS-format DB at dbPath. The normal first-run path,
        //     OR a recovery path when a previous attempt left both files.
        if (dbFormat == DbFormat.TsFormat)
        {
            if (!backupExists)
            {
                // Fresh first-run TS-format migration.
                _stderr.WriteLine("total-recall: detected existing TS-format database, migrating...");
                try
                {
                    _moveFile(dbPath, backupPath);
                }
                catch (Exception ex)
                {
                    _stderr.WriteLine($"total-recall: migration failed: could not rename old database: {ex.Message}");
                    return GuardResult.MigrationFailed;
                }
                return await RunMigrationFromBackupAsync(backupPath, dbPath, ct).ConfigureAwait(false);
            }

            // Both files exist. The .ts-backup is authoritative because the
            // guard NEVER creates a .ts-backup except by renaming dbPath, so
            // its existence proves dbPath was once renamed there. The current
            // dbPath must therefore be either (a) a partial migration result,
            // or (b) something the user dropped in by hand. Either way the
            // backup wins; the current file is moved aside non-destructively.
            _stderr.WriteLine(
                "total-recall: detected previous incomplete migration attempt — " +
                $"the existing {Path.GetFileName(backupPath)} is authoritative; " +
                $"setting current {Path.GetFileName(dbPath)} aside as failed-migration-<utc> and resuming");
            if (!TryMoveAside(dbPath, "ts-after-backup")) return GuardResult.MigrationFailed;
            return await RunMigrationFromBackupAsync(backupPath, dbPath, ct).ConfigureAwait(false);
        }

        // (5) Partial .NET DB at dbPath (has _schema_version but no marker).
        //     Two sub-cases differing only in the log message — the action
        //     is identical: move the partial aside, resume from backup.
        //     If no backup exists we can't auto-recover and bail with manual
        //     instructions. This is the maintainer-tinkering case from the
        //     pre-existing code comment (a fresh .NET dev DB created before
        //     the marker-stamp landed in MigrationRunner).
        if (dbFormat == DbFormat.PartialNetEmpty || dbFormat == DbFormat.PartialNetPopulated)
        {
            if (!backupExists)
            {
                _stderr.WriteLine(
                    "total-recall: migration guard cannot proceed — " +
                    $"{Path.GetFileName(dbPath)} appears to be a partial .NET database " +
                    "(has _schema_version but no migration_from_ts_complete marker), " +
                    "and no .ts-backup exists to migrate from. If this is actually a " +
                    "valid .NET DB created by a build that pre-dates the marker stamp " +
                    "(MigrationRunner.RunMigrations Plan 7 Task 7.-1), repair it once " +
                    "by running:");
                _stderr.WriteLine(
                    $"  sqlite3 \"{dbPath}\" \"INSERT OR IGNORE INTO _meta(key,value) " +
                    "VALUES('migration_from_ts_complete',strftime('%s','now'))\"");
                return GuardResult.MigrationFailed;
            }

            var label = dbFormat == DbFormat.PartialNetPopulated ? "populated" : "empty";
            _stderr.WriteLine(
                $"total-recall: detected {label} partial .NET database from a prior " +
                $"failed migration — moving {Path.GetFileName(dbPath)} aside as " +
                $"failed-migration-<utc> (NOT deleted) and resuming from {Path.GetFileName(backupPath)}");
            if (!TryMoveAside(dbPath, $"partial-net-{label}")) return GuardResult.MigrationFailed;
            return await RunMigrationFromBackupAsync(backupPath, dbPath, ct).ConfigureAwait(false);
        }

        // Unreachable — every DbFormat value is handled above. Defensive bail.
        _stderr.WriteLine($"total-recall: migration guard internal error: unhandled DbFormat={dbFormat}");
        return GuardResult.MigrationFailed;
    }

    /// <summary>
    /// Drives <see cref="IMigrateCommand"/>, writes the completion marker,
    /// and emits the user-facing log lines. Shared by every state-machine
    /// branch that ends in "actually run the migration."
    /// </summary>
    private async Task<GuardResult> RunMigrationFromBackupAsync(
        string backupPath,
        string dbPath,
        CancellationToken ct)
    {
        MigrationResult result;
        try
        {
            result = await _migrateCommand.MigrateAsync(backupPath, dbPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"total-recall: migration failed: {ex.Message}");
            return GuardResult.MigrationFailed;
        }

        if (!result.Success)
        {
            _stderr.WriteLine($"total-recall: migration failed: {result.ErrorMessage ?? "unknown error"}");
            return GuardResult.MigrationFailed;
        }

        try
        {
            WriteMarker(dbPath);
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"total-recall: migration failed: could not write completion marker: {ex.Message}");
            return GuardResult.MigrationFailed;
        }

        _stderr.WriteLine($"total-recall: migration complete, {result.EntriesMigrated} entries migrated");
        return GuardResult.Migrated;
    }

    /// <summary>
    /// Move <paramref name="dbPath"/> to a uniquely-named
    /// <c>.failed-migration-{utc}</c> sibling. Used to non-destructively
    /// preserve any file that's blocking a resume. Returns false on rename
    /// failure (caller logs and bails).
    /// </summary>
    private bool TryMoveAside(string dbPath, string reason)
    {
        var asidePath = $"{dbPath}.failed-migration-{DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
        try
        {
            _moveFile(dbPath, asidePath);
            return true;
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"total-recall: migration failed: could not set aside {reason} database: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Read-only inspection of the file at <paramref name="dbPath"/> to
    /// classify it into one of the <see cref="DbFormat"/> states. Opens
    /// SQLite in <c>Mode=ReadOnly</c> so the file is never mutated by the
    /// inspection itself — the previous TryReadMarker implementation
    /// CREATE-IF-NOT-EXISTS-ed the <c>_meta</c> table on every peek, which
    /// silently mutated TS-era DBs and complicated reasoning.
    /// </summary>
    private DbFormat InspectDbFormat(string dbPath)
    {
        if (!_fileExists(dbPath)) return DbFormat.NotPresent;

        SqliteConnection conn;
        try
        {
            conn = new SqliteConnection($"Data Source={dbPath};Pooling=False;Mode=ReadOnly");
            conn.Open();
        }
        catch
        {
            // File exists but can't be opened as a SQLite database — could
            // be a zero-byte file or arbitrary garbage. Treat as empty so
            // the state machine can move it aside and recover from backup
            // if one exists.
            return DbFormat.EmptyFile;
        }

        try
        {
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            // Marker check first — if the migration completed, every other
            // signal is irrelevant.
            if (tables.Contains("_meta") && TryReadMarkerValue(conn))
            {
                return DbFormat.NetMigrated;
            }

            // Discriminator: presence of _schema_version table identifies
            // this as a .NET-format DB regardless of completion state.
            // (TS-format DBs never had _schema_version. The marker check
            // above already excluded fully-migrated .NET DBs, so anything
            // landing here with _schema_version is partial/incomplete.)
            if (tables.Contains("_schema_version"))
            {
                return CountContentRows(conn, tables) > 0
                    ? DbFormat.PartialNetPopulated
                    : DbFormat.PartialNetEmpty;
            }

            // No .NET schema marker, no _schema_version. If there are any
            // user tables at all, treat as TS-format. If there are no tables
            // (just a SQLite header from a rolled-back transaction), treat
            // as empty.
            return tables.Count > 0 ? DbFormat.TsFormat : DbFormat.EmptyFile;
        }
        finally
        {
            conn.Dispose();
        }
    }

    private static bool TryReadMarkerValue(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key = $k";
        var p = cmd.CreateParameter();
        p.ParameterName = "$k";
        p.Value = MarkerKey;
        cmd.Parameters.Add(p);
        try
        {
            return cmd.ExecuteScalar() is string v && !string.IsNullOrEmpty(v);
        }
        catch
        {
            return false;
        }
    }

    private static long CountContentRows(SqliteConnection conn, HashSet<string> tables)
    {
        long total = 0;
        foreach (var name in ContentTableNames)
        {
            if (!tables.Contains(name)) continue;
            using var cmd = conn.CreateCommand();
            // Safe: name is from the static ContentTableNames literal.
            cmd.CommandText = $"SELECT COUNT(*) FROM {name}";
            try
            {
                if (cmd.ExecuteScalar() is long l) total += l;
            }
            catch
            {
                // Table malformed or unreadable — skip it.
            }
        }
        return total;
    }

    private static void WriteMarker(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE IF NOT EXISTS _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO _meta (key, value) VALUES ($k, $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        var kp = cmd.CreateParameter();
        kp.ParameterName = "$k";
        kp.Value = MarkerKey;
        cmd.Parameters.Add(kp);
        var vp = cmd.CreateParameter();
        vp.ParameterName = "$v";
        vp.Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        cmd.Parameters.Add(vp);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Internal classification of the file currently at <c>dbPath</c>.
    /// Used only by the state machine in <see cref="CheckAndMigrateAsync"/>.
    /// </summary>
    private enum DbFormat
    {
        /// <summary>File does not exist.</summary>
        NotPresent,

        /// <summary>
        /// File exists but contains no recognizable tables — either a
        /// zero-byte file, garbage, or a SQLite shell from a rolled-back
        /// CREATE-TABLE transaction.
        /// </summary>
        EmptyFile,

        /// <summary>
        /// TS-era SQLite database: at least one user table is present and
        /// <c>_schema_version</c> is absent.
        /// </summary>
        TsFormat,

        /// <summary>
        /// .NET schema is present (<c>_schema_version</c> exists), no
        /// migration-complete marker, and zero rows in any of the canonical
        /// content tables. Indicates a partial init that succeeded at
        /// schema creation but never wrote any data.
        /// </summary>
        PartialNetEmpty,

        /// <summary>
        /// Same as <see cref="PartialNetEmpty"/> but at least one row exists
        /// in the canonical content tables. Indicates a migration that ran
        /// far enough to write data but failed before stamping the marker.
        /// </summary>
        PartialNetPopulated,

        /// <summary>
        /// Fully-migrated .NET DB: <c>_meta</c> contains the
        /// <c>migration_from_ts_complete</c> key.
        /// </summary>
        NetMigrated,
    }
}

public enum GuardResult
{
    NoOldDbFound,
    AlreadyMigrated,
    Migrated,
    MigrationFailed,
}
