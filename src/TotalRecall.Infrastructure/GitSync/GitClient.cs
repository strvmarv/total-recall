using System.Text;
using Microsoft.FSharp.Core;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>Whether one commit is an ancestor of another; <c>Unknown</c> when git could not tell (a commit a shallow clone does not hold).</summary>
public enum Tri
{
    Yes,
    No,
    Unknown,
}

/// <summary>Timeouts and test seams of a <see cref="GitClient"/>.</summary>
public sealed record GitClientOptions
{
    /// <summary>The wall-clock limit of a call that stays on this machine.</summary>
    public TimeSpan LocalTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The wall-clock limit of a call that uses the network.</summary>
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Environment values set after the scrub on every call (a null value removes the variable).</summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentOverrides { get; init; }

    /// <summary>A cap on the standard output of every call; none means the process default (64 MiB).</summary>
    public int? MaxStdoutBytes { get; init; }

    /// <summary>The directory a process starts in; by default the sync directory (none for the version check).</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>
/// The typed git operations of sync. Every call goes through <see cref="IGitProcess"/> with a command from the closed
/// <see cref="SyncGit.GitCommand"/> set, so force, delete and mirror cannot be expressed. The constructor touches no
/// file; the first call of a command that takes the configuration overrides creates the sync directory, the empty
/// hooks directory and the empty file. `SyncSetupService` (the hidden `sync` commands, milestone 1a-3) is its caller; the sync cycle is milestone 1b.
/// </summary>
public sealed partial class GitClient
{
    /// <summary>The standard output a call may produce unless a lower limit is given (spec decision 11).</summary>
    internal const long DefaultMaxStdoutBytes = 64L << 20;

    private readonly IGitProcess _process;
    private readonly string _executable;
    private readonly GitSyncPaths _paths;
    private readonly GitClientOptions _options;
    private readonly object _setupGate = new();
    private readonly SemaphoreSlim _sshGate = new(1, 1);
    private (bool Computed, string? Command) _ssh;

    public GitClient(IGitProcess process, string executable, GitSyncPaths paths, GitClientOptions? options = null)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _executable = executable ?? throw new ArgumentNullException(nameof(executable));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _options = options ?? new GitClientOptions();
    }

    /// <summary>The path of the disposable clone.</summary>
    public string CloneDirectory => _paths.CloneDirectory;

    // ---- the one way a command runs -------------------------------------------------------------------------

    private GitContext Context() =>
        new(_executable, _paths.CloneDirectory, _paths.NoHooksDirectory, _paths.EmptyFile);

    /// <summary>A failed result that no process produced.</summary>
    private static GitResult Synthetic(SyncGit.FailureKind kind, string text) =>
        new(null, [], text, kind, TimeSpan.Zero);

