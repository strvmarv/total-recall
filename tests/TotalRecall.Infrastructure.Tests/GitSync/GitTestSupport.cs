using System.Diagnostics;
using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Marks a script executable where that means something.</summary>
internal static class Posix
{
    public static void Mode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }

    public static void Executable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }
}

/// <summary>A fact that needs a POSIX <c>sh</c>: skipped on Windows and where no <c>sh</c> is on the PATH.</summary>
internal sealed class ShFactAttribute : FactAttribute
{
    public ShFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "needs a POSIX sh";
        else if (!File.Exists("/bin/sh") && !File.Exists("/usr/bin/sh"))
            Skip = "no sh found";
    }
}

/// <summary>A fact that needs both a usable <c>git</c> and a POSIX <c>sh</c>.</summary>
internal sealed class GitShFactAttribute : FactAttribute
{
    public GitShFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "needs a POSIX sh";
        else if (!File.Exists("/bin/sh") && !File.Exists("/usr/bin/sh"))
            Skip = "no sh found";
        else
            Skip = GitFactAttribute.ComputeReason();
    }
}

/// <summary>A fact that needs a usable <c>git</c> (2.28 or later); skipped with a reason when there is none.</summary>
internal sealed class GitFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(ComputeReason);

    public GitFactAttribute()
    {
        Skip = Reason.Value;
    }

    internal static string? ComputeReason()
    {
        var git = GitExecutable.TryFind();
        if (git is null)
            return "git is not on the PATH";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(git, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var version = SyncGit.parseVersion(text);
            if (version is null)
                return "could not read the git version";
            return SyncGit.isSupportedVersion(version.Value.Item1, version.Value.Item2) ? null : $"git {version.Value.Item1}.{version.Value.Item2} is older than 2.28";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "git could not be started";
        }
    }
}

/// <summary>
/// Files that hostile hooks, askpass and fsmonitor programs write when git runs them. A test that expects git to
/// ignore the user's configuration asserts that none exists.
/// </summary>
internal sealed class MarkerFiles(string directory)
{
    public string Directory { get; } = directory;

    public IReadOnlyList<string> Fired =>
        System.IO.Directory.Exists(Directory)
            ? [.. System.IO.Directory.GetFiles(Directory).Select(Path.GetFileName).Select(n => n!).Order(StringComparer.Ordinal)]
            : [];

    public void AssertNoneFired()
    {
        var fired = Fired;
        Assert.True(fired.Count == 0, "the user's configuration ran something: " + string.Join(", ", fired));
    }

    /// <summary>A shell script that writes the marker, then runs <paramref name="tail"/> (which must end by itself) or exits 0.</summary>
    internal string Script(string name, string? tail = null) =>
        $"#!/bin/sh\ntouch '{Path.Combine(Directory, name)}'\n" + (tail ?? "exit 0\n");
}

