using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Migration;

namespace TotalRecall.Server;

/// <summary>
/// Startup guard that detects a legacy TS-format database and drives a
/// one-time migration via <see cref="IMigrateCommand"/>. Idempotent: the
/// migration is recorded in the <c>_meta</c> table and subsequent runs are
/// no-ops.
/// </summary>
/// <remarks>
/// Per spec Flow 4: the old DB is renamed to <c>*.ts-backup</c>, the new DB
/// is built at the original path, and the marker key
/// <c>migration_from_ts_complete</c> is written to <c>_meta</c> on success.
/// On failure the backup is left untouched so the user can re-run.
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
    /// Check the resolved DB path and, if present and unmarked, run the
    /// TS→.NET migration. <paramref name="dbPath"/> is the fully resolved
    /// file path (honoring <c>TOTAL_RECALL_DB_PATH</c>); the caller is
    /// responsible for ensuring its parent directory exists.
    /// </summary>
    public async Task<GuardResult> CheckAndMigrateAsync(
        string dbPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        var backupPath = dbPath + BackupSuffix;

        if (!_fileExists(dbPath))
        {
            return GuardResult.NoOldDbFound;
        }

        // Peek at _meta on whatever currently sits at dbPath. On first run
        // against an unmigrated TS DB the marker is absent (migration runs);
        // on subsequent runs the marker is present (no-op). Plan 7 Task 7.-1
        // closed the prior false-positive on fresh .NET-native DBs by having
        // MigrationRunner.RunMigrations stamp the marker inside the fresh-init
        // schema transaction — so a brand-new .NET DB enters this guard already
        // carrying the marker on every startup after its creation.
        //
        // NOTE: a .NET dev DB created BEFORE Task 7.-1 landed (Plan 3b–Plan 6
        // maintainer tinkering) does not carry the marker. On such DBs this
        // guard will still false-positive and the subsequent migrate attempt
        // will fail. Those DBs must be repaired by the maintainer once —
        // either by deleting the file or by running:
        //   sqlite3 "$HOME/.total-recall/total-recall.db" \
        //     "INSERT OR IGNORE INTO _meta(key,value) VALUES('migration_from_ts_complete',strftime('%s','now'))"
        if (TryReadMarker(dbPath))
        {
            return GuardResult.AlreadyMigrated;
        }

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

    private static bool TryReadMarker(string dbPath)
    {
        // Pooling=False so the file handle is released immediately on Dispose;
        // otherwise File.Move below can fail on Windows (and is flaky under xunit
        // parallelism on Linux).
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE IF NOT EXISTS _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key = $k";
        var p = cmd.CreateParameter();
        p.ParameterName = "$k";
        p.Value = MarkerKey;
        cmd.Parameters.Add(p);
        var value = cmd.ExecuteScalar() as string;
        return !string.IsNullOrEmpty(value);
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
        vp.Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        cmd.Parameters.Add(vp);
        cmd.ExecuteNonQuery();
    }
}

public enum GuardResult
{
    NoOldDbFound,
    AlreadyMigrated,
    Migrated,
    MigrationFailed,
}
