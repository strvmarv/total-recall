// tests/TotalRecall.Server.Tests/ArchivePassIsolationTests.cs
//
// The archive's session_start pass is started by session_start and nowhere else (search the archive, spec section 4):
// the pass object exists on the boot path (OpenProduction reaches OpenSqlite), but Program.RunServeAsync and
// OpenProduction never start it, and the archive search service is built only at the two registration sites. Source
// scans in the style of TranscriptSentinelIsolationTests.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

public sealed class ArchivePassIsolationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "TotalRecall.Host", "Program.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> Sources(string dir) =>
        Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    private static string Rel(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string Slice(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing '{from}'");
        var end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"missing '{to}' after '{from}'");
        return text.Substring(start, end - start);
    }

    [Fact]
    public void Kick_IsCalledOnlyFromSessionStartHandler()
    {
        var root = RepoRoot();
        var callers = Sources(Path.Combine(root, "src"))
            .Where(f => File.ReadAllText(f).Contains(".Kick(", StringComparison.Ordinal))
            .Select(f => Rel(root, f))
            .ToArray();

        Assert.Equal(new[] { "src/TotalRecall.Server/Handlers/SessionStartHandler.cs" }, callers);
        var handler = File.ReadAllText(Path.Combine(root, "src", "TotalRecall.Server", "Handlers", "SessionStartHandler.cs"));
        Assert.Single(Regex.Matches(handler, @"\.Kick\("));
    }

    [Fact]
    public void TheSearchService_IsBuiltOnlyAtTheTwoRegistrationSites()
    {
        var root = RepoRoot();
        var construct = new Regex(@"new\s+(?:[A-Za-z_][\w.]*\.)?TranscriptSearchService\s*\(");
        var sites = Sources(Path.Combine(root, "src"))
            .SelectMany(f => construct.Matches(File.ReadAllText(f)).Select(_ => Rel(root, f)))
            .ToArray();
        Assert.Equal(new[] { "src/TotalRecall.Server/ServerComposition.cs", "src/TotalRecall.Server/ServerComposition.cs" }, sites);

        var composition = File.ReadAllText(Path.Combine(root, "src", "TotalRecall.Server", "ServerComposition.cs"));
        var sqlite = Slice(composition, "private static ServerCompositionHandles OpenSqlite(", "private static ServerCompositionHandles OpenPostgres(");
        var cortex = Slice(composition, "private static ServerCompositionHandles OpenCortexCore(", "private static string ResolveUserId(");
        Assert.Single(construct.Matches(sqlite));
        Assert.Single(construct.Matches(cortex));
    }

    [Fact]
    public void TheBootPath_NeverStartsThePass()
    {
        var root = RepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "TotalRecall.Host", "Program.cs"));
        Assert.DoesNotContain("ArchivePass", program);
        Assert.DoesNotContain(".Kick(", program);

        var composition = File.ReadAllText(Path.Combine(root, "src", "TotalRecall.Server", "ServerComposition.cs"));
        var openProduction = Slice(composition, "public static ServerCompositionHandles OpenProduction(", "internal static ServerCompositionHandles OpenConfigured(");
        Assert.DoesNotContain("ArchivePass", openProduction);
        Assert.DoesNotContain(".Kick(", openProduction);
        Assert.DoesNotContain(".Kick(", composition);
    }

    [Fact]
    public void SyncAndGitSync_DoNotNameTheNewTypes()
    {
        var root = RepoRoot();
        var names = new[] { "ArchivePass", "IArchivePass", "ArchivePassContext", "ArchivePassOptions", "ArchivePassResult", "TranscriptAdapters" };
        foreach (var dir in new[] { "Sync", "GitSync" })
        {
            foreach (var file in Sources(Path.Combine(root, "src", "TotalRecall.Infrastructure", dir)))
            {
                var text = File.ReadAllText(file);
                foreach (var name in names)
                    Assert.False(text.Contains(name, StringComparison.Ordinal), $"{Rel(root, file)} names {name}");
            }
        }
    }
}
