using System.Diagnostics;
using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>A throwaway directory with stub "git" scripts in it.</summary>
internal sealed class StubDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tr-gitproc-" + Guid.NewGuid().ToString("N"));

    public StubDirectory() => Directory.CreateDirectory(Root);

    public string Script(string body, string name = "git-stub")
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n"));
        Posix.Executable(path);
        return path;
    }

    public string PathOf(string name) => Path.Combine(Root, name);

    public GitContext Context(string executable) =>
        new(executable, Path.Combine(Root, "clone"), Path.Combine(Root, "no-hooks"), Path.Combine(Root, "empty"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }
}

[Collection("process-environment")]
public sealed class GitProcessTests
{
    private static readonly GitProcess Runner = new();
    private static GitRunOptions Options(TimeSpan? timeout = null) => new(timeout ?? TimeSpan.FromSeconds(20));

    private static Task<GitResult> Run(StubDirectory d, string script, GitRunOptions? options = null,
        SyncGit.GitCommand? command = null, CancellationToken ct = default) =>
        Runner.RunAsync(command ?? SyncGit.GitCommand.Version, d.Context(d.Script(script)), options ?? Options(), ct);

    private static bool IsAlive(int pid)
    {
        if (Directory.Exists("/proc/self"))
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var close = stat.LastIndexOf(')');
                return close < 0 || stat[(close + 2)..].FirstOrDefault() != 'Z';
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // no /proc (macOS): the pid is alive when the process can be looked up and has not exited
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<int> WaitForPid(string pidFile)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                return pid;
            await Task.Delay(50);
        }
        throw new TimeoutException("the stub did not write its pid file");
    }

    private static async Task AssertGoneWithin(int pid, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (IsAlive(pid) && DateTime.UtcNow < until)
            await Task.Delay(50);
        Assert.False(IsAlive(pid), "the child process is still alive");
    }

    [Fact]
    public async Task NoGit_WhenTheExecutableCannotStart()
    {
        using var d = new StubDirectory();
        var result = await Runner.RunAsync(
            SyncGit.GitCommand.Version, d.Context(d.PathOf("does-not-exist")), Options(), default);
        Assert.Null(result.ExitCode);
        Assert.Equal(SyncGit.FailureKind.NoGit, result.Failure);
        Assert.Empty(result.Stdout);
    }

    [ShFact]
    public async Task WorkingDirectory_IsHonoured()
    {
        using var d = new StubDirectory();
        var work = d.PathOf("work");
        Directory.CreateDirectory(work);
        var result = await Run(d, "pwd -P\n", Options() with { WorkingDirectory = work });
        // the expected value is the same directory, resolved by the shell itself (macOS links /var to /private/var)
        var expected = await Run(d, $"cd '{work}' && pwd -P\n");
        Assert.Equal(Encoding.UTF8.GetString(expected.Stdout).Trim(), Encoding.UTF8.GetString(result.Stdout).Trim());
        Assert.EndsWith("work", Encoding.UTF8.GetString(result.Stdout).Trim());
    }

    [ShFact]
    public async Task Argv_IsBuiltFromTheCommand_AndSpyRecordsIt()
    {
        using var d = new StubDirectory();
        var spy = d.PathOf("argv.txt");
        var work = d.PathOf("work");
        Directory.CreateDirectory(work);
        var script = d.Script($"for a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{spy}'\n");
        var context = d.Context(script);
        var message = "$(touch x); \"q\"";
        var command = SyncGit.GitCommand.NewCommit(message);
        var result = await Runner.RunAsync(command, context, Options() with { WorkingDirectory = work }, default);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Failure);

        var expected = SyncGit.build(new SyncGit.BuildContext(context.CloneDirectory, context.NoHooksDirectory, context.EmptyFile, false), command)
            .ResultValue.ToArray();
        Assert.Equal(expected, File.ReadAllLines(spy));
        Assert.False(File.Exists(Path.Combine(work, "x")), "the message was expanded by a shell");
    }

    [Fact]
    public async Task AnInvalidCommand_IsAProgrammingError()
    {
        using var d = new StubDirectory();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Runner.RunAsync(SyncGit.GitCommand.NewFetch("-x"), d.Context(d.PathOf("git")), Options(), default));
    }

    private static readonly string[] Touched =
    [
        "GIT_DIR", "GIT_CONFIG_COUNT", "GIT_AUTHOR_NAME", "GIT_TRACE", "SSH_ASKPASS", "GIT_ASKPASS", "HTTPS_PROXY", "GIT_SSL_CAINFO",
    ];

    [ShFact]
    public async Task Environment_IsScrubbed_ThenOverridesApply()
    {
        var saved = Touched.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var n in Touched)
                Environment.SetEnvironmentVariable(n, "x-" + n);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/tmp/xdg-original");
            using var d = new StubDirectory();

            var plain = await Run(d, "env\n");
            var vars = ParseEnv(plain.Stdout);
            foreach (var dropped in new[] { "GIT_DIR", "GIT_CONFIG_COUNT", "GIT_AUTHOR_NAME", "GIT_TRACE", "SSH_ASKPASS", "GIT_ASKPASS" })
                Assert.DoesNotContain(dropped, vars.Keys);
            Assert.Equal("x-HTTPS_PROXY", vars["HTTPS_PROXY"]);
            Assert.Equal("x-GIT_SSL_CAINFO", vars["GIT_SSL_CAINFO"]);
            Assert.Equal("0", vars["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("C", vars["LC_ALL"]);
            Assert.Equal("C", vars["LANGUAGE"]);

            var overrides = new Dictionary<string, string?>
            {
                ["HOME"] = "/x",
                ["GIT_SSH_COMMAND"] = "fake",
                ["XDG_CONFIG_HOME"] = null,
            };
            var over = await Run(d, "env\n", Options() with { EnvironmentOverrides = overrides });
            var overVars = ParseEnv(over.Stdout);
            Assert.Equal("/x", overVars["HOME"]);
            Assert.Equal("fake", overVars["GIT_SSH_COMMAND"]);
            Assert.DoesNotContain("XDG_CONFIG_HOME", overVars.Keys);

            // an override wins over the scrub: it can set a name the scrub drops
            var late = await Run(d, "env\n", Options() with { EnvironmentOverrides = new Dictionary<string, string?> { ["GIT_DIR"] = "/late" } });
            Assert.Equal("/late", ParseEnv(late.Stdout)["GIT_DIR"]);
        }
        finally
        {
            foreach (var (n, v) in saved)
                Environment.SetEnvironmentVariable(n, v);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
        }
    }

    private static Dictionary<string, string> ParseEnv(byte[] stdout)
    {
        var map = new Dictionary<string, string>();
        foreach (var line in Encoding.UTF8.GetString(stdout).Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                map[line[..eq]] = line[(eq + 1)..];
        }
        return map;
    }

    [ShFact]
    public async Task TimeoutKillsTheWholeTree()
    {
        using var d = new StubDirectory();
        var pidFile = d.PathOf("pid");
        var result = await Run(d, $"sleep 60 &\necho $! > '{pidFile}'\nwait\n", Options(TimeSpan.FromSeconds(1)));
        Assert.Equal(SyncGit.FailureKind.TimedOut, result.Failure);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(3), $"took {result.Elapsed}");
        await AssertGoneWithin(await WaitForPid(pidFile), TimeSpan.FromSeconds(2));
    }

    [ShFact]
    public async Task CancellationKillsTheTree_AndReportsCancelled()
    {
        using var d = new StubDirectory();
        var pidFile = d.PathOf("pid");
        using var cts = new CancellationTokenSource();
        var task = Run(d, $"sleep 60 &\necho $! > '{pidFile}'\nwait\n", Options(TimeSpan.FromSeconds(30)), ct: cts.Token);
        var pid = await WaitForPid(pidFile);
        var cancelledAt = Stopwatch.StartNew();
        cts.Cancel();
        var result = await task;
        Assert.Equal(SyncGit.FailureKind.Cancelled, result.Failure);
        Assert.True(cancelledAt.Elapsed < TimeSpan.FromSeconds(2), $"took {cancelledAt.Elapsed}");
        await AssertGoneWithin(pid, TimeSpan.FromSeconds(2));
    }

    [ShFact]
    public async Task ReturnsWithinTheBudget_WhenAnOrphanKeepsThePipesOpen()
    {
        using var d = new StubDirectory();
        var pidFile = d.PathOf("pid");
        var script = d.Script($"sleep 30 &\necho $! > '{pidFile}'\nexit 0\n");
        var watch = Stopwatch.StartNew();
        var result = await Runner.RunAsync(SyncGit.GitCommand.Version, d.Context(script), Options(), default);
        try
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
            Assert.Equal(0, result.ExitCode);
            Assert.Null(result.Failure);
            Assert.Empty(result.Stdout);
        }
        finally
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
            {
                try
                {
                    Process.GetProcessById(pid).Kill();
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                }
            }
        }
    }

    [ShFact]
    public async Task StdoutLimit_KillsAndReportsOutputTooLarge()
    {
        using var d = new StubDirectory();
        var result = await Run(d, "head -c 200000 /dev/zero\n", Options() with { MaxStdoutBytes = 1000 });
        Assert.Equal(SyncGit.FailureKind.OutputTooLarge, result.Failure);
        Assert.Empty(result.Stdout);
    }

    [ShFact]
    public async Task StdoutLimit_IsInclusive()
    {
        using var d = new StubDirectory();
        var exact = await Run(d, "head -c 1000 /dev/zero\n", Options() with { MaxStdoutBytes = 1000 });
        Assert.Null(exact.Failure);
        Assert.Equal(1000, exact.Stdout.Length);
        var over = await Run(d, "head -c 1000 /dev/zero\n", Options() with { MaxStdoutBytes = 999 });
        Assert.Equal(SyncGit.FailureKind.OutputTooLarge, over.Failure);
    }

    [ShFact]
    public async Task StandardInput_IsClosedAtOnce_AndElapsedIsMeasured()
    {
        using var d = new StubDirectory();
        var result = await Run(d, "cat > /dev/null\nexit 0\n", Options(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Failure);
        Assert.True(result.Elapsed > TimeSpan.Zero);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(10));
    }

    [ShFact]
    public async Task ABlockingObserver_DoesNotHoldTheCallPastItsLimit()
    {
        using var d = new StubDirectory();
        var release = new ManualResetEventSlim();
        var watch = Stopwatch.StartNew();
        var task = Run(d, "printf 'x\\n' >&2\nsleep 30\n",
            Options(TimeSpan.FromSeconds(2)) with { OnStderrSegment = _ => release.Wait(TimeSpan.FromSeconds(20)) });
        var result = await task;
        release.Set();
        Assert.Equal(SyncGit.FailureKind.TimedOut, result.Failure);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task AnInvalidTimeout_ThrowsBeforeAnythingStarts()
    {
        using var d = new StubDirectory();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Runner.RunAsync(SyncGit.GitCommand.Version, d.Context(d.PathOf("git")), Options(TimeSpan.FromSeconds(-5)), default));
    }

    [ShFact]
    public async Task Stdout_IsReturnedByteForByte()
    {
        using var d = new StubDirectory();
        var result = await Run(d, "printf 'a\\000b\\r\\377\\n'\n");
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Failure);
        Assert.Equal(new byte[] { (byte)'a', 0, (byte)'b', (byte)'\r', 0xFF, (byte)'\n' }, result.Stdout);
    }

    [ShFact]
    public async Task NonZeroExit_ClassifiesTheStderr()
    {
        using var d = new StubDirectory();
        var result = await Run(d, "printf \"fatal: Authentication failed for 'https://u:tok@h/x'\\n\" >&2\nexit 128\n");
        Assert.Equal(128, result.ExitCode);
        Assert.Equal(SyncGit.FailureKind.Auth, result.Failure);
        Assert.Contains("***@h/x", result.StderrText);
        Assert.DoesNotContain("tok", result.StderrText);
    }

    [ShFact]
    public async Task Credential_SplitAcrossTwoWrites_IsStillRedacted()
    {
        using var d = new StubDirectory();
        var segments = new List<string>();
        var script = "printf \"fatal: unable to access 'https://user:SEC\" >&2\nsleep 0.3\nprintf \"RET@h/x'\\n\" >&2\nexit 128\n";
        var result = await Run(d, script, Options() with { OnStderrSegment = segments.Add });
        var all = string.Join("|", segments) + "|" + result.StderrText;
        Assert.DoesNotContain("SEC", all);
        Assert.DoesNotContain("RET", all);
        Assert.Contains("https://***@h/x", result.StderrText);
    }

    [ShFact]
    public async Task AMultiByteCharacter_SplitAcrossTwoWrites_IsOneCharacter()
    {
        using var d = new StubDirectory();
        var segments = new List<string>();
        var result = await Run(d, "printf '\\303' >&2\nsleep 0.3\nprintf '\\251\\n' >&2\nexit 128\n", Options() with { OnStderrSegment = segments.Add });
        Assert.Equal(["é"], segments);
        Assert.Equal("é", result.StderrText);
        Assert.DoesNotContain('\uFFFD', result.StderrText);
    }

    [ShFact]
    public async Task OverlongUnterminatedLine_IsDroppedNotLeaked()
    {
        using var d = new StubDirectory();
        var segments = new List<string>();
        var script = "{ head -c 100000 /dev/zero | tr '\\000' a; printf 'https://user:SECRET@h/x\\r\\nnext\\r\\n'; } >&2\nexit 128\n";
        var result = await Run(d, script, Options() with { MaxStderrBytes = 64, OnStderrSegment = segments.Add });
        var all = string.Join("|", segments) + "|" + result.StderrText;
        Assert.DoesNotContain("SECRET", all);
        Assert.DoesNotContain("user:", all);
        Assert.DoesNotContain("RET@h", all);
        Assert.Equal(["[line too long]", "next"], segments);
        Assert.Equal("[line too long]\nnext", result.StderrText);
    }

    [ShFact]
    public async Task CrAndLf_InSeparateWrites_AreOneTerminator()
    {
        using var d = new StubDirectory();
        var segments = new List<string>();
        var result = await Run(d, "printf 'Permission denied (publickey).\\r' >&2\nsleep 0.3\nprintf '\\n' >&2\nexit 128\n",
            Options() with { OnStderrSegment = segments.Add });
        Assert.Equal(["Permission denied (publickey)."], segments);
        Assert.Contains("Permission denied (publickey).", result.StderrText);
        Assert.Equal(SyncGit.FailureKind.Auth, result.Failure);
    }

    [ShFact]
    public async Task StderrCallback_SeesSegmentsSplitOnCrAndLf_Redacted()
    {
        using var d = new StubDirectory();
        var segments = new List<string>();
        var script = "printf 'Receiving objects:  10%%\\rReceiving objects: 100%%\\rdone\\n' >&2\n"
            + "printf \"fatal: x 'https://u:TOKEN123@h/x'\\n\" >&2\nexit 128\n";
        await Run(d, script, Options() with { OnStderrSegment = segments.Add });
        Assert.Equal(
            ["Receiving objects:  10%", "Receiving objects: 100%", "done", "fatal: x 'https://***@h/x'"],
            segments);
    }

    [ShFact]
    public async Task RedactValues_ScrubBareLiterals_AndIgnoreShortOnes()
    {
        using var d = new StubDirectory();
        var segments = new List<string>();
        var script = "printf 'fatal: bad token SECRET-TOKEN-123 here\\n' >&2\nprintf 'fatal: not a git repository\\n' >&2\nexit 128\n";
        var result = await Run(d, script, Options() with { RedactValues = ["SECRET-TOKEN-123", "git"], OnStderrSegment = segments.Add });
        Assert.Contains("fatal: bad token *** here", result.StderrText);
        Assert.DoesNotContain("SECRET-TOKEN-123", result.StderrText + string.Join("", segments));
        Assert.Contains("fatal: not a git repository", result.StderrText);
        Assert.Contains("fatal: bad token *** here", segments);
    }

    [ShFact]
    public async Task StderrText_IsTheLastKeptCharsWithProgressDropped()
    {
        using var d = new StubDirectory();
        var script = """
            i=0; while [ $i -lt 200 ]; do printf 'Receiving objects: %d%%\r' $i >&2; i=$((i+1)); done
            j=0; while [ $j -lt 100 ]; do printf 'ordinary line of text number %d xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n' $j >&2; j=$((j+1)); done
            printf 'error: boom\n' >&2
            exit 128
            """;
        var result = await Run(d, script);
        Assert.True(result.StderrText.Length <= SyncGit.maxStderrKeptChars);
        Assert.EndsWith("error: boom", result.StderrText);
        Assert.DoesNotContain("Receiving objects", result.StderrText);

        var crlf = await Run(d, "printf 'Permission denied (publickey).\\r\\n' >&2\nexit 128\n");
        Assert.Contains("Permission denied (publickey).", crlf.StderrText);
        Assert.Equal(SyncGit.FailureKind.Auth, crlf.Failure);
    }

    [Fact]
    public void GitExecutable_TryFind()
    {
        using var d = new StubDirectory();
        File.WriteAllText(d.PathOf("git"), "");
        Assert.Equal(d.PathOf("git"), GitExecutable.TryFind(d.Root, windows: false));
        Assert.Null(GitExecutable.TryFind("", windows: false));
        Assert.Null(GitExecutable.TryFind(d.PathOf("elsewhere"), windows: false));

        using var w = new StubDirectory();
        File.WriteAllText(w.PathOf("git.exe"), "");
        Assert.Equal(w.PathOf("git.exe"), GitExecutable.TryFind(w.Root, windows: true));
        Assert.Null(GitExecutable.TryFind(w.Root, windows: false));

        using var dirOnly = new StubDirectory();
        Directory.CreateDirectory(dirOnly.PathOf("git"));
        Assert.Null(GitExecutable.TryFind(dirOnly.Root, windows: false));
    }
}

