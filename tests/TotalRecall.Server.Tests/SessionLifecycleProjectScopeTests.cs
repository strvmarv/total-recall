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
            // Tier model v2 (Task 5): pins are sticky rows in the hot tier.
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("global pin content", Id: "g1"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("current repo pin content", Id: "r1", Project: "o/r"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("other repo pin content", Id: "x1", Project: "o/x"));
            store.SetSticky(ContentType.Memory, "g1", true);
            store.SetSticky(ContentType.Memory, "r1", true);
            store.SetSticky(ContentType.Memory, "x1", true);

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
            // Tier model v2 (Task 5): pins are sticky rows in the hot tier.
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("global pin content", Id: "g1"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("current repo pin content", Id: "r1", Project: "o/r"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("other repo pin content", Id: "x1", Project: "o/x"));
            store.SetSticky(ContentType.Memory, "g1", true);
            store.SetSticky(ContentType.Memory, "r1", true);
            store.SetSticky(ContentType.Memory, "x1", true);

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

    // ---- #47: session_start reports the detected project ----

    /// <summary>Runs session init with the process working directory set to <paramref name="dir"/>.</summary>
    private static async Task<SessionInitResult> InitInAsync(string dir, bool projectScoping, IStore store)
    {
        var lifecycle = BuildLifecycle(store, projectScoping, new ProjectResolver());
        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir;
            return await lifecycle.EnsureInitializedAsync();
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
        }
    }

    [Fact]
    public async Task Session_start_reports_lowercase_owner_repo_slug_for_repo_with_origin()
    {
        var (conn, store) = NewSqliteFixture();
        using (conn)
        {
            var repoDir = MakeRepo("slug-repo", "https://github.com/Owner/Repo.git");
            var result = await InitInAsync(repoDir, projectScoping: true, store);
            Assert.Equal("owner/repo", result.Project);
        }
    }

    [Fact]
    public async Task Session_start_reports_lowercase_folder_name_for_repo_without_remote()
    {
        var (conn, store) = NewSqliteFixture();
        using (conn)
        {
            var repo = Path.Combine(_tempRoot, "NoRemoteRepo");
            var git = Path.Combine(repo, ".git");
            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "config"), "[core]\n\trepositoryformatversion = 0\n");

            var result = await InitInAsync(repo, projectScoping: true, store);
            Assert.Equal("noremoterepo", result.Project);
        }
    }

    [Fact]
    public async Task Session_start_reports_null_project_outside_a_git_repo()
    {
        var (conn, store) = NewSqliteFixture();
        using (conn)
        {
            var plain = Path.Combine(_tempRoot, "plain-dir");
            Directory.CreateDirectory(plain);
            // The temp root must not sit inside a repo, or the test proves nothing.
            for (var d = new DirectoryInfo(plain); d is not null; d = d.Parent)
                Assert.False(Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")),
                    $"test temp dir has a .git ancestor: {d.FullName}");

            var result = await InitInAsync(plain, projectScoping: true, store);
            Assert.Null(result.Project);
        }
    }

    [Fact]
    public async Task Session_start_project_is_the_project_that_scopes_the_injected_pins()
    {
        var (conn, store) = NewSqliteFixture();
        using (conn)
        {
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("scoped pin for o/r", Id: "r1", Project: "o/r"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("scoped pin for o/x", Id: "x1", Project: "o/x"));
            store.SetSticky(ContentType.Memory, "r1", true);
            store.SetSticky(ContentType.Memory, "x1", true);

            var repoDir = MakeRepo("scope-match", "https://github.com/o/r.git");
            var result = await InitInAsync(repoDir, projectScoping: true, store);

            Assert.Equal("o/r", result.Project);
            Assert.Contains("scoped pin for o/r", result.Context);
            Assert.DoesNotContain("scoped pin for o/x", result.Context);
        }
    }

    [Fact]
    public async Task Session_start_reports_detected_project_even_when_pin_scoping_is_off()
    {
        var (conn, store) = NewSqliteFixture();
        using (conn)
        {
            var repoDir = MakeRepo("scoping-off", "https://github.com/o/r.git");
            var result = await InitInAsync(repoDir, projectScoping: false, store);
            Assert.Equal("o/r", result.Project);
        }
    }
}
