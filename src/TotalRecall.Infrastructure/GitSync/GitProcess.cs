using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>The paths a git call needs; the executable is whatever <see cref="GitExecutable.TryFind"/> found.</summary>
public sealed record GitContext(string Executable, string CloneDirectory, string NoHooksDirectory, string EmptyFile);

/// <summary>Per-call limits and test seams.</summary>
/// <param name="Timeout">Wall-clock limit; the process tree is killed at it.</param>
/// <param name="MaxStdoutBytes">Standard output above this ends the call as OutputTooLarge.</param>
/// <param name="MaxStderrBytes">Longest standard-error line kept, in characters; a longer one is dropped whole.</param>
/// <param name="EnvironmentOverrides">Set after the environment scrub (a null value removes the variable).</param>
/// <param name="RedactValues">Literals (the caller's own credentials) removed from every standard-error segment; values under 8 characters are ignored.</param>
/// <param name="OnStderrSegment">Receives each redacted standard-error segment as it ends (at CR or LF), progress included.</param>
/// <param name="WorkingDirectory">The directory the process starts in; none means the caller's own.</param>
public sealed record GitRunOptions(
    TimeSpan Timeout,
    long MaxStdoutBytes = 64L << 20,
    int MaxStderrBytes = 64 << 10,
    IReadOnlyDictionary<string, string?>? EnvironmentOverrides = null,
    IReadOnlyList<string>? RedactValues = null,
    Action<string>? OnStderrSegment = null,
    string? WorkingDirectory = null);

/// <summary>What a finished call produced. <c>Failure</c> is null exactly when the exit code is 0 and no limit was hit.</summary>
public sealed record GitResult(
    int? ExitCode,
    byte[] Stdout,
    string StderrText,
    SyncGit.FailureKind? Failure,
    TimeSpan Elapsed);

/// <summary>The seam every git call goes through.</summary>
public interface IGitProcess
{
    Task<GitResult> RunAsync(
        SyncGit.GitCommand command,
        GitContext context,
        GitRunOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Finds the git executable on the PATH.</summary>
public static class GitExecutable
{
    public static string? TryFind(string? pathVariable = null, bool? windows = null) =>
        Find("git", pathVariable, windows);

    /// <summary>The same search for any program (a test helper: <c>flock</c>).</summary>
    internal static string? TryFindProgram(string program, string? pathVariable = null, bool? windows = null) =>
        Find(program, pathVariable, windows);

    private static string? Find(string program, string? pathVariable, bool? windows)
    {
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        var isWindows = windows ?? OperatingSystem.IsWindows();
        // no `.cmd`: a batch file would be re-parsed by cmd.exe, which reads `& | ^ %` in an argument as syntax
        var names = isWindows ? new[] { program + ".exe" } : new[] { program };
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry is skipped
                }
            }
        }
        return null;
    }
}

/// <summary>
/// Turns the bytes of a standard-error stream into redacted segments. A segment ends at CR, LF or the end of the
/// stream, so a credential or a multi-byte character split across two reads is never redacted in pieces. CR LF is one
/// terminator even when the two arrive in different reads; a segment ended by a lone CR is a progress rewrite, passed
/// to the callback but not kept. A line longer than the limit is dropped whole and replaced once by
/// <c>[line too long]</c>. Empty segments are never emitted.
/// </summary>
internal sealed class StderrAccumulator
{
    internal const string TooLong = "[line too long]";
    private const int MinSecretLength = 8;

    private readonly object _gate = new();
    private readonly int _maxLine;
    private readonly FSharpList<string> _secrets;
    private readonly Action<string>? _onSegment;
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _kept = new();
    private readonly List<string> _outbox = [];
    private string? _pendingCr;
    private bool _discarding;
    private bool _closed;

    public StderrAccumulator(int maxLine, IReadOnlyList<string>? secrets, Action<string>? onSegment)
    {
        _maxLine = Math.Max(1, maxLine);
        _onSegment = onSegment;
        _secrets = ListModule.OfSeq((secrets ?? []).Where(s => s is { Length: >= MinSecretLength }));
    }

    public void Append(ReadOnlySpan<char> chars)
    {
        lock (_gate)
        {
            if (_closed)
                return;
            foreach (var c in chars)
                Feed(c);
        }
        Deliver();
    }