/// <summary>
/// A throwaway home directory and the environment values that point git at it, so a test never reads or writes the
/// developer's own configuration.
/// </summary>
internal sealed class GitTestEnvironment : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tr-githome-" + Guid.NewGuid().ToString("N"));

    public GitTestEnvironment()
    {
        Directory.CreateDirectory(Home);
    }

    public string Home => Path.Combine(Root, "home");

    public string GitConfigPath => Path.Combine(Home, ".gitconfig");

    /// <summary>Set after the scrub: the redirected home and the global and system configuration.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentOverrides => new Dictionary<string, string?>
    {
        ["HOME"] = Home,
        ["XDG_CONFIG_HOME"] = Path.Combine(Home, ".config"),
        ["USERPROFILE"] = Home,
        ["GIT_CONFIG_GLOBAL"] = GitConfigPath,
        ["GIT_CONFIG_NOSYSTEM"] = "1",
    };

    public void WriteGitConfig(string content) => File.WriteAllText(GitConfigPath, content);

    /// <summary>
    /// The shared hostile global configuration: signing, hooks, line-ending conversion, a default branch of
    /// <c>trunk</c>, a template with a hook, an askpass program, an uppercasing attributes filter, an fsmonitor program,
    /// <c>user.useConfigOnly</c> and no identity. Hooks, askpass, fsmonitor and the filter write a marker when they
    /// run. The template hook (marker <c>template-pre-commit</c>) cannot fire while <c>core.hooksPath</c> is set, so
    /// a copied template shows only as a non-empty <c>.git/hooks</c>: tests assert that directory is empty.
    /// </summary>
    public MarkerFiles WriteHostileConfig()
    {
        var markers = new MarkerFiles(Path.Combine(Root, "markers"));
        Directory.CreateDirectory(markers.Directory);

        var hooks = Path.Combine(Root, "hooks");
        Directory.CreateDirectory(hooks);
        foreach (var hook in new[] { "pre-commit", "post-checkout", "post-commit", "commit-msg" })
            WriteExecutable(Path.Combine(hooks, hook), markers.Script("hook-" + hook));

        var template = Path.Combine(Root, "template", "hooks");
        Directory.CreateDirectory(template);
        WriteExecutable(Path.Combine(template, "pre-commit"), markers.Script("template-pre-commit"));

        var askpass = Path.Combine(Root, "askpass");
        WriteExecutable(askpass, markers.Script("askpass", "echo hostile-password\n"));
        var fsmonitor = Path.Combine(Root, "fsmonitor");
        WriteExecutable(fsmonitor, markers.Script("fsmonitor"));

        var filter = Path.Combine(Root, "filter-clean");
        WriteExecutable(filter, markers.Script("filter", "exec tr a-z A-Z\n"));

        var attributes = Path.Combine(Root, "attributes");
        File.WriteAllText(attributes, "* filter=up\n");

        string P(string path) => path.Replace('\\', '/');
        WriteGitConfig(
            $"""
            [commit]
                gpgsign = true
            [push]
                gpgSign = true
            [core]
                hooksPath = {P(hooks)}
                autocrlf = true
                askPass = {P(askpass)}
                attributesFile = {P(attributes)}
                fsmonitor = {P(fsmonitor)}
            [init]
                defaultBranch = trunk
                templateDir = {P(Path.Combine(Root, "template"))}
            [user]
                useConfigOnly = true
            [filter "up"]
                clean = {P(filter)}
                smudge = cat
            """);
        return markers;
    }

    /// <summary>
    /// Runs the real <c>git</c> for a test's own setup and assertions, with every <c>GIT_*</c> variable removed and the
    /// redirected home. Returns standard output; throws with both streams on a non-zero exit.
    /// </summary>
    public string RunGit(string workingDirectory, params string[] args)
    {
        var (code, output, error) = TryRunGit(workingDirectory, args);
        if (code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {code}: {output}{error}");
        return output;
    }

    public (int ExitCode, string Output, string Error) TryRunGit(string workingDirectory, params string[] args)
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
        foreach (var a in args)
            info.ArgumentList.Add(a);
        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
            info.Environment.Remove(name);
        foreach (var (name, value) in EnvironmentOverrides)
            info.Environment[name] = value;
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["LC_ALL"] = "C";

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        Posix.Executable(path);
    }

    public void Dispose()
    {
        try
        {
            ForceDelete(Root);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes a tree that may hold read-only git object files.</summary>
    internal static void ForceDelete(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, true);
    }
}

/// <summary>
/// A bare repository reached by a <c>file://</c> URL, seeded through a scratch clone. It uses its own clean home, never
/// the hostile one.
/// </summary>
internal sealed class BareRemote : IDisposable
{
    private readonly GitTestEnvironment _env = new();
    private readonly string _scratch;
    private int _counter;

    private readonly string _root;

    private BareRemote(string root)
    {
        _root = root;
        Path = System.IO.Path.Combine(root, "remote.git");
        _scratch = System.IO.Path.Combine(_env.Root, "scratch");
    }

    /// <summary>The bare repository's directory.</summary>
    public string Path { get; }

    public string Url => new Uri(Path).AbsoluteUri;

    public string Head() => _env.RunGit(Path, "rev-parse", "main").Trim();

    /// <summary>
    /// Creates the remote with <paramref name="commits"/> commits on <c>main</c> and
    /// <paramref name="filesInFirstCommit"/> files in the first; with no commits it is an empty remote.
    /// </summary>
    public static BareRemote Create(int commits = 1, int filesInFirstCommit = 1)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tr-bare-" + Guid.NewGuid().ToString("N"));
        var remote = new BareRemote(root);
        try
        {
            Directory.CreateDirectory(remote.Path);
            remote._env.RunGit(remote.Path, "init", "--bare", "-b", "main");
            remote._env.RunGit(remote._env.Root, "clone", remote.Path, remote._scratch);
            remote.Identify();
            for (var i = 0; i < commits; i++)
            {
                var files = i == 0 ? filesInFirstCommit : 1;
                for (var f = 0; f < files; f++)
                    File.WriteAllText(System.IO.Path.Combine(remote._scratch, $"file-{i}-{f}.txt"), $"content {i} {f}\n");
                remote._env.RunGit(remote._scratch, "add", "-A");
                remote._env.RunGit(remote._scratch, "commit", "--allow-empty", "-m", $"commit {i}");
            }
            if (commits > 0)
                remote._env.RunGit(remote._scratch, "push", "origin", "HEAD:refs/heads/main");
            return remote;
        }
        catch
        {
            remote.Dispose();
            throw;
        }
    }

    private void Identify()
    {
        _env.RunGit(_scratch, "config", "user.name", "Test");
        _env.RunGit(_scratch, "config", "user.email", "test@example.com");
        _env.RunGit(_scratch, "config", "commit.gpgsign", "false");
        _env.RunGit(_scratch, "checkout", "-B", "main");
    }

    /// <summary>Adds one commit (a file, written with exactly the given content) and returns the new head; an unchanged file still gives a commit.</summary>
    public string PushCommit(string? fileName = null, string? content = null)
    {
        var n = ++_counter;
        RefreshScratch();
        fileName ??= $"pushed-{n}.txt";
        var target = System.IO.Path.Combine(_scratch, fileName);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content ?? $"pushed {n}\n"); // a given content is written verbatim
        _env.RunGit(_scratch, "add", "-A");
        _env.RunGit(_scratch, "commit", "--allow-empty", "-m", $"push {n}");
        _env.RunGit(_scratch, "push", "origin", "HEAD:refs/heads/main");
        return Head();
    }

    /// <summary>Brings the scratch clone up to date with the bare repository, so pushes from it are fast-forwards.</summary>
    private void RefreshScratch()
    {
        _env.RunGit(_scratch, "fetch", "origin");
        var (code, _, _) = _env.TryRunGit(_scratch, "rev-parse", "--verify", "--quiet", "refs/remotes/origin/main");
        if (code == 0)
            _env.RunGit(_scratch, "reset", "--hard", "refs/remotes/origin/main");
    }

    /// <summary>Rewrites the head by hand (amend and force-push): the old head is no longer an ancestor of the new one.</summary>
    public string RewriteHistory()
    {
        RefreshScratch();
        _env.RunGit(_scratch, "commit", "--amend", "--allow-empty", "-m", $"rewritten {++_counter}");
        _env.RunGit(_scratch, "push", "--force", "origin", "HEAD:refs/heads/main");
        return Head();
    }

    public void Dispose()
    {
        try
        {
            GitTestEnvironment.ForceDelete(_root);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            _env.Dispose();
        }
    }
}

