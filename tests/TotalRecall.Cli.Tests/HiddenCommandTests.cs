using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TotalRecall.Cli;
using Xunit;

namespace TotalRecall.Cli.Tests;

/// <summary>The sync commands exist but are invisible to anyone who never types <c>sync</c> (until milestone 1b-iii).</summary>
[Collection("ConsoleCapture")]
public sealed class HiddenCommandTests : IDisposable
{
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public void Dispose()
    {
        CliApp.SetRegistryForTestsInternal(null);
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    private static readonly string[] SyncVerbs = ["init", "join", "leave", "status"];

    private sealed class Stub(string name, string? group, bool hidden = false) : ICliCommand
    {
        public string Name { get; } = name;
        public string? Group { get; } = group;
        public string Description => "a stub command";
        public Task<int> RunAsync(string[] args) => Task.FromResult(0);
        // `Hidden` is not declared for the visible stub: the interface's default member answers false
        public bool Hidden { get; } = hidden;
    }

    private sealed class PlainStub : ICliCommand
    {
        public string Name => "plainstub";
        public string? Group => null;
        public string Description => "an implementer that predates Hidden";
        public Task<int> RunAsync(string[] args) => Task.FromResult(0);
    }

    private static async Task<(int Code, string Out, string Err)> Capture(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        Console.SetOut(o);
        Console.SetError(e);
        var code = await CliApp.RunAsync(args);
        return (code, o.ToString(), e.ToString());
    }

    private static string WithoutVersionLine(string help) =>
        string.Join("\n", help.Replace("\r\n", "\n").Split('\n').Skip(1));

    [Fact]
    public async Task TopLevelHelp_IsTheSameWithoutTheSyncCommands()
    {
        var real = CliApp.GetRegistryForTestsInternal();
        Assert.Contains(real, c => c.Group == "sync");
        var withSync = await Capture("--help");

        CliApp.SetRegistryForTestsInternal(real.Where(c => c.Group != "sync").ToList());
        var without = await Capture("--help");

        Assert.Equal(0, withSync.Code);
        Assert.Equal(WithoutVersionLine(without.Out), WithoutVersionLine(withSync.Out));
        Assert.DoesNotContain("sync", withSync.Out, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheGroupHelp_ListsTheFourCommands_AndEachAnswersItsOwnHelp()
    {
        var group = await Capture("sync", "--help");
        Assert.Equal(0, group.Code);
        foreach (var verb in SyncVerbs)
            Assert.Contains(verb, group.Out);

        foreach (var verb in SyncVerbs)
        {
            var help = await Capture("sync", verb, "--help");
            Assert.Equal(0, help.Code);
            Assert.Contains($"total-recall sync {verb}", help.Out);
        }
    }

    [Fact]
    public async Task ACommandWithoutHidden_StillCompilesAndIsListed()
    {
        CliApp.SetRegistryForTestsInternal([new PlainStub(), new Stub("shown", "grp")]);
        var help = await Capture("--help");
        Assert.Contains("plainstub", help.Out);
        Assert.Contains("grp", help.Out);
    }

    [Fact]
    public async Task HiddenCommands_AreLeftOutOfTheTopLevelHelp_AndAGroupOfOnlyHiddenCommandsToo()
    {
        CliApp.SetRegistryForTestsInternal(
        [
            new Stub("secret", null, hidden: true),
            new Stub("covert", "hush", hidden: true),
            new Stub("covert2", "hush", hidden: true),
            new Stub("open", null),
        ]);

        var help = await Capture("--help");

        Assert.DoesNotContain("secret", help.Out);
        Assert.DoesNotContain("hush", help.Out);
        Assert.DoesNotContain("covert", help.Out);
        Assert.Contains("open", help.Out);
        // a hidden command still dispatches and its group still has help
        Assert.Equal(0, (await Capture("secret")).Code);
        var group = await Capture("hush", "--help");
        Assert.Contains("covert", group.Out);
    }

    // ---- nothing user-facing names the commands ---------------------------------------------------------------------

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "catalog.json")))
                return dir.FullName;
        }
        throw new InvalidOperationException("catalog.json was not found above " + AppContext.BaseDirectory);
    }

    private static readonly Regex Phrase = new(@"sync\s+(init|join|leave|status)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public async Task NoUserFacingDocumentNamesTheSyncCommands()
    {
        var root = RepositoryRoot();
        var found = 0;
        var offenders = new List<string>();

        void ScanFile(string file, Regex pattern)
        {
            found++;
            if (pattern.IsMatch(File.ReadAllText(file)))
                offenders.Add(Path.GetRelativePath(root, file));
        }

        void ScanTree(string relative, string pattern, Regex regex, Func<string, bool>? skip = null)
        {
            var dir = Path.Combine(root, relative);
            if (!Directory.Exists(dir))
                return;
            foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/node_modules/") || rel.Contains("/bin/") || rel.Contains("/obj/") || (skip?.Invoke(rel) ?? false))
                    continue;
                ScanFile(file, regex);
            }
        }

        foreach (var name in new[] { "README.md", "INSTALL.md", "CHANGELOG.md", "CONTRIBUTING.md", "PRIVACY.md", "catalog.json", "package.json", "GEMINI.md" })
        {
            var file = Path.Combine(root, name);
            if (File.Exists(file))
                ScanFile(file, Phrase);
        }
        ScanTree("docs", "*.md", Phrase, rel => rel.StartsWith("docs/superpowers/", StringComparison.Ordinal));
        foreach (var dir in new[]
                 {
                     "skills", "hooks", "agents", ".kiro", "pi", "codex", "copilot", "cursor", "hermes-plugin", ".claude-plugin",
                     ".codex-plugin", ".cursor-plugin", ".github/plugin", ".opencode", "scripts", "src/TotalRecall.Web/ClientApp/src",
                 })
            ScanTree(dir, "*", Phrase, rel => rel.EndsWith(".test.js", StringComparison.Ordinal) || rel.StartsWith("scripts/fixtures/", StringComparison.Ordinal));

        // the source: every message and comment of the product code, except the sync commands themselves and one unrelated
        // comment of Schema.cs that says "sync status"
        ScanTree(
            "src", "*.cs", Phrase,
            rel => rel.StartsWith("src/TotalRecall.Cli/Commands/Sync/", StringComparison.Ordinal)
                   || rel.StartsWith("src/TotalRecall.Infrastructure/GitSync/", StringComparison.Ordinal)
                   || rel == "src/TotalRecall.Infrastructure/Storage/Schema.cs");

        Assert.True(found > 0, "no document was scanned");
        Assert.True(offenders.Count == 0, "these files name the sync commands: " + string.Join(", ", offenders));

        // the help of every other command, and the config set refusal text
        foreach (var command in CliApp.GetRegistryForTestsInternal().Where(c => c.Group != "sync"))
        {
            var args = command.Group is null ? new[] { command.Name, "--help" } : [command.Group, command.Name, "--help"];
            var help = await Capture(args);
            Assert.False(Phrase.IsMatch(help.Out + help.Err), $"the help of '{command.Name}' names the sync commands");
        }
        var refusal = await Capture("config", "set", "storage.mode", "git");
        Assert.NotEqual(0, refusal.Code);
        Assert.False(Phrase.IsMatch(refusal.Out + refusal.Err), "the config set refusal names the sync commands");
        Assert.Contains("storage.mode", refusal.Out + refusal.Err);
    }
}