    /// <summary>Flushes what is left at the end of the stream and ignores anything that arrives later.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            if (_discarding)
                EmitTooLong();
            else if (_line.Length > 0)
                EndLine(byCarriageReturn: false);
            if (_pendingCr is not null)
                Keep(_pendingCr);
            _pendingCr = null;
            _closed = true;
        }
        Deliver();
    }

    /// <summary>Hands the queued segments to the observer outside the lock, so a slow observer cannot block the call.</summary>
    private void Deliver()
    {
        string[] batch;
        lock (_gate)
        {
            if (_outbox.Count == 0)
                return;
            batch = [.. _outbox];
            _outbox.Clear();
        }
        foreach (var segment in batch)
        {
            try
            {
                _onSegment?.Invoke(segment);
            }
            catch (Exception)
            {
                // a failing observer must not stop the call
            }
        }
    }

    /// <summary>The last <see cref="SyncGit.maxStderrKeptChars"/> characters of the kept, redacted text.</summary>
    public string Text()
    {
        lock (_gate)
        {
            var text = _kept.ToString().TrimEnd('\n');
            return Tail(text);
        }
    }

    private static string Tail(string text)
    {
        var max = SyncGit.maxStderrKeptChars;
        var start = Math.Max(0, text.Length - max);
        // an earlier trim may have cut a pair in half, so the first character is checked even when the text fits
        if (start < text.Length && char.IsLowSurrogate(text[start]))
            start++;
        return text[start..];
    }

    private void Feed(char c)
    {
        if (_pendingCr is not null)
        {
            var pending = _pendingCr;
            _pendingCr = null;
            if (c == '\n')
            {
                Keep(pending); // CR LF: one terminator, the line is an ordinary one
                return;
            }
            // anything else: the CR ended a progress rewrite, which is not kept
        }

        if (c == '\r' || c == '\n')
        {
            if (_discarding)
                EmitTooLong();
            else
                EndLine(byCarriageReturn: c == '\r');
            return;
        }

        if (_discarding)
            return;
        _line.Append(c);
        if (_line.Length > _maxLine)
        {
            _line.Clear();
            _discarding = true;
        }
    }

    private void EmitTooLong()
    {
        _discarding = false;
        _line.Clear();
        Emit(TooLong);
        Keep(TooLong);
    }

    private void EndLine(bool byCarriageReturn)
    {
        var segment = _line.ToString();
        _line.Clear();
        if (segment.Length == 0)
            return;
        var redacted = SyncGit.redact(_secrets, segment);
        Emit(redacted);
        if (byCarriageReturn)
            _pendingCr = redacted;
        else
            Keep(redacted);
    }

    private void Keep(string redacted)
    {
        _kept.Append(redacted).Append('\n');
        // bounded: the final text is the tail, so older text is not needed
        var limit = SyncGit.maxStderrKeptChars * 2;
        if (_kept.Length > limit)
            _kept.Remove(0, _kept.Length - SyncGit.maxStderrKeptChars - 1);
    }

    private void Emit(string segment)
    {
        if (_onSegment is not null)
            _outbox.Add(segment);
    }
}

