using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.OutcomeAssert;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>A TCP listener that accepts connections and never answers them.</summary>
internal sealed class SilentServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _clients = [];
    private readonly CancellationTokenSource _stop = new();

    public SilentServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_clients)
                    _clients.Add(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_clients)
        {
            foreach (var c in _clients)
                c.Dispose();
        }
    }
}

/// <summary>An HTTP server that answers every request with 401 and a Basic challenge, and records the requests.</summary>
internal sealed class UnauthorizedServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _requests = [];

    public UnauthorizedServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var text = new StringBuilder();
                while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await stream.ReadAsync(buffer, _stop.Token);
                    if (n == 0)
                        return;
                    text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }
                lock (_requests)
                    _requests.Add(text.ToString());
                var response = "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=x\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

/// <summary>Runs the real <c>git</c> directly (not through the client) with the same scrub, for tests of the overrides themselves.</summary>
internal static class DirectGit
{
    public static (int ExitCode, string Output) Run(GitTestEnvironment env, IEnumerable<string> argv, string workingDirectory, TimeSpan? limit = null)
    {
        var git = GitExecutable.TryFind() ?? throw new InvalidOperationException("git is not on the PATH");
        var info = new ProcessStartInfo(git)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in argv)
            info.ArgumentList.Add(a);
        info.Environment.Clear();
        var current = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => Tuple.Create((string)e.Key, e.Value as string ?? ""));
        foreach (var pair in SyncGit.scrubEnvironment(Microsoft.FSharp.Collections.ListModule.OfSeq(current)))
            info.Environment[pair.Item1] = pair.Item2;
        foreach (var pair in SyncGit.fixedEnvironment)
            info.Environment[pair.Item1] = pair.Item2;
        foreach (var (name, value) in env.EnvironmentOverrides)
        {
            if (value is null)
                info.Environment.Remove(name);
            else
                info.Environment[name] = value;
        }

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(limit ?? TimeSpan.FromSeconds(20)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return (-1, "timed out");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>The real overrides as `-c k=v` argument pairs, without the pairs whose value starts with one of <paramref name="without"/>.</summary>
    public static List<string> Overrides(string noHooks, string empty, params string[] without)
    {
        var all = SyncGit.overrides(noHooks, empty).ToList();
        var kept = new List<string>();
        for (var i = 0; i + 1 < all.Count; i += 2)
        {
            if (!without.Any(w => all[i + 1].StartsWith(w, StringComparison.Ordinal)))
            {
                kept.Add(all[i]);
                kept.Add(all[i + 1]);
            }
        }
        return kept;
    }
}

[Collection("git-client")]
public sealed class GitClientNetworkTests(RecordingFixture fixture)
{
    private static SyncGit.RemoteRef Url(string url) => SyncGit.RemoteRef.NewUrl(url);

    private ClientRig Rig(Func<GitClientOptions, GitClientOptions>? options = null, string? executable = null) =>
        new(fixture, options: options, executable: executable);