public sealed class StderrAccumulatorTests
{
    private static (StderrAccumulator, List<string>) Make(int maxLine = 1000, IReadOnlyList<string>? secrets = null)
    {
        var segments = new List<string>();
        return (new StderrAccumulator(maxLine, secrets, segments.Add), segments);
    }

    [Fact]
    public void ASegmentEndsAtCrLfOrTheEnd_AndEmptySegmentsAreNeverEmitted()
    {
        var (acc, segments) = Make();
        acc.Append("a\n\nb\r\r\rc");
        acc.Complete();
        Assert.Equal(["a", "b", "c"], segments);
    }

    [Fact]
    public void CrAndLf_AcrossAppends_AreOneTerminatorAndTheLineIsKept()
    {
        var (acc, segments) = Make();
        acc.Append("keep me\r");
        acc.Append("\nprogress\rlast");
        acc.Complete();
        Assert.Equal(["keep me", "progress", "last"], segments);
        Assert.Equal("keep me\nlast", acc.Text());
    }

    [Fact]
    public void ACrEndedSegmentAtTheEndOfTheStreamIsKept()
    {
        var (acc, _) = Make();
        acc.Append("x\r");
        acc.Complete();
        Assert.Equal("x", acc.Text());
    }

    [Fact]
    public void AnOverlongLineIsDroppedWholeAndReportedOnce()
    {
        var (acc, segments) = Make(maxLine: 10);
        acc.Append("0123456789ABCDEF-secret-tail\nnext\n");
        acc.Complete();
        Assert.Equal([StderrAccumulator.TooLong, "next"], segments);
        Assert.Equal(StderrAccumulator.TooLong + "\nnext", acc.Text());
    }

