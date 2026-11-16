using TotalRecall.Infrastructure.GitSync;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class LegacyDatabaseProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tr-legacy-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "total-recall.db");

    public LegacyDatabaseProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        MsSqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private void Backup() => File.WriteAllText(DbPath + ".ts-backup", "backup");

    private string? ProbeAndAssertUnchanged()
    {
        var before = SyncDb.Snapshot(_root);
        var result = LegacyDatabaseProbe.Check(DbPath);
        MsSqliteConnection.ClearAllPools();
        Assert.Equal(before, SyncDb.Snapshot(_root));
        return result;
    }

    private static readonly string[] SchemaVersion =
    [
        "CREATE TABLE _schema_version (version INTEGER NOT NULL, applied_at INTEGER NOT NULL)",
    ];

    private void Plain(int version, bool meta, string? marker, bool backup)
    {
        var statements = new List<string>(SchemaVersion) { $"INSERT INTO _schema_version VALUES ({version}, 0)" };
        if (meta)
        {
            statements.Add("CREATE TABLE _meta (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL)");
            if (marker is not null)
                statements.Add($"INSERT INTO _meta VALUES ('migration_from_ts_complete', '{marker}')");
        }
        SyncDb.CreatePlain(DbPath, statements.ToArray());
        if (backup)
            Backup();
    }

    [Fact]
    public void NoFile_NoBackup_Proceeds() => Assert.Null(ProbeAndAssertUnchanged());

    [Fact]
    public void NoFile_WithABackup_Refuses()
    {
        Backup();
        Assert.Equal(LegacyDatabaseProbe.RefusalText, ProbeAndAssertUnchanged());
    }

    [Fact]
    public void AnEmptyFile_Proceeds_AndWithABackupRefuses()
    {
        File.WriteAllBytes(DbPath, []);
        Assert.Null(ProbeAndAssertUnchanged());
        Backup();
        Assert.Equal(LegacyDatabaseProbe.RefusalText, ProbeAndAssertUnchanged());
    }

    [Fact]
    public void TablesWithoutASchemaVersion_Refuse()
    {
        SyncDb.CreatePlain(DbPath, "CREATE TABLE hot_memories (id TEXT)");
        Assert.Equal(LegacyDatabaseProbe.RefusalText, ProbeAndAssertUnchanged());
    }

    [Fact]
    public void TheRealTypeScriptShape_Refuses()
    {
        // _schema_version at 3, _meta, the six content tables, no marker
        var statements = new List<string>(SchemaVersion)
        {
            "INSERT INTO _schema_version VALUES (3, 0)",
            "CREATE TABLE _meta (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL)",
        };
        foreach (var t in new[] { "hot_memories", "warm_memories", "cold_memories", "hot_knowledge", "warm_knowledge", "cold_knowledge" })
            statements.Add($"CREATE TABLE {t} (id TEXT PRIMARY KEY NOT NULL, content TEXT NOT NULL)");
        SyncDb.CreatePlain(DbPath, statements.ToArray());
        Assert.Equal(LegacyDatabaseProbe.RefusalText, ProbeAndAssertUnchanged());
        Backup();
        Assert.Equal(LegacyDatabaseProbe.RefusalText, ProbeAndAssertUnchanged());
    }

    [Fact]
    public void Version4_Refuses() { Plain(4, true, "1", backup: false); Assert.Equal(LegacyDatabaseProbe.RefusalText, ProbeAndAssertUnchanged()); }

    [Fact]
    public void Version5OrMore_WithoutABackup_Proceeds_WithOrWithoutTheMarker()
    {
        Plain(5, true, null, backup: false);
        Assert.Null(ProbeAndAssertUnchanged());
        File.Delete(DbPath);
        Plain(18, true, "1700000000", backup: false);
        Assert.Null(ProbeAndAssertUnchanged());
    }

    [Fact]
    public void Version5OrMore_WithABackup_NeedsANonEmptyMarker()
    {
        Plain(18, true, "1700000000", backup: true);
        Assert.Null(ProbeAndAssertUnchanged());

        File.Delete(DbPath);
        Plain(18, true, null, backup: true);
        Assert.Equal(LegacyDatabaseProbe.RefusalText, ProbeAndAssertUnchanged());

        File.Delete(DbPath);
        Plain(18, true, "", backup: true);
        Assert.Equal(LegacyDatabaseProbe.RefusalText, ProbeAndAssertUnchanged());

        File.Delete(DbPath);
        Plain(18, false, null, backup: true);
        Assert.Equal(LegacyDatabaseProbe.RefusalText, ProbeAndAssertUnchanged());
    }

    [Fact]
    public void AFullyMigratedDatabase_Proceeds_AndIsNotChanged()
    {
        SyncDb.CreateMigrated(DbPath);
        Assert.Null(ProbeAndAssertUnchanged());
        Backup();
        Assert.Null(ProbeAndAssertUnchanged()); // a fresh .NET database carries the marker
    }

    [Fact]
    public void AGarbageFile_Throws_AndIsNotChanged()
    {
        File.WriteAllText(DbPath, string.Concat(Enumerable.Repeat("this is not sqlite. ", 100)));
        var before = SyncDb.Snapshot(_root);
        Assert.ThrowsAny<Exception>(() => LegacyDatabaseProbe.Check(DbPath));
        MsSqliteConnection.ClearAllPools();
        Assert.Equal(before, SyncDb.Snapshot(_root));
    }

    [Fact]
    public void APendingWriteAheadLog_Throws()
    {
        SyncDb.CreateMigrated(DbPath);
        File.WriteAllBytes(DbPath + "-wal", []);
        Assert.Throws<InvalidOperationException>(() => LegacyDatabaseProbe.Check(DbPath));
    }
}