/// <summary>Records every command and its full argument list, then delegates.</summary>
internal sealed class RecordingGitProcess(IGitProcess inner) : IGitProcess
{
    private readonly object _gate = new();
    private readonly List<(SyncGit.GitCommand Command, IReadOnlyList<string> Argv)> _calls = [];

    public IReadOnlyList<(SyncGit.GitCommand Command, IReadOnlyList<string> Argv)> Calls
    {
        get
        {
            lock (_gate)
                return [.. _calls];
        }
    }

    public IEnumerable<IReadOnlyList<string>> Argvs => Calls.Select(c => c.Argv);

    public Task<GitResult> RunAsync(SyncGit.GitCommand command, GitContext context, GitRunOptions options, CancellationToken cancellationToken)
    {
        var built = SyncGit.build(
            new SyncGit.BuildContext(context.CloneDirectory, context.NoHooksDirectory, context.EmptyFile, OperatingSystem.IsWindows()),
            command);
        if (!built.IsError)
        {
            lock (_gate)
                _calls.Add((command, [.. built.ResultValue]));
        }
        return inner.RunAsync(command, context, options, cancellationToken);
    }
}

/// <summary>Asserts that no recorded argument list can force, delete, mirror or prune.</summary>
internal static class ForceFlags
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "--force", "-f", "--force-with-lease", "--force-if-includes", "--mirror", "--delete", "--prune", "-d", "-D",
    };

    public static void AssertNone(IEnumerable<IReadOnlyList<string>> argvs)
    {
        foreach (var argv in argvs)
        {
            for (var i = 0; i < argv.Count; i++)
            {
                // the value after `-m` is a commit message, which may begin with anything
                if (i > 0 && argv[i - 1] == "-m")
                    continue;
                var arg = argv[i];
                var problem = Problem(arg, argv);
                if (problem is not null)
                    Assert.Fail($"{problem}: {string.Join(' ', argv)}");
            }
        }
    }

    private static string? Problem(string arg, IReadOnlyList<string> argv)
    {
        if (Forbidden.Contains(arg) || arg.StartsWith("--force-with-lease=", StringComparison.Ordinal))
            return $"forbidden option {arg}";
        // git accepts an unambiguous prefix of a long option (`--forc`, `--mirr`)
        if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
        {
            var name = arg.Split('=')[0];
            if (Forbidden.Any(f => f.StartsWith("--", StringComparison.Ordinal) && f.StartsWith(name, StringComparison.Ordinal)))
                return $"abbreviation of a forbidden option {arg}";
        }
        // `-fd` is the one short force shape, and only for `clean`
        if (arg.Length > 1 && arg[0] == '-' && arg[1] != '-' && arg.Contains('f') && !arg.StartsWith("--", StringComparison.Ordinal))
        {
            if (!(arg == "-fd" && argv.Contains("clean")))
                return $"force-like short option {arg}";
        }
        if (arg.StartsWith('+'))
        {
            var ok = arg.StartsWith("+refs/heads/", StringComparison.Ordinal) && arg.Contains(":refs/remotes/origin/", StringComparison.Ordinal);
            if (!ok)
                return $"forcing refspec {arg}";
        }
        if (arg.StartsWith(':'))
            return $"deleting refspec {arg}";
        return null;
    }
}