    [Fact]
    public void ALineOfExactlyTheLimitIsKept()
    {
        var (acc, segments) = Make(maxLine: 5);
        acc.Append("12345\n123456\n");
        acc.Complete();
        Assert.Equal(["12345", StderrAccumulator.TooLong], segments);
    }

    [Fact]
    public void AnOverlongLineAtTheEndOfTheStreamIsReported()
    {
        var (acc, segments) = Make(maxLine: 3);
        acc.Append("abcdefgh");
        acc.Complete();
        Assert.Equal([StderrAccumulator.TooLong], segments);
    }

    [Fact]
    public void SecretsShorterThanEightCharactersAreIgnored_LongerOnesAreRedacted()
    {
        var (acc, segments) = Make(secrets: ["git", "LONGSECRET"]);
        acc.Append("a git repo LONGSECRET\n");
        acc.Complete();
        Assert.Equal(["a git repo ***"], segments);
    }

    [Fact]
    public void ACredentialInAUrlIsRedactedWhenItArrivesInPieces()
    {
        var (acc, segments) = Make();
        foreach (var piece in new[] { "https://us", "er:pa", "ss@h/x\n" })
            acc.Append(piece);
        acc.Complete();
        Assert.Equal(["https://***@h/x"], segments);
    }

    [Fact]
    public void NothingIsAcceptedAfterComplete_AndAThrowingObserverIsHarmless()
    {
        var acc = new StderrAccumulator(100, null, _ => throw new InvalidOperationException("observer"));
        acc.Append("a\n");
        acc.Complete();
        acc.Append("late\n");
        Assert.Equal("a", acc.Text());
    }

