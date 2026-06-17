using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Eval;

/// <summary>
/// eval_grow accept guard: a candidate captured from a real retrieval miss
/// must NOT be appended to the public benchmark corpus when its query or
/// surfaced content carries secrets/PII or a configured internal term. Blocked
/// candidates stay pending and are reported.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BenchmarkCandidatesGuardTests : IDisposable
{
    private readonly string _dir;

    public BenchmarkCandidatesGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tr-bcguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private static (MsSqliteConnection conn, BenchmarkCandidates bc) NewBc()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new BenchmarkCandidates(conn));
    }

    [Fact]
    public void Resolve_BlocksAccept_WhenSurfacedContentHasSecret()
    {
        var (conn, bc) = NewBc();
        using (conn)
        {
            bc.UpsertFromMisses(
                new List<MissEntry> { new("how do we rotate the mirror token", 0.1, 100) },
                new List<MissContext>
                {
                    new("how do we rotate the mirror token",
                        "procedure uses github_pat_11ABCDEF0123456789_abcdefABCDEFghij", "e1"),
                });
            var id = bc.ListPending()[0].Id;
            var file = Path.Combine(_dir, "retrieval.jsonl");

            var result = bc.Resolve(new[] { id }, Array.Empty<string>(), file, sensitiveTerms: null);

            Assert.Empty(result.CorpusEntries); // nothing written to the public corpus
            Assert.Contains(result.Blocked, b => b.Id == id);
            Assert.False(File.Exists(file)); // nothing written
            // Blocked candidate stays pending for review.
            Assert.Contains(bc.ListPending(), r => r.Id == id);
        }
    }

    [Fact]
    public void Resolve_BlocksAccept_WhenConfiguredInternalTermPresent()
    {
        var (conn, bc) = NewBc();
        using (conn)
        {
            bc.UpsertFromMisses(
                new List<MissEntry> { new("cortex staging deploy steps", 0.1, 100) },
                new List<MissContext> { new("cortex staging deploy steps", "internal runbook text", "e1") });
            var id = bc.ListPending()[0].Id;
            var file = Path.Combine(_dir, "retrieval.jsonl");

            var result = bc.Resolve(
                new[] { id }, Array.Empty<string>(), file, sensitiveTerms: new[] { "cortex" });

            Assert.Empty(result.CorpusEntries);
            Assert.Contains(result.Blocked, b => b.Id == id && b.Reasons.Count > 0);
        }
    }

    [Fact]
    public void Resolve_CleanAccept_IsStillWritten()
    {
        var (conn, bc) = NewBc();
        using (conn)
        {
            bc.UpsertFromMisses(
                new List<MissEntry> { new("what test runner do we use", 0.1, 100) },
                new List<MissContext> { new("what test runner do we use", "the project uses vitest", "e1") });
            var id = bc.ListPending()[0].Id;
            var file = Path.Combine(_dir, "retrieval.jsonl");

            var result = bc.Resolve(new[] { id }, Array.Empty<string>(), file, sensitiveTerms: new[] { "cortex" });

            Assert.Single(result.CorpusEntries);
            Assert.Equal(1, result.Accepted);
            Assert.Empty(result.Blocked);
            Assert.Contains("vitest", File.ReadAllText(file));
        }
    }

    [Fact]
    public void ListPending_FlagsSensitiveRows()
    {
        var (conn, bc) = NewBc();
        using (conn)
        {
            bc.UpsertFromMisses(
                new List<MissEntry> { new("clean query", 0.1, 100), new("email me at a@b.com", 0.2, 101) },
                new List<MissContext>
                {
                    new("clean query", "totally fine content", "e1"),
                    new("email me at a@b.com", "reach me", "e2"),
                });

            var flagged = bc.ListPending(sensitiveTerms: null);
            var clean = flagged.Single(r => r.QueryText == "clean query");
            var pii = flagged.Single(r => r.QueryText == "email me at a@b.com");
            Assert.False(clean.Sensitive);
            Assert.True(pii.Sensitive);
        }
    }
}
