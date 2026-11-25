// tests/TotalRecall.Server.Tests/TranscriptToolHermeticGuardTests.cs
//
// With the sync-on-read wired (spec 2026-11-24, section 3), a transcript tool called through a composition looks for
// the current session; without a marker it guesses from the real Claude Code directory and could ingest a live
// session into a test's archive. Source scan: every Server test file that builds a composition and names a transcript
// tool must pass HermeticSync.Options (or an ArchivePassOptions carrying its own Sync). The Part 5 acceptance tests
// (TranscriptSyncAcceptanceTests.cs) use real seams on purpose and are exempt; this file is excluded (it names both).

namespace TotalRecall.Server.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

public sealed class TranscriptToolHermeticGuardTests
{
    private const string OwnFile = "TranscriptToolHermeticGuardTests.cs";
    private const string Exempt = "TranscriptSyncAcceptanceTests.cs";

    private static readonly string[] CompositionCalls =
    {
        "ServerComposition.OpenLocalForCatalog(", "ServerComposition.OpenCortexForTest(", "ServerComposition.OpenConfigured(",
        "ServerComposition.OpenGit(", "ServerComposition.OpenProduction(",
    };

    private static readonly string[] ToolNames = { "\"transcript_search\"", "\"transcript_get\"", "\"transcript_sessions\"" };

    private static readonly Regex PassOptionsWithSync = new(@"new\s+ArchivePassOptions\s*\([^;]*?\bSync\s*:", RegexOptions.Singleline);

    private static string TestsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "TotalRecall.Server.Tests")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tests", "TotalRecall.Server.Tests");
    }

    [Fact]
    public void ServerTests_CallingTranscriptTools_AreHermetic()
    {
        var root = TestsDir();
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(sep + "bin" + sep, StringComparison.Ordinal) && !f.Contains(sep + "obj" + sep, StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) is not OwnFile and not Exempt)
            .ToArray();

        var callers = files
            .Select(f => (Path: f, Text: File.ReadAllText(f)))
            .Where(f => CompositionCalls.Any(c => f.Text.Contains(c, StringComparison.Ordinal))
                        && ToolNames.Any(n => f.Text.Contains(n, StringComparison.Ordinal)))
            .ToArray();

        // The scan sees the known callers (it cannot pass by finding nothing).
        var names = callers.Select(c => Path.GetFileName(c.Path)).ToArray();
        foreach (var known in new[] { "TranscriptToolHandlerTests.cs", "TranscriptReadAcceptanceTests.cs", "TranscriptSearchSessionStartAcceptanceTests.cs" })
            Assert.Contains(known, names);

        var offenders = callers
            .Where(c => !c.Text.Contains("HermeticSync.Options", StringComparison.Ordinal)
                        && !PassOptionsWithSync.IsMatch(c.Text))
            .Select(c => Path.GetFileName(c.Path))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.True(offenders.Length == 0,
            "these files call a transcript tool through a composition without HermeticSync.Options: " + string.Join(", ", offenders));
    }
}
