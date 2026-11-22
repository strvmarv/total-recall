using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TotalRecall.Cli;
using TotalRecall.Cli.Tests.Commands;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Tests.Commands.Sync;

/// <summary>
/// End-to-end tests of the hidden <c>sync</c> commands through <see cref="CliApp.RunAsync"/>: the data directory, the
/// database path, HOME and git's global configuration are all redirected to a temporary tree for the duration of a test.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class SyncCommandTests : IDisposable
{
    private static readonly string[] Variables =
        ["TOTAL_RECALL_HOME", "TOTAL_RECALL_DB_PATH", "HOME", "USERPROFILE", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "XDG_CONFIG_HOME"];

    private readonly Dictionary<string, string?> _saved = [];
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tr-cli-sync-" + Guid.NewGuid().ToString("N"));

    private string DataDir => Path.Combine(_root, "home");
    private string ConfigPath => Path.Combine(DataDir, "config.toml");

    public SyncCommandTests()
    {
        foreach (var v in Variables)
            _saved[v] = Environment.GetEnvironmentVariable(v);
        Directory.CreateDirectory(_root);
        var gitHome = Path.Combine(_root, "githome");
        Directory.CreateDirectory(gitHome);
        var gitConfig = Path.Combine(gitHome, ".gitconfig");
        File.WriteAllText(gitConfig, "");
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", DataDir);
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", null);
        Environment.SetEnvironmentVariable("HOME", gitHome);
        Environment.SetEnvironmentVariable("USERPROFILE", gitHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(gitHome, ".config"));
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", gitConfig);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Console.SetOut(_out);
        Console.SetError(_err);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
        MsSqliteConnection.ClearAllPools();
        try
        {
            ForceDelete(_root);
        }
        catch (IOException)
        {
        }
    }

    private static void ForceDelete(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    private async Task<int> Run(params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        return await CliApp.RunAsync(["sync", .. args]);
    }

    private string Out => _out.ToString();
    private string Err => _err.ToString();

    private string MakeBareRepository()
    {
        var path = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(path);
        var info = new ProcessStartInfo(GitExecutable.TryFind()!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "init", "--bare", "-b", "main", path })
            info.ArgumentList.Add(a);
        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) && k != "GIT_CONFIG_GLOBAL" && k != "GIT_CONFIG_NOSYSTEM").ToList())
            info.Environment.Remove(name);
        using var p = Process.Start(info)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return path;
    }

    private string Fact(string label) =>
        Out.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith(label + ":", StringComparison.Ordinal));

    // ---- usage --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("init")]
    [InlineData("init", "--url")]
    [InlineData("init", "--nope")]
    [InlineData("init", "git@h:o/r")]
    [InlineData("join")]
    [InlineData("join", "a", "b")]
    [InlineData("join", "--url", "x")]
    [InlineData("join", "--branch")]
    [InlineData("leave", "x")]
    [InlineData("status", "--branch", "x")]
    [InlineData("unblock")]
    [InlineData("unblock", "a", "b")]
    [InlineData("unblock", "--branch", "x")]
    [InlineData("unblock", "--nope")]
    public async Task ArgumentErrors_ExitTwo_WithUsageOnStandardError(params string[] args)
    {
        var code = await Run(args);

        Assert.Equal(2, code);
        Assert.Contains("Usage: total-recall sync", Err);
        Assert.False(Directory.Exists(DataDir));
    }

    [Theory]
    [InlineData("init", "https://tokenSECRET@host/o/r")]
    [InlineData("init", "--url=https://user:pwSECRET@host/o/r")]
    [InlineData("join", "https://tokenSECRET@host/o/r", "https://other:pwSECRET@host/o/r")]
    [InlineData("leave", "https://tokenSECRET@host/o/r")]
    [InlineData("unblock", "a", "https://tokenSECRET@host/o/r")]
    [InlineData("unblock", "--id=tokenSECRET")]
    public async Task AUsageError_NeverEchoesAnAddress(params string[] args)
    {
        var code = await Run(args);

        Assert.Equal(2, code);
        Assert.DoesNotContain("SECRET", Out + Err);
    }

    // ---- status and leave -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_InAFreshDataDirectory_PrintsTheFacts_AndCreatesNothing()
    {
        var code = await Run("status");

        Assert.Equal(0, code);
        Assert.Equal("storage mode: local", Fact("storage mode"));
        Assert.Equal("recorded remote: not recorded", Fact("recorded remote"));
        Assert.Contains("last sync: never", Out);
        Assert.False(Directory.Exists(DataDir), "status created the data directory");
    }

    [Fact]
    public async Task Leave_InTheDefaultMode_ChangesNothing()
    {
        var code = await Run("leave");

        Assert.Equal(0, code);
        Assert.Contains("not in git mode; nothing changed", Out);
        Assert.False(Directory.Exists(DataDir));
    }

    [Fact]
    public async Task AClosedStandardOutput_DoesNotChangeTheExitCode()
    {
        Console.SetOut(new ThrowingWriter());
        var code = await CliApp.RunAsync(["sync", "status"]);
        Assert.Equal(0, code);
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("the pipe is closed");
        public override void Write(char value) => throw new IOException("the pipe is closed");
    }

    [Fact]
    public async Task Status_NeverPrintsACredentialFromTheConfig()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ConfigPath, "[git_sync]\nremote = \"https://tokenSECRET@host/o/r\"\n");

        var code = await Run("status");

        Assert.Equal(0, code);
        Assert.DoesNotContain("tokenSECRET", Out + Err);
        Assert.Contains("https://***@host/o/r", Out);
    }

    // ---- unblock --------------------------------------------------------------------------------------------------

    private static readonly FakeSecrets Fakes = new(61);

    /// <summary>A memory with a token in the database the commands use, held back by a scan; returns the token.</summary>
    private static string BlockedMemory(string id)
    {
        var token = Fakes.Get("github-token");
        var dbPath = ConfigLoader.GetDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(dbPath, pooling: false))
        {
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            store.SetMeta("git_sync_machine", "4f2a9c1e");
            store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(Content: "the token is " + token, Id: id));
            Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(conn, () => 1_800_000_000_000L).Scan());
            Assert.Equal("secret-detected", GitSyncRows.Read(conn, id)!.BlockedReason);
        }
        MsSqliteConnection.ClearAllPools();
        return token;
    }

    [Fact]
    public async Task Unblock_ABlockedMemory_ExitsZero_AndPrintsTheCategoriesOnly()
    {
        var token = BlockedMemory("3f9c2a7e");

        var code = await Run("unblock", "3f9c2a7e");

        Assert.Equal(0, code);
        Assert.Contains("unblocked 3f9c2a7e", Out);
        Assert.Contains("found: API token or secret", Out);
        Assert.DoesNotContain(token, Out + Err);
        Assert.DoesNotContain(token[4..20], Out + Err);
    }

    [Fact]
    public async Task Unblock_ARefusal_ExitsOne_WithTheRefusalOnStandardError()
    {
        var token = BlockedMemory("3f9c2a7e");

        var code = await Run("unblock", "aaaa1111");

        Assert.Equal(1, code);
        Assert.Equal("", Out);
        Assert.StartsWith("refused: unknown-entry: ", Err);
        Assert.DoesNotContain(token, Out + Err);

        Assert.Equal(0, await Run("unblock", "3f9c2a7e"));
        Assert.Equal(1, await Run("unblock", "3f9c2a7e"));
        Assert.StartsWith("refused: not-blocked: ", Err);
    }

    [Fact]
    public async Task Unblock_InAFreshDataDirectory_IsUnknownEntry_AndCreatesNothing()
    {
        var code = await Run("unblock", "3f9c2a7e");

        Assert.Equal(1, code);
        Assert.StartsWith("refused: unknown-entry: ", Err);
        Assert.False(Directory.Exists(DataDir), "unblock created the data directory");
    }

    // ---- load errors ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMalformedConfig_IsFailedForInitJoinLeaveAndUnblock_AndUnknownForStatus()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ConfigPath, "[storage\nmode = ");

        foreach (var args in new[] { new[] { "init", "--url", "git@h:o/r" }, ["join", "git@h:o/r"], ["leave"], ["unblock", "3f9c2a7e"] })
        {
            var code = await Run(args);
            Assert.Equal(1, code);
            Assert.Contains("refused: failed:", Err);
        }

        var status = await Run("status");
        Assert.Equal(0, status);
        Assert.Contains("unknown (", Out);
    }

    [Fact]
    public async Task ARelativeDatabasePath_IsFailedForInit_AndUnknownForStatus()
    {
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", "relative.db");

        var init = await Run("init", "--url", "git@h:o/r");
        Assert.Equal(1, init);
        Assert.Contains("refused: failed:", Err);

        var status = await Run("status");
        Assert.Equal(0, status);
        Assert.Contains("unknown (", Out);
    }

    // ---- git -----------------------------------------------------------------------------------------------------

    [GitAvailableFact]
    public async Task Init_OfAMissingRepository_IsRefusedAsUnreachable()
    {
        var code = await Run("init", "--url", Path.Combine(_root, "nowhere.git"));

        Assert.Equal(1, code);
        var refusal = Err.Split('\n').Select(l => l.TrimEnd('\r')).Last(l => l.StartsWith("refused:", StringComparison.Ordinal));
        Assert.StartsWith("refused: unreachable:", refusal);
    }

    [GitAvailableFact]
    public async Task InitStatusLeaveJoin_AgainstARealRepository()
    {
        var remote = MakeBareRepository();

        Assert.Equal(0, await Run("init", "--url", remote));
        Assert.Contains("initialised a new sync repository", Out);
        Assert.Contains("mode = \"git\"", File.ReadAllText(ConfigPath));

        Assert.Equal(0, await Run("status"));
        Assert.Equal("storage mode: git", Fact("storage mode"));
        Assert.Equal("join state: joined", Fact("join state"));
        var repoId = Fact("repository id");
        Assert.Matches("^repository id: [0-9a-f]{32}$", repoId);

        Assert.Equal(0, await Run("leave"));
        Assert.Contains("mode = \"local\"", File.ReadAllText(ConfigPath));
        Assert.Contains("remote = ", File.ReadAllText(ConfigPath)); // [git_sync] is kept

        Assert.Equal(0, await Run("join", remote));
        Assert.Contains("joined the sync repository", Out);
        Assert.Contains("mode = \"git\"", File.ReadAllText(ConfigPath));

        Assert.Equal(0, await Run("status"));
        Assert.Equal(repoId, Fact("repository id"));
        Assert.Equal("join state: joined", Fact("join state"));
    }
}
