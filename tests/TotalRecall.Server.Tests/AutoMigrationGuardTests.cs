using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Migration;
using TotalRecall.Server;
using Xunit;

namespace TotalRecall.Server.Tests;

public sealed class AutoMigrationGuardTests : IDisposable
{
    private readonly string _tempDir;

    public AutoMigrationGuardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            // Give SQLite pooling a beat to release file handles on Windows; on
            // Linux this is a no-op and harmless.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private string DbPath => Path.Combine(_tempDir, AutoMigrationGuard.DbFileName);
    private string BackupPath => DbPath + AutoMigrationGuard.BackupSuffix;

    private static void SeedDb(string path, bool withMarker)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS memory_entries (id TEXT PRIMARY KEY, content TEXT)";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO memory_entries (id, content) VALUES ('a', 'hello')";
            cmd.ExecuteNonQuery();
        }
        if (withMarker)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); " +
                "INSERT OR REPLACE INTO _meta (key, value) VALUES ('migration_from_ts_complete', '123')";
            cmd.ExecuteNonQuery();
        }
    }

    private static bool DbHasMarker(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key = 'migration_from_ts_complete'";
        try
        {
            var v = cmd.ExecuteScalar() as string;
            return !string.IsNullOrEmpty(v);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    [Fact]
    public async Task NoOldDb_Returns_NoOldDbFound()
    {
        var fake = new FakeMigrateCommand();
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var result = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.NoOldDbFound, result);
        Assert.Equal(0, fake.CallCount);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task AlreadyMigrated_MarkerPresent_ReturnsAlreadyMigrated()
    {
        SeedDb(DbPath, withMarker: true);
        var fake = new FakeMigrateCommand();
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var result = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.AlreadyMigrated, result);
        Assert.Equal(0, fake.CallCount);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public async Task OldDbPresent_NoMarker_RunsMigration_WritesMarker()
    {
        SeedDb(DbPath, withMarker: false);
        var fake = new FakeMigrateCommand
        {
            Result = new MigrationResult(Success: true, EntriesMigrated: 7, ErrorMessage: null),
            OnInvoke = (src, dst) =>
            {
                // Simulate Plan 5 producing a fresh target DB at dst.
                using var conn = new SqliteConnection($"Data Source={dst}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS memory_entries (id TEXT PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            },
        };
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var result = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.Migrated, result);
        Assert.Equal(1, fake.CallCount);
        Assert.Equal(BackupPath, fake.LastSource);
        Assert.Equal(DbPath, fake.LastTarget);
        Assert.True(File.Exists(BackupPath));
        Assert.True(File.Exists(DbPath));
        Assert.True(DbHasMarker(DbPath));

        var log = stderr.ToString();
        Assert.Contains("total-recall: detected existing TS-format database, migrating...", log);
        Assert.Contains("total-recall: migration complete, 7 entries migrated", log);
    }

    [Fact]
    public async Task OldDbPresent_MigrateFails_ReturnsMigrationFailed_NoMarker()
    {
        SeedDb(DbPath, withMarker: false);
        var fake = new FakeMigrateCommand
        {
            Result = new MigrationResult(Success: false, EntriesMigrated: 0, ErrorMessage: "boom"),
        };
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var result = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.MigrationFailed, result);
        Assert.Equal(1, fake.CallCount);
        Assert.True(File.Exists(BackupPath)); // untouched — not deleted, not restored
        Assert.False(DbHasMarker(BackupPath));
        Assert.Contains("total-recall: migration failed: boom", stderr.ToString());
    }

    [Fact]
    public async Task FreshDotNetDb_InitializedByMigrationRunner_ReturnsAlreadyMigrated()
    {
        // Plan 7 Task 7.-1 regression guard. A DB produced by the real
        // MigrationRunner.RunMigrations (i.e. a fresh .NET-native DB, the
        // exact shape a brand-new install creates) must be recognised as
        // already migrated — historically the guard false-positived on it
        // and crashed the migrator with a disk-I/O error.
        using (var seed = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(DbPath))
        {
            TotalRecall.Infrastructure.Storage.MigrationRunner.RunMigrations(seed);
        }
        SqliteConnection.ClearAllPools();

        var fake = new FakeMigrateCommand();
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var result = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.AlreadyMigrated, result);
        Assert.Equal(0, fake.CallCount);
        Assert.False(File.Exists(BackupPath));
        Assert.Equal(string.Empty, stderr.ToString());
    }

    // ----------------------------------------------------------------------
    // State-machine resume tests (the cliff that beta.6 hit on a real user
    // box). All five non-trivial transitions are exercised below; each one
    // documents which (dbPath state, backupPath presence) cell of the table
    // in AutoMigrationGuard's xmldoc it covers.
    // ----------------------------------------------------------------------

    /// <summary>
    /// Seeds a SQLite file that mimics a "partial .NET DB" — has the
    /// _schema_version table that the .NET schema creates, but no
    /// migration_from_ts_complete marker. Optionally inserts a row in
    /// hot_memories so the inspector classifies it as Populated rather
    /// than Empty.
    /// </summary>
    private static void SeedPartialNetDb(string path, bool withRowInContentTable)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE _schema_version (version INTEGER NOT NULL, applied_at INTEGER NOT NULL);" +
                "INSERT INTO _schema_version (version, applied_at) VALUES (1, 0);" +
                "CREATE TABLE hot_memories (id TEXT PRIMARY KEY, content TEXT)";
            cmd.ExecuteNonQuery();
        }
        if (withRowInContentTable)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO hot_memories (id, content) VALUES ('partial', 'half-migrated row')";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns the count of files in the data directory whose name matches
    /// the failed-migration sideline pattern. Used to assert that the guard
    /// preserved (rather than deleted) any file it set aside.
    /// </summary>
    private int FailedMigrationSidelineCount() =>
        Directory.GetFiles(_tempDir)
            .Count(f => Path.GetFileName(f).StartsWith(AutoMigrationGuard.DbFileName + ".failed-migration-", StringComparison.Ordinal));

    [Fact]
    public async Task Resume_TsAtDb_BackupExists_MovesDbAside_AndRetriesFromBackup()
    {
        // Cell: dbPath = TsFormat, backupPath present
        // Expected: dbPath moved to .failed-migration-<utc>, migration runs
        // from backup, marker stamped at dbPath, both backup AND sideline
        // remain on disk (nothing deleted).

        // Set up: a backup that the guard will treat as authoritative…
        SeedDb(BackupPath, withMarker: false);
        // …and a current dbPath that mimics another TS-format file (e.g.
        // a previous failed migration left it behind, or the user dropped
        // a stale file in by hand).
        SeedDb(DbPath, withMarker: false);

        var fake = new FakeMigrateCommand
        {
            Result = new MigrationResult(Success: true, EntriesMigrated: 11, ErrorMessage: null),
            OnInvoke = (src, dst) =>
            {
                using var conn = new SqliteConnection($"Data Source={dst}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE memory_entries (id TEXT PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            },
        };
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var result = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.Migrated, result);
        Assert.Equal(1, fake.CallCount);
        Assert.Equal(BackupPath, fake.LastSource); // migrated FROM the existing backup
        Assert.Equal(DbPath, fake.LastTarget);
        Assert.True(File.Exists(BackupPath)); // backup still on disk
        Assert.True(File.Exists(DbPath));     // freshly built by migrator
        Assert.True(DbHasMarker(DbPath));
        Assert.Equal(1, FailedMigrationSidelineCount()); // the previous dbPath was preserved
        Assert.Contains("previous incomplete migration attempt", stderr.ToString());
    }

    [Fact]
    public async Task Resume_PartialNetEmpty_BackupExists_MovesAsideAndRetries()
    {
        // Cell: dbPath = PartialNetEmpty, backupPath present
        // The exact bug the user hit on beta.6: a half-built .NET DB at
        // dbPath (12k file with _schema_version but no rows) plus a
        // .ts-backup left over from an earlier successful rename.

        SeedDb(BackupPath, withMarker: false);
        SeedPartialNetDb(DbPath, withRowInContentTable: false);

        var fake = new FakeMigrateCommand
        {
            Result = new MigrationResult(Success: true, EntriesMigrated: 9, ErrorMessage: null),
            OnInvoke = (src, dst) =>
            {
                using var conn = new SqliteConnection($"Data Source={dst}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE memory_entries (id TEXT PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            },
        };
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var result = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.Migrated, result);
        Assert.Equal(1, fake.CallCount);
        Assert.Equal(BackupPath, fake.LastSource);
        Assert.True(DbHasMarker(DbPath));
        Assert.Equal(1, FailedMigrationSidelineCount());
        var log = stderr.ToString();
        Assert.Contains("empty partial .NET database", log);
        Assert.Contains("NOT deleted", log);
    }

    [Fact]
    public async Task Resume_PartialNetPopulated_BackupExists_MovesAsideAndRetries()
    {
        // Cell: dbPath = PartialNetPopulated, backupPath present
        // Same recovery action as the empty variant, just a different log
        // line so the user knows we found data in the partial file.

        SeedDb(BackupPath, withMarker: false);
        SeedPartialNetDb(DbPath, withRowInContentTable: true);

        var fake = new FakeMigrateCommand
        {
            Result = new MigrationResult(Success: true, EntriesMigrated: 5, ErrorMessage: null),
            OnInvoke = (src, dst) =>
            {
                using var conn = new SqliteConnection($"Data Source={dst}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE memory_entries (id TEXT PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            },
        };
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var result = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.Migrated, result);
        Assert.Equal(1, FailedMigrationSidelineCount());
        var log = stderr.ToString();
        Assert.Contains("populated partial .NET database", log);
        Assert.Contains("NOT deleted", log);
    }

    [Fact]
    public async Task Resume_PartialNetEmpty_NoBackup_BailsWithManualInstructions()
    {
        // Cell: dbPath = PartialNetEmpty (or Populated), backupPath absent
        // Cannot auto-recover. Bail with the sqlite3-INSERT one-liner so
        // the maintainer-tinkering case (pre-Plan-7-Task-7.-1 dev DB) has
        // a documented manual repair path.

        SeedPartialNetDb(DbPath, withRowInContentTable: false);

        var fake = new FakeMigrateCommand();
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var result = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.MigrationFailed, result);
        Assert.Equal(0, fake.CallCount);
        Assert.True(File.Exists(DbPath)); // not moved
        Assert.False(File.Exists(BackupPath));
        Assert.Equal(0, FailedMigrationSidelineCount());
        var log = stderr.ToString();
        Assert.Contains("partial .NET database", log);
        Assert.Contains("INSERT OR IGNORE INTO _meta", log);
    }

    [Fact]
    public async Task Resume_NoDb_BackupOnly_RunsMigrationFromBackup()
    {
        // Cell: dbPath absent, backupPath present
        // A previous attempt completed the rename but the migrator never
        // ran (or crashed before producing any output file). Resume.

        SeedDb(BackupPath, withMarker: false);
        Assert.False(File.Exists(DbPath));

        var fake = new FakeMigrateCommand
        {
            Result = new MigrationResult(Success: true, EntriesMigrated: 13, ErrorMessage: null),
            OnInvoke = (src, dst) =>
            {
                using var conn = new SqliteConnection($"Data Source={dst}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE memory_entries (id TEXT PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            },
        };
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var result = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.Migrated, result);
        Assert.Equal(1, fake.CallCount);
        Assert.Equal(BackupPath, fake.LastSource);
        Assert.True(DbHasMarker(DbPath));
        Assert.Contains("orphan .ts-backup with no live database", stderr.ToString());
    }

    [Fact]
    public async Task EmptyShellAtDb_BackupExists_MovesShellAsideAndResumes()
    {
        // Cell: dbPath = EmptyFile (rolled-back partial init), backupPath present
        // The empty shell shape comes from a transaction that began the
        // schema CREATE then rolled back — the file exists but has no
        // user tables. Move it aside and resume from backup.

        // Make an empty SQLite file by opening a connection and immediately
        // closing it without creating any tables.
        using (var seed = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            seed.Open();
        }
        SqliteConnection.ClearAllPools();
        Assert.True(File.Exists(DbPath));

        SeedDb(BackupPath, withMarker: false);

        var fake = new FakeMigrateCommand
        {
            Result = new MigrationResult(Success: true, EntriesMigrated: 4, ErrorMessage: null),
            OnInvoke = (src, dst) =>
            {
                using var conn = new SqliteConnection($"Data Source={dst}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE memory_entries (id TEXT PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            },
        };
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var result = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.Migrated, result);
        Assert.Equal(1, FailedMigrationSidelineCount());
        Assert.Contains("empty SQLite shell", stderr.ToString());
    }

    [Fact]
    public async Task RunTwice_IsIdempotent()
    {
        SeedDb(DbPath, withMarker: false);
        var fake = new FakeMigrateCommand
        {
            Result = new MigrationResult(Success: true, EntriesMigrated: 3, ErrorMessage: null),
            OnInvoke = (src, dst) =>
            {
                using var conn = new SqliteConnection($"Data Source={dst}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS memory_entries (id TEXT PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            },
        };
        var stderr = new StringWriter();
        var guard = new AutoMigrationGuard(fake, stderr);

        var first = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);
        var second = await guard.CheckAndMigrateAsync(DbPath, CancellationToken.None);

        Assert.Equal(GuardResult.Migrated, first);
        Assert.Equal(GuardResult.AlreadyMigrated, second);
        Assert.Equal(1, fake.CallCount);
    }

    private sealed class FakeMigrateCommand : IMigrateCommand
    {
        public int CallCount { get; private set; }
        public string? LastSource { get; private set; }
        public string? LastTarget { get; private set; }
        public MigrationResult Result { get; set; } =
            new(Success: true, EntriesMigrated: 0, ErrorMessage: null);
        public Action<string, string>? OnInvoke { get; set; }

        public Task<MigrationResult> MigrateAsync(string sourceDbPath, string targetDbPath, CancellationToken ct)
        {
            CallCount++;
            LastSource = sourceDbPath;
            LastTarget = targetDbPath;
            OnInvoke?.Invoke(sourceDbPath, targetDbPath);
            return Task.FromResult(Result);
        }
    }
}