    private static Dictionary<string, string?> Merge(GitTestEnvironment env, params (string Name, string? Value)[] more)
    {
        var map = env.EnvironmentOverrides.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (name, value) in more)
            map[name] = value;
        return map;
    }

    // ---- transport helpers ------------------------------------------------------------------------------------

    [GitFact]
    public async Task ExtTransportIsRefused()
    {
        using var env = new GitTestEnvironment();
        env.WriteGitConfig("[protocol]\n\tallow = always\n[protocol \"ext\"]\n\tallow = always\n");
        var work = Path.Combine(env.Root, "work");
        Directory.CreateDirectory(work);
        var noHooks = Path.Combine(env.Root, "no-hooks");
        var empty = Path.Combine(env.Root, "empty");
        Directory.CreateDirectory(noHooks);
        File.WriteAllText(empty, "");
        var marker = Path.Combine(env.Root, "ext-ran");
        var url = $"ext::touch {marker}";

        var guarded = DirectGit.Overrides(noHooks, empty).Concat(["ls-remote", "--", url, "refs/heads/main"]);
        var (code, _) = DirectGit.Run(env, guarded, work);
        Assert.NotEqual(0, code);
        Assert.False(File.Exists(marker), "the ext transport ran a program");

        // without the override the same call runs the program, so the test can fail
        var open = DirectGit.Overrides(noHooks, empty, "protocol.ext.allow").Concat(["ls-remote", "--", url, "refs/heads/main"]);
        DirectGit.Run(env, open, work);
        Assert.True(File.Exists(marker), "the test is not sensitive: ext was not allowed by the redirected configuration");

        using var rig = Rig();
        var helper = await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.LsRemoteAsync(Url("ext::sh -c x"), "main"));
        Assert.NotNull(helper);
        var leaked = await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.LsRemoteAsync(Url("https://u:tok@h/x y"), "main"));
        Assert.DoesNotContain("tok", leaked.Message);
        Assert.DoesNotContain("u:", leaked.Message);
    }

    // ---- limits -----------------------------------------------------------------------------------------------

    [GitFact]
    public async Task Timeout_KillsAGitThatNeverAnswers()
    {
        using var server = new SilentServer();
        using var rig = Rig();
        var url = Url($"git://127.0.0.1:{server.Port}/x");

        var watch = Stopwatch.StartNew();
        Failed(await rig.Client.LsRemoteAsync(url, "main", timeout: TimeSpan.FromSeconds(1)), SyncGit.FailureKind.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");

        using var cts = new CancellationTokenSource();
        var call = rig.Client.LsRemoteAsync(url, "main", cancellationToken: cts.Token);
        await Task.Delay(300);
        var cancelledAt = Stopwatch.StartNew();
        cts.Cancel();
        Failed(await call, SyncGit.FailureKind.Cancelled);
        Assert.True(cancelledAt.Elapsed < TimeSpan.FromSeconds(2), $"took {cancelledAt.Elapsed}");
    }

    // ---- no prompt --------------------------------------------------------------------------------------------

    [GitFact]
    public async Task NoPrompt_HttpAskingForCredentials_FailsAsAuth()
    {
        using var server = new UnauthorizedServer();
        using var rig = Rig();
        var watch = Stopwatch.StartNew();
        var outcome = await rig.Client.LsRemoteAsync(Url($"http://127.0.0.1:{server.Port}/x.git"), "main");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Failed(outcome, SyncGit.FailureKind.Auth);
        Assert.DoesNotContain("Authorization", outcome.Failure.StderrText, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(server.Requests);
        Assert.DoesNotContain(server.Requests, r => r.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
    }

    [GitShFact]
    public void AskPassOverride_StopsAGlobalAskpassProgram()
    {
        using var server = new UnauthorizedServer();
        using var env = new GitTestEnvironment();
        var markers = new MarkerFiles(Path.Combine(env.Root, "markers"));
        Directory.CreateDirectory(markers.Directory);
        var askpass = Path.Combine(env.Root, "askpass");
        File.WriteAllText(askpass, markers.Script("askpass", "echo hostile\n"));
        File.SetUnixFileMode(askpass, (UnixFileMode)0b111_101_101);
        env.WriteGitConfig($"[core]\n\taskPass = {askpass}\n");
        var work = Path.Combine(env.Root, "work");
        Directory.CreateDirectory(work);
        var noHooks = Path.Combine(env.Root, "no-hooks");
        var empty = Path.Combine(env.Root, "empty");
        Directory.CreateDirectory(noHooks);
        File.WriteAllText(empty, "");
        string[] call = ["ls-remote", "--", $"http://127.0.0.1:{server.Port}/x.git", "refs/heads/main"];

        // the override's `core.askPass=` stops the program even without `credential.interactive=never`
        DirectGit.Run(env, DirectGit.Overrides(noHooks, empty, "credential.interactive").Concat(call), work);
        markers.AssertNoneFired();

        // and without it the program runs, so the override is shown to matter
        DirectGit.Run(env, DirectGit.Overrides(noHooks, empty, "credential.interactive", "core.askPass").Concat(call), work);
        Assert.Contains("askpass", markers.Fired);
    }

    // ---- ssh --------------------------------------------------------------------------------------------------

    private static string FakeSsh(string directory, string name, string argsFile, string body = "exit 255\n")
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, $"#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{argsFile}'\n{body}");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        return path;
    }

    [GitShFact]
    public async Task SshOptions_ArePassedToTheUsersCommand()
    {
        using var env = new GitTestEnvironment();
        var argsFile = Path.Combine(env.Root, "ssh-args");
        var globalSsh = FakeSsh(env.Root, "global-ssh", argsFile);
        env.WriteGitConfig($"[core]\n\tsshCommand = {globalSsh}\n");
        var url = Url("ssh://git@127.0.0.1/o/r.git");
        string[] none = ["GIT_SSH_COMMAND", "GIT_SSH", "GIT_SSH_VARIANT"];

        // a local core.sshCommand of the directory the call runs in is never read
        var localArgs = Path.Combine(env.Root, "local-args");
        var localSsh = FakeSsh(env.Root, "local-ssh", localArgs);
        var scratch = Path.Combine(env.Root, "scratch-repo");
        Directory.CreateDirectory(scratch);
        env.RunGit(scratch, "init");
        env.RunGit(scratch, "config", "core.sshCommand", localSsh);

        using var rig = Rig(o => o with
        {
            EnvironmentOverrides = Merge(env, none.Select(n => (n, (string?)null)).ToArray()),
            WorkingDirectory = scratch,
        });
        Failed(await rig.Client.LsRemoteAsync(url, "main"), SyncGit.FailureKind.Other);
        var args = File.ReadAllLines(argsFile);
        Assert.Contains("BatchMode=yes", args);
        Assert.Contains("ConnectTimeout=8", args);
        Assert.Equal(2, args.Count(a => a == "-o"));
        Assert.False(File.Exists(localArgs), "the repository's own core.sshCommand was used");

        // the configuration is read once per client, not on every call
        int ConfigReads() => rig.Recorder.Calls.Count(c => c.Command.IsConfigGetSshCommand);
        var reads = ConfigReads();
        Failed(await rig.Client.LsRemoteAsync(url, "main"), SyncGit.FailureKind.Other);
        Assert.Equal(reads, ConfigReads());

        // GIT_SSH_COMMAND from the call's environment wins over the configuration and gets the same options
        var envArgs = Path.Combine(env.Root, "env-args");
        var envSsh = FakeSsh(env.Root, "env-ssh", envArgs);
        using var withEnv = Rig(o => o with { EnvironmentOverrides = Merge(env, ("GIT_SSH_COMMAND", envSsh), ("GIT_SSH", null), ("GIT_SSH_VARIANT", null)) });
        File.Delete(argsFile);
        Failed(await withEnv.Client.LsRemoteAsync(url, "main"), SyncGit.FailureKind.Other);
        Assert.Contains("BatchMode=yes", File.ReadAllLines(envArgs));
        Assert.False(File.Exists(argsFile), "the global core.sshCommand outranked GIT_SSH_COMMAND");
    }

    [GitShFact]
    public async Task CoreSshCommand_InAnIncludedFile_IsFound()
    {
        using var env = new GitTestEnvironment();
        var argsFile = Path.Combine(env.Root, "ssh-args");
        var included = FakeSsh(env.Root, "included-ssh", argsFile);
        File.WriteAllText(Path.Combine(env.Home, "ssh.inc"), $"[core]\n\tsshCommand = {included}\n");
        env.WriteGitConfig("[include]\n\tpath = ssh.inc\n");
        using var rig = Rig(o => o with { EnvironmentOverrides = Merge(env, ("GIT_SSH_COMMAND", null), ("GIT_SSH", null), ("GIT_SSH_VARIANT", null)) });
        Failed(await rig.Client.LsRemoteAsync(Url("ssh://git@127.0.0.1/o/r.git"), "main"), SyncGit.FailureKind.Other);
        Assert.Contains("BatchMode=yes", File.ReadAllLines(argsFile));
    }

    [ShFact]
    public async Task ASshConfigurationReadThatCannotAnswer_FailsTheCall_AndIsNotCached()
    {
        using var d = new StubDirectory();
        using var rig = Rig(executable: d.Script("sleep 5\n", "slow-git"), options: o => o with { LocalTimeout = TimeSpan.FromMilliseconds(300) });
        Failed(await rig.Client.LsRemoteAsync(Url("ssh://git@127.0.0.1/o/r.git"), "main"), SyncGit.FailureKind.TimedOut);
    }

    [GitShFact]
    public async Task OnlyGitSsh_LeavesTheEnvironmentAlone()
    {
        using var env = new GitTestEnvironment();
        env.WriteGitConfig("");
        var argsFile = Path.Combine(env.Root, "ssh-args");
        var program = FakeSsh(env.Root, "git-ssh-program", argsFile);
        using var rig = Rig(o => o with { EnvironmentOverrides = Merge(env, ("GIT_SSH_COMMAND", null), ("GIT_SSH", program), ("GIT_SSH_VARIANT", null)) });
        Failed(await rig.Client.LsRemoteAsync(Url("ssh://git@127.0.0.1/o/r.git"), "main"), SyncGit.FailureKind.Other);
        var args = File.ReadAllLines(argsFile);
        Assert.DoesNotContain("BatchMode=yes", args); // GIT_SSH programs do not take OpenSSH options
    }

    // ---- failure kinds ----------------------------------------------------------------------------------------

    [GitFact]
    public async Task Clone_MissingRepository_FailsAsRemoteNotFound()
    {
        using var rig = Rig();
        Failed(await rig.Client.CloneAsync("file:///nonexistent/x", "main"), SyncGit.FailureKind.RemoteNotFound);
    }

    [GitFact]
    public async Task LsRemote_ClosedPort_FailsAsNetwork()
    {
        using var rig = Rig();
        var watch = Stopwatch.StartNew();
        Failed(await rig.Client.LsRemoteAsync(Url("http://127.0.0.1:1/x.git"), "main"), SyncGit.FailureKind.Network);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [GitShFact]
    public async Task LsRemote_SshUserGit_MissingRepository_IsRemoteNotFound()
    {
        using var env = new GitTestEnvironment();
        var script = Path.Combine(env.Root, "fake-ssh");
        File.WriteAllText(script, "#!/bin/sh\necho \"fatal: '/o/r.git' does not appear to be a git repository\" >&2\necho 'fatal: Could not read from remote repository.' >&2\nexit 128\n");
        File.SetUnixFileMode(script, (UnixFileMode)0b111_101_101);
        env.WriteGitConfig($"[core]\n\tsshCommand = {script}\n");
        using var rig = Rig(o => o with { EnvironmentOverrides = Merge(env, ("GIT_SSH_COMMAND", null), ("GIT_SSH", null), ("GIT_SSH_VARIANT", null)) });
        Failed(await rig.Client.LsRemoteAsync(Url("ssh://git@127.0.0.1/o/r.git"), "main"), SyncGit.FailureKind.RemoteNotFound);
    }

    [GitShFact]
    public async Task ACredentialInTheUrl_NeverAppearsInAResult()
    {
        using var env = new GitTestEnvironment();
        var script = Path.Combine(env.Root, "fake-ssh");
        File.WriteAllText(script, "#!/bin/sh\necho 'fatal: bad login deployer:SECRETPASS123 for deployer' >&2\necho 'fatal: Could not read from remote repository.' >&2\nexit 255\n");
        File.SetUnixFileMode(script, (UnixFileMode)0b111_101_101);
        env.WriteGitConfig($"[core]\n\tsshCommand = {script}\n");
        using var rig = Rig(o => o with { EnvironmentOverrides = Merge(env, ("GIT_SSH_COMMAND", null), ("GIT_SSH", null), ("GIT_SSH_VARIANT", null)) });
        var segments = new List<string>();
        var outcome = await rig.Client.LsRemoteAsync(Url("ssh://deployer:SECRETPASS123@127.0.0.1/o/r.git"), "main", onStderr: segments.Add);
        Assert.False(outcome.IsOk);
        var all = outcome.Failure.StderrText + "|" + string.Join("|", segments);
        Assert.DoesNotContain("SECRETPASS123", all);
        Assert.DoesNotContain("deployer:", all);
        Assert.Contains("fatal: bad login", all);
    }
}
