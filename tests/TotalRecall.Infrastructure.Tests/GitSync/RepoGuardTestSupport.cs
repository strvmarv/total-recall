using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Settings and stores for the <see cref="RepoGuard"/> tests.</summary>
internal static class GuardSettings
{
    /// <summary>The normalised <c>[git_sync]</c> settings for a remote (null: not set), everything else default.</summary>
    public static TotalRecall.Core.Config.GitSyncConfig For(string? remote, string? branch = null)
    {
        var absent = TotalRecall.Core.Config.GitRawValue.RawAbsent;
        var raw = new TotalRecall.Core.Config.RawGitSync(
            remote is null ? absent : TotalRecall.Core.Config.GitRawValue.NewRawStr(remote),
            branch is null ? absent : TotalRecall.Core.Config.GitRawValue.NewRawStr(branch),
            absent, absent, absent, absent, absent, absent, absent, absent);
        return TotalRecall.Core.Config.GitSyncSettings.normalise(raw);
    }
}

/// <summary>An in-memory migrated store whose <c>_meta</c> a test sets with <c>SetMeta</c>.</summary>
internal sealed class GuardStore : IDisposable
{
    private readonly MsSqliteConnection _conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");

    public SqliteStore Store { get; }

    public GuardStore()
    {
        MigrationRunner.RunMigrations(_conn);
        Store = new SqliteStore(_conn);
    }

    public void Dispose() => _conn.Dispose();
}

/// <summary>A probe that returns what the test says and counts its calls.</summary>
internal sealed class StubProbe(Func<int?> status) : IPublicRepoProbe
{
    public int Calls { get; private set; }
    public List<(string Owner, string Repo)> Asked { get; } = [];

    public Task<int?> GetStatusAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        Calls++;
        Asked.Add((owner, repo));
        return Task.FromResult(status());
    }
}