    private async Task<GitResult> RunAsync(
        SyncGit.GitCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? onStderr = null,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null,
        IReadOnlyList<string>? redactValues = null,
        long? maxStdoutBytes = null)
    {
        if (cancellationToken.IsCancellationRequested)
            return Synthetic(SyncGit.FailureKind.Cancelled, "");

        var takesOverrides = SyncGit.takesOverrides(command);
        // the version check needs no directory; every other call (the ssh configuration read included) runs in the sync directory
        var inSyncDirectory = takesOverrides || command.IsConfigGetSshCommand;
        if (inSyncDirectory)
        {
            var failure = EnsureCreated();
            if (failure is not null)
                return failure;
        }

        // a clone-scoped command never starts against a clone that is missing, damaged or a link
        if (SyncGit.isCloneScoped(command) && !CloneIsSound())
            return Synthetic(SyncGit.FailureKind.NotARepository, "not a git repository: the clone is missing or is not a plain directory");

        // GIT_CEILING_DIRECTORIES is a list separated by the platform's path separator and has no escape, so a database
        // directory whose own path contains it cannot be named: fail closed rather than leave git free to read the
        // configuration of an enclosing repository
        if (inSyncDirectory && _paths.DatabaseDirectory.Contains(Path.PathSeparator))
        {
            return Synthetic(
                SyncGit.FailureKind.Other,
                $"git sync cannot run: the database directory's path contains '{Path.PathSeparator}', which git's ceiling-directory list cannot hold");
        }

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (_options.EnvironmentOverrides is not null)
        {
            foreach (var (name, value) in _options.EnvironmentOverrides)
                environment[name] = value;
        }
        if (extraEnvironment is not null)
        {
            foreach (var (name, value) in extraEnvironment)
                environment[name] = value;
        }
        // a sync directory inside the user's own repository must not let a command that is not clone-scoped read that
        // repository's configuration (`url.<x>.insteadOf`, `credential.helper`)
        environment["GIT_CEILING_DIRECTORIES"] = _paths.DatabaseDirectory;

        // a caller's own limit only ever lowers the process default
        var cap = Math.Min(Math.Min(maxStdoutBytes ?? long.MaxValue, _options.MaxStdoutBytes ?? long.MaxValue), DefaultMaxStdoutBytes);
        var runOptions = new GitRunOptions(timeout)
        {
            EnvironmentOverrides = environment,
            RedactValues = redactValues,
            OnStderrSegment = onStderr,
            WorkingDirectory = inSyncDirectory ? _options.WorkingDirectory ?? _paths.SyncDirectory : null,
            MaxStdoutBytes = cap,
        };
        return await _process.RunAsync(command, Context(), runOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the sync directory, the empty hooks directory and the empty file when the sync directory is missing (the
    /// first call, or after something removed it: a missing working directory would otherwise read as "git is not
    /// installed"). A failure is a failed result of kind Other.
    /// </summary>
    private GitResult? EnsureCreated()
    {
        lock (_setupGate)
        {
            if (Directory.Exists(_paths.SyncDirectory) && Directory.Exists(_paths.NoHooksDirectory) && File.Exists(_paths.EmptyFile))
                return null;
            try
            {
                _paths.EnsureCreated();
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Synthetic(SyncGit.FailureKind.Other, "the sync directory could not be created: " + e.GetType().Name);
            }
        }
    }

    // ---- the network: ssh options and redaction --------------------------------------------------------------

    private static FSharpOption<string> Opt(string? value) =>
        string.IsNullOrWhiteSpace(value) ? FSharpOption<string>.None : FSharpOption<string>.Some(value);

    /// <summary>A name set in the client's overrides is authoritative (a null value means absent); else the process's own.</summary>
    private string? EnvironmentValue(string name) =>
        _options.EnvironmentOverrides is not null && _options.EnvironmentOverrides.TryGetValue(name, out var overridden)
            ? overridden
            : Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// The <c>GIT_SSH_COMMAND</c> for network calls, composed once per instance: the user's command (the process's own
    /// <c>GIT_SSH_COMMAND</c>, else <c>core.sshCommand</c> as git resolves it for a user outside any repository: the
    /// system, global and XDG files, included files followed, read from the sync directory under the ceiling directory,
    /// so never the configuration of whatever repository the process is in; an <c>includeIf "hasconfig:remote.*.url:…"</c>
    /// is therefore not seen, and such a user sets <c>GIT_SSH_COMMAND</c>) with <c>-o BatchMode=yes -o ConnectTimeout=8</c>
    /// appended. Null leaves the variable alone (only <c>GIT_SSH</c> is set, which does not take OpenSSH options).
    /// </summary>
    private async Task<(string? Command, GitResult? Failure)> SshCommandAsync(CancellationToken cancellationToken)
    {
        await _sshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ssh.Computed)
                return (_ssh.Command, null);

            var envCommand = EnvironmentValue("GIT_SSH_COMMAND");
            string? configCommand = null;
            if (string.IsNullOrWhiteSpace(envCommand))
            {
                var read = await RunAsync(SyncGit.GitCommand.ConfigGetSshCommand, _options.LocalTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (read.Failure is null)
                    configCommand = Text(read).Trim();
                else if (read.ExitCode != 1) // 1 is "unset"; anything else (a timeout, no git) is not an answer: fail the call, cache nothing
                    return (null, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var command = SyncGit.composeSshCommand(
                new SyncGit.SshSource(Opt(envCommand), Opt(configCommand), Opt(EnvironmentValue("GIT_SSH")), Opt(EnvironmentValue("GIT_SSH_VARIANT"))));
            var value = command is null ? null : command.Value;
            _ssh = (true, value);
            return (value, null);
        }
        finally
        {
            _sshGate.Release();
        }
    }

    /// <summary>The literal pieces of a URL's user-info that must never appear in any text: the whole and the password part.</summary>
    private static IReadOnlyList<string> RedactValuesOf(string? url)
    {
        var values = new List<string>();
        var info = string.IsNullOrEmpty(url) ? null : SyncGit.userInfoOf(url);
        if (info is not null)
        {
            var whole = info.Value;
            values.Add(whole);
            var colon = whole.IndexOf(':');
            if (colon >= 0)
                values.Add(whole[(colon + 1)..]);
        }
        return values.Where(v => v.Length >= 8).ToList();
    }

    /// <summary>A call that may use ssh or carry a credential in its URL.</summary>
    private async Task<GitResult> RunNetworkAsync(
        SyncGit.GitCommand command,
        string? url,
        TimeSpan timeout,
        Action<string>? onStderr,
        CancellationToken cancellationToken,
        long? maxStdoutBytes = null)
    {
        if (cancellationToken.IsCancellationRequested)
            return Synthetic(SyncGit.FailureKind.Cancelled, "");
        Dictionary<string, string?>? extra = null;
        string? ssh;
        try
        {
            var composed = await SshCommandAsync(cancellationToken).ConfigureAwait(false);
            if (composed.Failure is not null)
                return composed.Failure;
            ssh = composed.Command;
        }
        catch (OperationCanceledException)
        {
            return Synthetic(SyncGit.FailureKind.Cancelled, "");
        }
        if (ssh is not null)
            extra = new Dictionary<string, string?> { ["GIT_SSH_COMMAND"] = ssh };
        return await RunAsync(command, timeout, cancellationToken, onStderr, extra, RedactValuesOf(url), maxStdoutBytes).ConfigureAwait(false);
    }

    private static GitOutcome<NoValue> Done(GitResult result) =>
        result.Failure is null ? GitOutcome<NoValue>.Ok(default) : GitOutcome<NoValue>.Fail(result);

    private static string Text(GitResult result) => Encoding.UTF8.GetString(result.Stdout);

    private static bool IsRealDirectory(DirectoryInfo directory) =>
        directory.Exists && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    /// True when the clone is what sync made: the <c>&lt;cloneKey&gt;</c> directory, the <c>repo</c> directory and its
    /// <c>.git</c> are real directories, not links and not a <c>gitdir:</c> file. A link planted in any of them would
    /// point <c>--git-dir</c> and <c>--work-tree</c> at someone else's repository.
    /// </summary>
    private bool CloneIsSound()
    {
        var clone = new DirectoryInfo(_paths.CloneDirectory);
        return IsRealDirectory(clone)
            && clone.Parent is { } parent
            && IsRealDirectory(parent)
            && IsRealDirectory(new DirectoryInfo(Path.Combine(_paths.CloneDirectory, ".git")));
    }

    // ---- operations -----------------------------------------------------------------------------------------

    /// <summary>The installed git's version; <c>GitTooOld</c> below 2.28, <c>NoGit</c> when it cannot start.</summary>
    public async Task<GitOutcome<(int Major, int Minor)>> VersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(SyncGit.GitCommand.Version, _options.LocalTimeout, cancellationToken).ConfigureAwait(false);
        if (result.Failure is not null)
            return GitOutcome<(int, int)>.Fail(result);
        var parsed = SyncGit.parseVersion(Text(result));
        if (parsed is null)
            return GitOutcome<(int, int)>.Fail(result with { Failure = SyncGit.FailureKind.Other });
        var (major, minor) = (parsed.Value.Item1, parsed.Value.Item2);
        return SyncGit.isSupportedVersion(major, minor)
            ? GitOutcome<(int, int)>.Ok((major, minor))
            : GitOutcome<(int, int)>.Fail(result with { Failure = SyncGit.FailureKind.GitTooOld });
    }

    /// <summary>Creates an empty repository with the given initial branch at the clone directory.</summary>
    public async Task<GitOutcome<NoValue>> InitAsync(string branch, CancellationToken cancellationToken = default) =>
        Done(await RunAsync(SyncGit.GitCommand.NewInit(branch, _paths.CloneDirectory), _options.LocalTimeout, cancellationToken).ConfigureAwait(false));

    /// <summary>Points <c>origin</c> at the address, adding it or, when it exists, changing it.</summary>
    public async Task<GitOutcome<NoValue>> SetOriginAsync(string url, CancellationToken cancellationToken = default)
    {
        var added = await RunAsync(SyncGit.GitCommand.NewAddOrigin(url), _options.LocalTimeout, cancellationToken).ConfigureAwait(false);
        if (added.ExitCode == 3) // remote origin already exists
            added = await RunAsync(SyncGit.GitCommand.NewSetOriginUrl(url), _options.LocalTimeout, cancellationToken).ConfigureAwait(false);
        return Done(added);
    }

    /// <summary>
    /// A shallow (depth 1), single-branch clone of the remote at the clone directory. A failed or interrupted clone can
    /// leave a partial directory behind, which makes the next clone to the same place fail: the caller moves a clone
    /// that is not good aside before trying again (the sync cycle of a later milestone does).
    /// </summary>
    public async Task<GitOutcome<NoValue>> CloneAsync(
        string url,
        string branch,
        TimeSpan? timeout = null,
        Action<string>? onStderr = null,
        CancellationToken cancellationToken = default) =>
        Done(await RunNetworkAsync(
            SyncGit.GitCommand.NewClone(url, branch, _paths.CloneDirectory),
            url,
            timeout ?? _options.NetworkTimeout,
            onStderr,
            cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The object id of <c>refs/heads/&lt;branch&gt;</c> on the remote, or null when the remote has no such branch
    /// (an empty remote included). Reads exactly that ref: a branch named <c>foo/refs/heads/main</c> is not <c>main</c>.
    /// </summary>
    public async Task<GitOutcome<string?>> LsRemoteAsync(
        SyncGit.RemoteRef remote,
        string branch,
        TimeSpan? timeout = null,
        Action<string>? onStderr = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunNetworkAsync(
            SyncGit.GitCommand.NewLsRemote(remote, branch),
            remote.IsUrl ? ((SyncGit.RemoteRef.Url)remote).Item : null,
            timeout ?? _options.NetworkTimeout,
            onStderr,
            cancellationToken).ConfigureAwait(false);
        if (result.Failure is not null)
            return GitOutcome<string?>.Fail(result);
        var head = SyncGit.parseLsRemote(branch, Text(result));
        return GitOutcome<string?>.Ok(head is null ? null : head.Value);
    }

    /// <summary>
    /// Every ref name the remote advertises (<c>HEAD</c>, <c>refs/heads/main</c>, tags, pull refs), empty for an empty
    /// repository. The setup commands use it to tell an empty repository from one that already holds something.
    /// </summary>
    public async Task<GitOutcome<IReadOnlyList<string>>> LsRemoteRefsAsync(
        string url,
        TimeSpan? timeout = null,
        Action<string>? onStderr = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunNetworkAsync(
            SyncGit.GitCommand.NewLsRemoteRefs(url),
            url,
            timeout ?? _options.NetworkTimeout,
            onStderr,
            cancellationToken).ConfigureAwait(false);
        if (result.Failure is not null)
            return GitOutcome<IReadOnlyList<string>>.Fail(result);
        IReadOnlyList<string> refs = SyncGit.parseLsRemoteRefs(Text(result)).ToList();
        return GitOutcome<IReadOnlyList<string>>.Ok(refs);
    }

    /// <summary>
    /// Fetches the branch into <c>refs/remotes/origin/&lt;branch&gt;</c>, forced (the remote's history may have been
    /// rewritten by hand). The clone stays shallow and the fetch is incremental.
    /// </summary>
    public async Task<GitOutcome<NoValue>> FetchAsync(
        string branch,
        TimeSpan? timeout = null,
        Action<string>? onStderr = null,
        CancellationToken cancellationToken = default) =>
        Done(await RunNetworkAsync(
            SyncGit.GitCommand.NewFetch(branch),
            null,
            timeout ?? _options.NetworkTimeout,
            onStderr,
            cancellationToken).ConfigureAwait(false));

    /// <summary>Moves the work tree and index to a revision, discarding local changes. Refuses when the clone has no <c>.git</c>.</summary>
    public async Task<GitOutcome<NoValue>> ResetHardAsync(SyncGit.Revision revision, CancellationToken cancellationToken = default)
    {
        return Done(await RunAsync(SyncGit.GitCommand.NewResetHard(revision), _options.LocalTimeout, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Removes untracked files and directories from the clone. Refuses when the clone has no <c>.git</c>.</summary>
    public async Task<GitOutcome<NoValue>> CleanAsync(CancellationToken cancellationToken = default)
    {
        return Done(await RunAsync(SyncGit.GitCommand.Clean, _options.LocalTimeout, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Whether <paramref name="ancestor"/> is an ancestor of <paramref name="descendant"/>; any exit code but 0 and 1 is <see cref="Tri.Unknown"/>.</summary>
    public async Task<GitOutcome<Tri>> IsAncestorAsync(SyncGit.Revision ancestor, SyncGit.Revision descendant, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(SyncGit.GitCommand.NewIsAncestor(ancestor, descendant), _options.LocalTimeout, cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => GitOutcome<Tri>.Ok(Tri.Yes),
            1 => GitOutcome<Tri>.Ok(Tri.No),
            null => GitOutcome<Tri>.Fail(result), // a limit was hit or git did not start: not an answer
            _ => GitOutcome<Tri>.Ok(Tri.Unknown),
        };
    }

    /// <summary>The commit a revision names, or null when it does not exist.</summary>
    public async Task<GitOutcome<string?>> RevParseAsync(SyncGit.Revision revision, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(SyncGit.GitCommand.NewRevParse(revision), _options.LocalTimeout, cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => GitOutcome<string?>.Ok(Text(result).Trim()),
            1 => GitOutcome<string?>.Ok(null),
            _ => GitOutcome<string?>.Fail(result),
        };
    }

    /// <summary>Points the protective ref <c>refs/total-recall/last-synced</c> at a commit the clone holds.</summary>
    public async Task<GitOutcome<NoValue>> UpdateLastSyncedAsync(string sha, CancellationToken cancellationToken = default)
    {
        return Done(await RunAsync(SyncGit.GitCommand.NewUpdateLastSynced(sha), _options.LocalTimeout, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Deletes every <c>*.lock</c> file under the clone's <c>.git</c> (an interrupted git leaves them) and returns the
    /// number removed. It does nothing when the clone is not sound (see <see cref="CloneIsSound"/>), and inside <c>.git</c>
    /// skips links (a symlinked file or directory is neither followed nor deleted), so nothing outside it is touched.
    /// </summary>
    public int RemoveStaleLocks()
    {
        var gitDirectory = Path.Combine(_paths.CloneDirectory, ".git");
        var removed = 0;
        try
        {
            if (!CloneIsSound())
                return 0;
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
            };
            foreach (var file in Directory.EnumerateFiles(gitDirectory, "*.lock", enumeration).ToList())
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return removed;
    }
}
