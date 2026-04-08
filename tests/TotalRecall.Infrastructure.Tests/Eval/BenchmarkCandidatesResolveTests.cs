using System;
using System.Collections.Generic;
using System.IO;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Eval;

[Trait("Category", "Integration")]
public sealed class BenchmarkCandidatesResolveTests : IDisposable
{
    private readonly string _tempDir;

    public BenchmarkCandidatesResolveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-bc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static (MsSqliteConnection conn, BenchmarkCandidates bc) NewBc()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new BenchmarkCandidates(conn));
    }

    [Fact]
    public void Resolve_FlipsStatusAndAppendsCorpusEntries()
    {
        var (conn, bc) = NewBc();
        using (conn)
        {
            bc.UpsertFromMisses(
                new List<MissEntry>
                {
                    new("q1", 0.1, 100),
                    new("q2", 0.2, 101),
                },
                new List<MissContext>
                {
                    new("q1", "This is the content for q1", "e1"),
                    new("q2", "Content for q2", "e2"),
                });

            var pending = bc.ListPending();
            Assert.Equal(2, pending.Count);
            var acceptId = pending[0].Id;
            var rejectId = pending[1].Id;

            var benchmarkFile = Path.Combine(_tempDir, "retrieval.jsonl");
            File.WriteAllText(benchmarkFile, "{\"query\":\"existing\"}\n");

            var result = bc.Resolve(new[] { acceptId }, new[] { rejectId }, benchmarkFile);
            Assert.Equal(1, result.Accepted);
            Assert.Equal(1, result.Rejected);
            Assert.Single(result.CorpusEntries);

            var content = File.ReadAllText(benchmarkFile);
            Assert.Contains("existing", content);
            Assert.Contains("\"source\":\"grow\"", content);
            Assert.EndsWith("\n", content);

            // After resolve, no more pending rows.
            Assert.Empty(bc.ListPending());
        }
    }

    [Fact]
    public void Resolve_MissingBenchmarkFile_CreatesIt()
    {
        var (conn, bc) = NewBc();
        using (conn)
        {
            bc.UpsertFromMisses(
                new List<MissEntry> { new("q1", 0.1, 100) },
                new List<MissContext> { new("q1", "content", "e1") });

            var acceptId = bc.ListPending()[0].Id;
            var path = Path.Combine(_tempDir, "new.jsonl");
            bc.Resolve(new[] { acceptId }, Array.Empty<string>(), path);
            Assert.True(File.Exists(path));
            Assert.Contains("\"query\":\"q1\"", File.ReadAllText(path));
        }
    }

    [Fact]
    public void Resolve_UnwritableFilePath_RollsBackStatusFlips()
    {
        // Task 5.10 item 6: if the corpus file write fails, the DB row
        // status flips must roll back so the candidate stays 'pending'.
        var (conn, bc) = NewBc();
        using (conn)
        {
            bc.UpsertFromMisses(
                new List<MissEntry> { new("q1", 0.1, 100) },
                new List<MissContext> { new("q1", "content for q1", "e1") });

            var pending = bc.ListPending();
            Assert.Single(pending);
            var acceptId = pending[0].Id;

            // Point the corpus path into a non-existent parent directory
            // so the temp-file write (<path>.tmp) throws
            // DirectoryNotFoundException. Resolve must roll back the row
            // flips so the candidate stays 'pending'.
            var bogusPath = Path.Combine(_tempDir, "nope", "does-not-exist", "retrieval.jsonl");

            Assert.ThrowsAny<Exception>(() =>
                bc.Resolve(new[] { acceptId }, Array.Empty<string>(), bogusPath));

            // Row status MUST still be 'pending'.
            var still = bc.ListPending();
            Assert.Single(still);
            Assert.Equal(acceptId, still[0].Id);
            Assert.Equal("pending", still[0].Status);

            // And there should be no stray .tmp sibling left behind.
            Assert.False(File.Exists(bogusPath + ".tmp"));
        }
    }

    [Fact]
    public void Resolve_UnknownAcceptId_Skipped()
    {
        var (conn, bc) = NewBc();
        using (conn)
        {
            var benchmarkFile = Path.Combine(_tempDir, "r.jsonl");
            var result = bc.Resolve(new[] { "does-not-exist" }, Array.Empty<string>(), benchmarkFile);
            Assert.Equal(1, result.Accepted); // count mirrors caller input
            Assert.Empty(result.CorpusEntries);
            Assert.False(File.Exists(benchmarkFile));
        }
    }
}