    [Fact]
    public void ATrimmedBufferNeverStartsWithALoneLowSurrogate()
    {
        var (acc, _) = Make(maxLine: 5000);
        for (var i = 0; i < 4; i++)
            acc.Append(new string('a', 1000) + "\n");
        acc.Append(string.Concat(Enumerable.Repeat("\U0001F600", 1100)) + "x\n");
        acc.Complete();
        var text = acc.Text();
        Assert.True(text.Length <= SyncGit.maxStderrKeptChars);
        Assert.False(char.IsLowSurrogate(text[0]));
    }

    [Fact]
    public async Task TheObserverRunsOutsideTheLock()
    {
        StderrAccumulator? acc = null;
        string? seen = null;
        acc = new StderrAccumulator(100, null, _ => seen = acc!.Text()); // would deadlock if called under the lock on another thread
        await Task.Run(() => acc.Append("hello\n")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello", seen);
    }

    [Fact]
    public void TheKeptTextIsBounded_AndNeverStartsInTheMiddleOfASurrogatePair()
    {
        var (acc, _) = Make(maxLine: 5000);
        acc.Append("\U0001F600" + new string('a', SyncGit.maxStderrKeptChars - 1) + "\n");
        acc.Complete();
        var text = acc.Text();
        Assert.True(text.Length <= SyncGit.maxStderrKeptChars);
        Assert.False(char.IsLowSurrogate(text[0]));
        var (many, _) = Make(maxLine: 5000);
        for (var j = 0; j < 50; j++)
            many.Append(new string('z', 900) + "\n");
        many.Complete();
        Assert.True(many.Text().Length <= SyncGit.maxStderrKeptChars);
    }
}
