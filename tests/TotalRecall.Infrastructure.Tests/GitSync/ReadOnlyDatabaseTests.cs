using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class ReadOnlyDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tr-rodb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        MsSqliteConnection.ClearAllPools();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
                Posix.Mode(d, (UnixFileMode)0b111_111_111);
            Posix.Mode(_root, (UnixFileMode)0b111_111_111);
            GitTestEnvironment.ForceDelete(_root);
        }
        catch (Exception e) when (e is IOException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
        }
    }

    private string Db(string directoryName)
    {
        var path = Path.Combine(_root, directoryName, "total-recall.db");
        SyncDb.CreateMigrated(path);
        using var conn = SyncDb.OpenMigrated(path);
        SyncDb.Exec(conn, "INSERT INTO _meta (key, value) VALUES ('git_sync_repo_id', 'the-value')");
        conn.Dispose();
        MsSqliteConnection.ClearAllPools();
        return path;
    }

    private static string? ReadValue(ReadOnlyOpen open)
    {
        Assert.NotNull(open.Connection);
        using var conn = open.Connection!;
        return new ReadOnlyMeta(conn).GetMeta("git_sync_repo_id");
    }

    private static void AssertReadsAndChangesNothing(string dbPath)
    {
        var parent = Path.GetDirectoryName(Path.GetDirectoryName(dbPath))!;
        var before = SyncDb.Snapshot(parent);
        Assert.Equal("the-value", ReadValue(ReadOnlyDatabase.Open(dbPath)));
        MsSqliteConnection.ClearAllPools();
        Assert.Equal(before, SyncDb.Snapshot(parent));
        Assert.False(File.Exists(dbPath + "-wal"));
        Assert.False(File.Exists(dbPath + "-shm"));
    }

    [Fact]
    public void ACleanlyClosedDatabase_IsReadAndNothingIsCreated()
    {
        var path = Db("plain");
        Assert.False(File.Exists(path + "-wal"), "the fixture must be cleanly closed");
        AssertReadsAndChangesNothing(path);
    }

    [Theory]
    [InlineData("a#b")]
    [InlineData("a%41")]
    [InlineData("a;b")]
    [InlineData("a b")]
    [InlineData("é")]
    [InlineData("x%y#z")]
    public void DirectoryNamesWithSpecialCharacters_AreReadAndNothingIsCreated(string name) =>
        AssertReadsAndChangesNothing(Db(name));

    [Fact]
    public void ADirectoryNamedWithAQuestionMark_IsRead_OffWindows()
    {
        if (OperatingSystem.IsWindows())
            return; // '?' is not a legal path character there
        AssertReadsAndChangesNothing(Db("a?b"));
    }

    [NonRootShFact]
    public void ACleanDatabaseInAReadOnlyDirectory_IsRead()
    {
        var path = Db("ro");
        var dir = Path.GetDirectoryName(path)!;
        Posix.Mode(dir, (UnixFileMode)0b101_101_101);
        AssertReadsAndChangesNothing(path);
    }

    [ShFact]
    public void ADatabaseReachedThroughASymbolicLink_IsRead()
    {
        var path = Db("real");
        var linkDir = Path.Combine(_root, "link");
        Directory.CreateDirectory(linkDir);
        var link = Path.Combine(linkDir, "total-recall.db");
        File.CreateSymbolicLink(link, path);
        Assert.Equal("the-value", ReadValue(ReadOnlyDatabase.Open(link)));
        MsSqliteConnection.ClearAllPools();
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(link + "-wal"));
        Assert.Single(Directory.GetFileSystemEntries(linkDir));
    }

    [ShFact]
    public void ALinkToADatabaseWithALiveWriter_ReadsTheWritersValue_AndOneSidecarIsPending()
    {
        var path = Db("reallive");
        var linkDir = Path.Combine(_root, "linklive");
        Directory.CreateDirectory(linkDir);
        var link = Path.Combine(linkDir, "total-recall.db");
        File.CreateSymbolicLink(link, path);

        using (var writer = SyncDb.OpenMigrated(path))
        {
            SyncDb.Exec(writer, "UPDATE _meta SET value = 'newer' WHERE key = 'git_sync_repo_id'");
            Assert.Equal("newer", ReadValue(ReadOnlyDatabase.Open(link)));
        }

        File.WriteAllBytes(path + "-wal", []);
        Assert.Equal(ReadOnlyDatabase.PendingWalText, ReadOnlyDatabase.Open(link).Unknown);
    }

    [Fact]
    public void ALiveWriter_IsOpenedReadOnly_AndItsLatestValueIsRead()
    {
        var path = Db("live");
        using var writer = SyncDb.OpenMigrated(path);
        SyncDb.Exec(writer, "UPDATE _meta SET value = 'newer' WHERE key = 'git_sync_repo_id'");
        Assert.True(File.Exists(path + "-wal") && File.Exists(path + "-shm"), "the writer must have both sidecars");

        var open = ReadOnlyDatabase.Open(path);
        Assert.Equal("newer", ReadValue(open));
    }

    [Fact]
    public void ExactlyOneSidecar_IsNotOpened_AndNothingIsCreated()
    {
        var path = Db("one");
        File.WriteAllBytes(path + "-wal", []);
        var before = SyncDb.Snapshot(Path.GetDirectoryName(path)!);

        var open = ReadOnlyDatabase.Open(path);

        Assert.Null(open.Connection);
        Assert.Equal(ReadOnlyDatabase.PendingWalText, open.Unknown);
        Assert.Equal(before, SyncDb.Snapshot(Path.GetDirectoryName(path)!));

        File.Delete(path + "-wal");
        File.WriteAllBytes(path + "-shm", []);
        Assert.Equal(ReadOnlyDatabase.PendingWalText, ReadOnlyDatabase.Open(path).Unknown);
    }

    [Fact]
    public void AMissingFile_GivesNothingAndNoReason_AndCreatesNothing()
    {
        Directory.CreateDirectory(_root);
        var open = ReadOnlyDatabase.Open(Path.Combine(_root, "absent", "x.db"));
        Assert.Null(open.Connection);
        Assert.Null(open.Unknown);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void AFileThatIsNotADatabase_GivesAReason()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "garbage.db");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("this is not sqlite. ", 100)));
        var open = ReadOnlyDatabase.Open(path);
        Assert.Null(open.Connection);
        Assert.False(string.IsNullOrEmpty(open.Unknown));
        MsSqliteConnection.ClearAllPools();
        Assert.False(File.Exists(path + "-wal"));
    }

    [Fact]
    public void ReadOnlyMeta_OverNoConnection_ReadsNull_AndItsSettersThrow()
    {
        var meta = new ReadOnlyMeta(null);
        Assert.Null(meta.GetMeta("x"));
        Assert.Throws<NotSupportedException>(() => meta.SetMeta("x", "y"));
        Assert.Throws<NotSupportedException>(() => meta.DeleteMeta("x"));
    }

    [Fact]
    public void ReadOnlyMeta_WithoutAMetaTable_ReadsNull_AndOtherErrorsPropagate()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "nometa.db");
        SyncDb.CreatePlain(path, "CREATE TABLE other (a TEXT)");
        using (var open = ReadOnlyDatabase.Open(path).Connection)
            Assert.Null(new ReadOnlyMeta(open).GetMeta("k"));

        // a _meta table without the expected column is a real SqliteException that is not "no such table"
        var broken = Path.Combine(_root, "brokenmeta.db");
        SyncDb.CreatePlain(broken, "CREATE TABLE _meta (key TEXT, other TEXT)");
        using var brokenOpen = ReadOnlyDatabase.Open(broken).Connection;
        Assert.Throws<SqliteException>(() => new ReadOnlyMeta(brokenOpen).GetMeta("k"));
    }

    [Fact]
    public void ImmutableUri_EncodesPercentQuestionMarkAndHash_OnBothPlatformShapes()
    {
        Assert.Equal("file:///C:/a%25b/c%3Fd%23e.db?immutable=1", ReadOnlyDatabase.ImmutableUri(@"C:\a%b\c?d#e.db", windows: true));
        Assert.Equal("file:///a%25b/c%3Fd%23e.db?immutable=1", ReadOnlyDatabase.ImmutableUri("/a%b/c?d#e.db", windows: false));
    }
}
