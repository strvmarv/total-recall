using TotalRecall.Infrastructure.GitSync;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncIssuesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tr-issues-" + Guid.NewGuid().ToString("N"));
    private readonly MsSqliteConnection _conn;

    public GitSyncIssuesTests() => _conn = SyncDb.OpenMigrated(Path.Combine(_dir, "total-recall.db"));

    public void Dispose()
    {
        _conn.Dispose();
        MsSqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Upsert_ReplacesDetailAttemptsAndHash()
    {
        GitSyncIssues.Upsert(_conn, "3f9c2a7e", GitSyncIssues.ApplyFailed, "h1", 1, "first");
        GitSyncIssues.Upsert(_conn, "3f9c2a7e", GitSyncIssues.ApplyFailed, "h2", 2, "second");

        var row = GitSyncIssues.Read(_conn, "3f9c2a7e", GitSyncIssues.ApplyFailed)!;
        Assert.Equal(new IssueRow("3f9c2a7e", "apply-failed", "h2", 2, "second"), row);
    }

    [Fact]
    public void Upsert_KeepsOneRowPerKind_AndNullsRoundTrip()
    {
        GitSyncIssues.Upsert(_conn, "k", GitSyncIssues.ApplyFailed, null, 0, null);
        GitSyncIssues.Upsert(_conn, "k", GitSyncIssues.RejectedRecord, "blob", 0, "symlink");

        Assert.Equal(new IssueRow("k", "apply-failed", null, 0, null), GitSyncIssues.Read(_conn, "k", GitSyncIssues.ApplyFailed));
        Assert.Equal("symlink", GitSyncIssues.Read(_conn, "k", GitSyncIssues.RejectedRecord)!.Detail);
    }

    [Fact]
    public void ReadOpen_ReturnsOnlyTheKind_ByEntryId()
    {
        GitSyncIssues.Upsert(_conn, "a", GitSyncIssues.ApplyFailed, null, 1, "x");
        GitSyncIssues.Upsert(_conn, "b", GitSyncIssues.RejectedRecord, null, 0, "y");
        GitSyncIssues.Upsert(_conn, "c", GitSyncIssues.ApplyFailed, null, 3, "z");

        var open = GitSyncIssues.ReadOpen(_conn, GitSyncIssues.ApplyFailed);

        Assert.Equal(["a", "c"], open.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, open["c"].Attempts);
    }

    [Fact]
    public void Delete_RemovesOnlyThatKind_AndAMissingRowIsANoOp()
    {
        GitSyncIssues.Upsert(_conn, "a", GitSyncIssues.ApplyFailed, null, 1, "x");
        GitSyncIssues.Upsert(_conn, "a", GitSyncIssues.RejectedRecord, null, 0, "y");

        GitSyncIssues.Delete(_conn, "a", GitSyncIssues.ApplyFailed);
        GitSyncIssues.Delete(_conn, "missing", GitSyncIssues.ApplyFailed);

        Assert.Null(GitSyncIssues.Read(_conn, "a", GitSyncIssues.ApplyFailed));
        Assert.NotNull(GitSyncIssues.Read(_conn, "a", GitSyncIssues.RejectedRecord));
    }

    [Fact]
    public void SyncSql_GetMetaAndSetMeta_RoundTrip()
    {
        Assert.Null(SyncSql.GetMeta(_conn, "x"));
        SyncSql.SetMeta(_conn, "x", "1");
        SyncSql.SetMeta(_conn, "x", "2");

        Assert.Equal("2", SyncSql.GetMeta(_conn, "x"));
    }
}