/// <summary>
/// The one place a git process starts. No shell: the argument list comes from <see cref="SyncGit.build"/>. The
/// environment is the process's own, scrubbed. The call is bounded by a wall-clock limit and a cancellation token,
/// both of which kill the whole process tree; output is bounded; standard error is redacted as it is read.
/// </summary>
public sealed class GitProcess : IGitProcess
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    public async Task<GitResult> RunAsync(
        SyncGit.GitCommand command,
        GitContext context,
        GitRunOptions options,
        CancellationToken cancellationToken)
    {
        var buildContext = new SyncGit.BuildContext(
            context.CloneDirectory, context.NoHooksDirectory, context.EmptyFile, OperatingSystem.IsWindows());
        var built = SyncGit.build(buildContext, command);
        if (built.IsError)
            throw new ArgumentException(built.ErrorValue, nameof(command));

        // created before the process starts, so an invalid timeout throws while nothing is running
        using var timeout = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var drain = new CancellationTokenSource();

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = StartInfo(context, options, built.ResultValue) };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException or InvalidOperationException or DirectoryNotFoundException)
        {
            return Finish(null, [], "", spawnFailed: true, timedOut: false, cancelled: false, tooLarge: false, stopwatch);
        }

        var accumulator = new StderrAccumulator(options.MaxStderrBytes, options.RedactValues, options.OnStderrSegment);
        var stdout = new BoundedBuffer(options.MaxStdoutBytes);

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // the child has already closed its input
        }

        var stdoutTask = Task.Run(() => PumpStdout(process, stdout, drain.Token));
        var stderrTask = Task.Run(() => PumpStderr(process, accumulator, drain.Token));

        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                cancelled = true;
            else
                timedOut = true;
        }

        if (!process.HasExited || timedOut || cancelled)
            KillTree(process);

        await WaitForExitQuietly(process).ConfigureAwait(false);

        // Pipes held open by a detached grandchild must not hold the call: drain for a short grace, then stop waiting.
        // The cancel below does not interrupt a read already blocked on a Linux pipe, so such a read (two pool threads)
        // lives until the grandchild exits; the accumulator and the buffer ignore whatever it delivers after that.
        var pumps = Task.WhenAll(stdoutTask, stderrTask);
        if (await Task.WhenAny(pumps, Task.Delay(DrainGrace)).ConfigureAwait(false) != pumps)
            drain.Cancel();
        accumulator.Complete();
        var bytes = stdout.Snapshot(out var tooLarge);

        int? exit = null;
        if (!timedOut && !cancelled && !tooLarge)
        {
            try
            {
                exit = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exit = null;
            }
        }
        return Finish(exit, tooLarge ? [] : bytes, accumulator.Text(), false, timedOut, cancelled, tooLarge, stopwatch);
    }

    private static GitResult Finish(
        int? exit, byte[] stdout, string stderr, bool spawnFailed, bool timedOut, bool cancelled, bool tooLarge, Stopwatch watch)
    {
        SyncGit.FailureKind? failure = null;
        if (spawnFailed || timedOut || cancelled || tooLarge || exit != 0)
        {
            failure = SyncGit.classifyFailure(
                new SyncGit.FailureInput(
                    exit is { } code ? FSharpOption<int>.Some(code) : FSharpOption<int>.None,
                    timedOut,
                    cancelled,
                    spawnFailed,
                    tooLarge,
                    stderr));
        }
        return new GitResult(exit, stdout, stderr, failure, watch.Elapsed);
    }

    private static ProcessStartInfo StartInfo(GitContext context, GitRunOptions options, FSharpList<string> argv)
    {
        var info = new ProcessStartInfo
        {
            FileName = context.Executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = null,
            StandardErrorEncoding = null,
        };
        if (!string.IsNullOrEmpty(options.WorkingDirectory))
            info.WorkingDirectory = options.WorkingDirectory;
        foreach (var arg in argv)
            info.ArgumentList.Add(arg);

        var current = new List<Tuple<string, string>>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            current.Add(Tuple.Create((string)entry.Key, entry.Value as string ?? ""));
        info.Environment.Clear();
        foreach (var pair in SyncGit.scrubEnvironment(ListModule.OfSeq(current)))
            info.Environment[pair.Item1] = pair.Item2;
        foreach (var removed in SyncGit.removedEnvironmentNames)
            info.Environment.Remove(removed);
        foreach (var pair in SyncGit.fixedEnvironment)
            info.Environment[pair.Item1] = pair.Item2;
        if (options.EnvironmentOverrides is not null)
        {
            foreach (var (name, value) in options.EnvironmentOverrides)
            {
                if (value is null)
                    info.Environment.Remove(name);
                else
                    info.Environment[name] = value;
            }
        }
        return info;
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // it already exited, or a descendant could not be killed
        }
    }

    private static async Task WaitForExitQuietly(Process process)
    {
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
            // nothing more to do: the tree has been killed
        }
    }

    private static async Task PumpStdout(Process process, BoundedBuffer buffer, CancellationToken drain)
    {
        var chunk = new byte[81920];
        try
        {
            var stream = process.StandardOutput.BaseStream;
            while (true)
            {
                var n = await stream.ReadAsync(chunk, drain).ConfigureAwait(false);
                if (n == 0)
                    return;
                if (!buffer.Add(chunk, n))
                {
                    KillTree(process);
                    return;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // abandoned or closed
        }
    }

    private static async Task PumpStderr(Process process, StderrAccumulator accumulator, CancellationToken drain)
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192 + 8];
        try
        {
            var stream = process.StandardError.BaseStream;
            while (true)
            {
                var n = await stream.ReadAsync(bytes, drain).ConfigureAwait(false);
                if (n == 0)
                    break;
                var count = decoder.GetChars(bytes, 0, n, chars, 0, flush: false);
                accumulator.Append(chars.AsSpan(0, count));
            }
            var tail = decoder.GetChars([], 0, 0, chars, 0, flush: true);
            if (tail > 0)
                accumulator.Append(chars.AsSpan(0, tail));
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // abandoned or closed
        }
    }

    /// <summary>Standard output up to a byte cap; once it is exceeded nothing more is kept.</summary>
    private sealed class BoundedBuffer(long limit)
    {
        private readonly object _gate = new();
        private readonly MemoryStream _data = new();
        private bool _tooLarge;
        private bool _closed;

        public bool Add(byte[] chunk, int count)
        {
            lock (_gate)
            {
                if (_closed)
                    return true;
                if (_data.Length + count > limit)
                {
                    _tooLarge = true;
                    return false;
                }
                _data.Write(chunk, 0, count);
                return true;
            }
        }

        public byte[] Snapshot(out bool tooLarge)
        {
            lock (_gate)
            {
                _closed = true;
                tooLarge = _tooLarge;
                return _data.ToArray();
            }
        }
    }
}
