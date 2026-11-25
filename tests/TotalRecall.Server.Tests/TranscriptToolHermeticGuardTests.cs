// tests/TotalRecall.Server.Tests/TranscriptToolHermeticGuardTests.cs
//
// With the sync-on-read wired (spec 2026-11-24, section 3), a transcript tool called through a composition looks for
// the current session; without a marker it guesses from the real Claude Code directory and could ingest a live
// session into a test's archive. Source scan: in every Server test file that builds a composition and names a transcript
// tool, each composition call must pass HermeticSync.Options (or an ArchivePassOptions carrying its own Sync). The Part 5 acceptance tests
// (TranscriptSyncAcceptanceTests.cs) use real seams on purpose and are exempt; this file is excluded (it names both).

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
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

        // Every call site in such a file, not only the file as a whole.
        var offenders = callers
            .SelectMany(c => UnguardedCalls(c.Text).Select(call => Path.GetFileName(c.Path) + ": " + call))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.True(offenders.Length == 0,
            "these composition calls in files that call a transcript tool pass no hermetic sync: " + string.Join("; ", offenders));
    }

    [Fact]
    public void UnguardedCalls_FindsAnUnguardedCallInAGuardedFile()
    {
        const string guardedOnly =
            "var a = ServerComposition.OpenLocalForCatalog(db, new ArchivePassOptions(Sync: HermeticSync.Options));\n" +
            "var b = ServerComposition.OpenCortexForTest(db, \"u\", \"k\", new ArchivePassOptions(MinInterval: x,\n Sync: s));\n" +
            "var options = new ArchivePassOptions(Sync: HermeticSync.Options);\n" +
            "var c = ServerComposition.OpenLocalForCatalog(db, options);\n";
        Assert.Empty(UnguardedCalls(guardedOnly));

        // The same file with one more call that passes no options.
        var noOptions = guardedOnly + "var d = ServerComposition.OpenLocalForCatalog(Path.Combine(home, \"t.db\"));\n";
        Assert.Contains("t.db", Assert.Single(UnguardedCalls(noOptions)), StringComparison.Ordinal);

        // One more call whose options carry no sync: it is found, and the variable call above is no longer trusted.
        var noSync = guardedOnly + "var e = ServerComposition.OpenLocalForCatalog(db, new ArchivePassOptions(MinInterval: x));\n";
        var found = UnguardedCalls(noSync);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.Contains("MinInterval: x)", StringComparison.Ordinal));
        Assert.Contains(found, f => f.EndsWith("(db, options)", StringComparison.Ordinal));

        // An options variable is accepted only when every ArchivePassOptions the file builds carries a sync.
        var loose = "var options = new ArchivePassOptions(MinInterval: x);\nvar c = ServerComposition.OpenLocalForCatalog(db, options);\n";
        Assert.Single(UnguardedCalls(loose));
    }

    /// <summary>
    /// The argument lists of the composition calls in <paramref name="text"/> that pass no hermetic sync: one that names
    /// <c>HermeticSync.Options</c> or builds an <c>ArchivePassOptions</c> with a <c>Sync:</c> argument is guarded; one
    /// whose last argument is a plain variable is guarded when every <c>ArchivePassOptions</c> the file builds carries a
    /// <c>Sync:</c> (and it builds at least one); anything else (no options at all, options without a sync) is not.
    /// </summary>
    private static List<string> UnguardedCalls(string text)
    {
        var built = Regex.Matches(text, @"new\s+ArchivePassOptions\s*\(").Select(m => ArgumentList(text, m.Index + m.Length - 1)).ToArray();
        var everyBuildHasSync = built.Length > 0 && built.All(HasSync);

        var unguarded = new List<string>();
        foreach (var call in CompositionCalls)
        {
            for (var at = text.IndexOf(call, StringComparison.Ordinal); at >= 0; at = text.IndexOf(call, at + call.Length, StringComparison.Ordinal))
            {
                var args = ArgumentList(text, at + call.Length - 1);
                var guarded = args.Contains("HermeticSync.Options", StringComparison.Ordinal)
                    || (args.Contains("ArchivePassOptions", StringComparison.Ordinal) ? PassOptionsWithSync.IsMatch(args)
                        : Regex.IsMatch(args, @",\s*@?[A-Za-z_][A-Za-z0-9_]*\s*$") && everyBuildHasSync);
                if (!guarded) unguarded.Add(call + args + ")");
            }
        }
        return unguarded;
    }

    private static bool HasSync(string args) =>
        args.Contains("HermeticSync.Options", StringComparison.Ordinal) || Regex.IsMatch(args, @"\bSync\s*:");

    /// <summary>The text between the parenthesis at <paramref name="open"/> and its match (to the end when unbalanced).</summary>
    private static string ArgumentList(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return text.Substring(open + 1, i - open - 1);
        }
        return text[(open + 1)..];
    }
}
