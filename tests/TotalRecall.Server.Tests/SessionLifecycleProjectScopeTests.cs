// tests/TotalRecall.Server.Tests/SessionLifecycleProjectScopeTests.cs
//
// Task 6 — integration tests verifying that project-scoped pinned injection
// in session_start (RunInit) injects the correct subset of pinned entries.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

public sealed class SessionLifecycleProjectScopeTests : IDisposable
{
    private readonly string _tempRoot;

    public SessionLifecycleProjectScopeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tr-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ---- helpers ----

    /// <summary>Creates a minimal git repo dir with a remote "origin" url.</summary>
    private string MakeRepo(string name, string remoteUrl)
    {
        var repo = Path.Combine(_tempRoot, name);
        var git = Path.Combine(repo, ".git");
        Directory.CreateDirectory(git);
        var config = $"[core]\n\trepositoryformatversion = 0\n[remote \"origin\"]\n\turl = {remoteUrl}\n";
        File.WriteAllText(Path.Combine(git, "config"), config);
        return repo;
    }

    private static (Microsoft.Data.Sqlite.SqliteConnection conn, SqliteStore store) NewSqliteFixture()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new SqliteStore(conn));
    }

    private sealed class FakeCompactionLog : ICompactionLogReader
    {
        public long? GetLastTimestampExcludingReason(string excludedReason) => null;

        public IReadOnlyList<CompactionAnalyticsRow> GetAllForAnalytics(long? sinceTimestamp = null)
            => Array.Empty<CompactionAnalyticsRow>();

        public IReadOnlyList<CompactionMovementRow> GetRecentMovements(int limit)
            => Array.Empty<CompactionMovementRow>();

        public CompactionMovementRow? GetByTargetEntryId(string targetEntryId) => null;
    }

    private static SessionLifecycle BuildLifecycle(
        IStore store,
        bool projectScoping,
        ProjectResolver? resolver = null)
    {
        return new SessionLifecycle(
            importers: new List<IImporter>(),
            store: store,
            compactionLog: new FakeCompactionLog(),
            sessionId: "test-" + Guid.NewGuid().ToString("N"),
            nowMs: () => 1_000_000_000_000L,
            usageIndexer: null,
            storageMode: "sqlite",
            tokenBudget: 4000,
            maxEntries: 50,
            projectScoping: projectScoping,
            projectResolver: resolver);
    }

    // ---- tests ----

    [Fact]
    public async Task Session_start_injects_global_and_current_project_pins_only()
    {
        // Arrange
        var (conn, store) = NewSqliteFixture();
        using (conn)
        {
            // Seed: one global pin, one "o/r" pin, one "o/x" pin (should be excluded).
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("global pin content", Id: "g1"));
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("current repo pin content", Id: "r1", Project: "o/r"));
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("other repo pin content", Id: "x1", Project: "o/x"));

            // Create a temp repo whose origin resolves to "o/r".
            var repoDir = MakeRepo("main-repo", "https://github.com/o/r.git");
            var resolver = new ProjectResolver();

            var lifecycle = BuildLifecycle(store, projectScoping: true, resolver: resolver);

            // Act — temporarily set cwd to the temp repo so RunInit resolves it.
            var originalDir = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = repoDir;
                var result = await lifecycle.EnsureInitializedAsync();

                // Assert: context must contain global and o/r content, NOT o/x.
                Assert.Contains("global pin content", result.Context);
                Assert.Contains("current repo pin content", result.Context);
                Assert.DoesNotContain("other repo pin content", result.Context);
            }
            finally
            {
                Environment.CurrentDirectory = originalDir;
            }
        }
    }

    [Fact]
    public async Task Scoping_disabled_injects_all_pins()
    {
        // Arrange
        var (conn, store) = NewSqliteFixture();
        using (conn)
        {
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("global pin content", Id: "g1"));
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("current repo pin content", Id: "r1", Project: "o/r"));
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("other repo pin content", Id: "x1", Project: "o/x"));

            var repoDir = MakeRepo("main-repo2", "https://github.com/o/r.git");
            var resolver = new ProjectResolver();

            // projectScoping: false → all pins should inject regardless of project
            var lifecycle = BuildLifecycle(store, projectScoping: false, resolver: resolver);

            var originalDir = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = repoDir;
                var result = await lifecycle.EnsureInitializedAsync();

                // Assert: context must contain ALL pinned content including o/x.
                Assert.Contains("global pin content", result.Context);
                Assert.Contains("current repo pin content", result.Context);
                Assert.Contains("other repo pin content", result.Context);
            }
            finally
            {
                Environment.CurrentDirectory = originalDir;
            }
        }
    }
}
