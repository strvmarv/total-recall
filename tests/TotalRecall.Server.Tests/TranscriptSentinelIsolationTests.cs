// tests/TotalRecall.Server.Tests/TranscriptSentinelIsolationTests.cs
//
// The sentinel mirror (live/enabled) runs once per MCP engine start and nowhere else: it is a
// best-effort step in Program.RunServeAsync after the composition is opened, so it can never
// delay or fail the handshake, and `ui`, `dump-catalog` and the composition root never touch it.
// Source scan, modelled on UpdateCheckIsolationTests; Mirror's behaviour is tested with the type.

namespace TotalRecall.Server.Tests;

using System;
using System.IO;
using System.Linq;
using Xunit;

public sealed class TranscriptSentinelIsolationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "TotalRecall.Host", "Program.cs")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Slice(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing '{from}'");
        var end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"missing '{to}' after '{from}'");
        return text.Substring(start, end - start);
    }

    private static int Count(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }

    [Fact]
    public void Mirror_IsCalledOnlyFromRunServeAsync_AfterTheCompositionIsOpened()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "TotalRecall.Host", "Program.cs"));

        Assert.Equal(1, Count(program, ".Mirror("));
        Assert.Equal(1, Count(program, "new TranscriptSentinel("));

        var serve = Slice(program, "private static async Task<int> RunServeAsync()", "private static async Task<int> RunUiAsync(");
        Assert.Contains(".Mirror(", serve);
        Assert.Contains("TranscriptSentinel.EffectiveStorageMode(", serve);
        Assert.Contains("LoadTranscriptsSettings(", serve);
        Assert.True(serve.IndexOf(".Mirror(", StringComparison.Ordinal)
                    > serve.IndexOf("ServerComposition.OpenProduction(", StringComparison.Ordinal),
            "the mirror must run after the composition is opened");
        Assert.True(serve.IndexOf(".Mirror(", StringComparison.Ordinal)
                    < serve.IndexOf("new McpServer(", StringComparison.Ordinal),
            "the mirror must run before the server loop starts");
    }

    [Fact]
    public void OtherEntries_DoNotReferenceTheSentinel()
    {
        var root = RepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "TotalRecall.Host", "Program.cs"));

        // Everything outside RunServeAsync in Program.cs (Main's dump-catalog branch, RunUiAsync, ...).
        var serve = Slice(program, "private static async Task<int> RunServeAsync()", "private static async Task<int> RunUiAsync(");
        Assert.DoesNotContain("TranscriptSentinel", program.Replace(serve, string.Empty));

        // The composition root (reached by ui, dump-catalog and tests) never mirrors or writes the sentinel. It only
        // READS it, through the gate of the three archive tools (OpenSqlite and OpenCortexCore, one each), evaluated
        // on every tool call.
        var composition = File.ReadAllText(Path.Combine(root, "src", "TotalRecall.Server", "ServerComposition.cs"));
        Assert.DoesNotContain(".Mirror(", composition);
        Assert.DoesNotContain("TranscriptSentinel.EffectiveStorageMode", composition);
        Assert.Equal(2, Count(composition, "new Infrastructure.Transcripts.TranscriptSentinel(ConfigLoader.GetDataDir())"));
        Assert.Equal(2, Count(composition, "TranscriptSentinel"));
        Assert.Equal(6, Count(composition, "transcriptSentinel.IsEnabled"));
        // The web UI never touches it.
        var web = Directory.EnumerateFiles(Path.Combine(root, "src", "TotalRecall.Web"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
        foreach (var file in web)
        {
            Assert.DoesNotContain("TranscriptSentinel", File.ReadAllText(file));
        }
    }
}
