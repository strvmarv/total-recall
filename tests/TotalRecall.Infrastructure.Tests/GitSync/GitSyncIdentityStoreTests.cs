using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncIdentityStoreTests : IDisposable
{
    private readonly MsSqliteConnection _conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
    private readonly SqliteStore _store;

    public GitSyncIdentityStoreTests()
    {
        MigrationRunner.RunMigrations(_conn);
        _store = new SqliteStore(_conn);
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public void Read_ReturnsTheThreeKeys()
    {
        _store.SetMeta(SyncIdentity.MetaKeys.Remote, "git@github.com:o/r.git");
        _store.SetMeta(SyncIdentity.MetaKeys.RepoId, "0123456789abcdef0123456789abcdef");
        _store.SetMeta(SyncIdentity.MetaKeys.Host, "hostfp");

        var identity = new GitSyncIdentityStore(_store).Read();

        Assert.Equal("git@github.com:o/r.git", identity.Remote.Value);
        Assert.Equal("0123456789abcdef0123456789abcdef", identity.RepoId.Value);
        Assert.Equal("hostfp", identity.Host.Value);
    }

    [Fact]
    public void AnEmptyValueAndAMissingKey_AreNone()
    {
        _store.SetMeta(SyncIdentity.MetaKeys.Remote, "");

        var identity = new GitSyncIdentityStore(_store).Read();

        Assert.True(FSharpOption<string>.get_IsNone(identity.Remote));
        Assert.True(FSharpOption<string>.get_IsNone(identity.RepoId));
        Assert.True(FSharpOption<string>.get_IsNone(identity.Host));
    }
}
