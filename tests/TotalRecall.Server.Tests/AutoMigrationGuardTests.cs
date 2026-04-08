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

        var result = await guard.CheckAndMigrateAsync(_tempDir, CancellationToken.None);

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

        var result = await guard.CheckAndMigrateAsync(_tempDir, CancellationToken.None);

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

        var result = await guard.CheckAndMigrateAsync(_tempDir, CancellationToken.None);

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

        var result = await guard.CheckAndMigrateAsync(_tempDir, CancellationToken.None);

        Assert.Equal(GuardResult.MigrationFailed, result);
        Assert.Equal(1, fake.CallCount);
        Assert.True(File.Exists(BackupPath)); // untouched — not deleted, not restored
        Assert.False(DbHasMarker(BackupPath));
        Assert.Contains("total-recall: migration failed: boom", stderr.ToString());
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

        var first = await guard.CheckAndMigrateAsync(_tempDir, CancellationToken.None);
        var second = await guard.CheckAndMigrateAsync(_tempDir, CancellationToken.None);

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
