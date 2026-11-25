using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// The ancestor process ids of this process, nearest first (spec section 2, step 1): the host process
/// of a plugin install is the nearest ancestor that has a live-session marker. Linux reads
/// <c>/proc/&lt;pid&gt;/stat</c>, macOS runs <c>ps</c>, any other platform has no cheap parent lookup.
/// </summary>
public static class ProcessAncestry
{
    private const int MaxHops = 12;
    private static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PsPerHop = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The ancestors of <paramref name="selfPid"/>, nearest first and <paramref name="selfPid"/> excluded, at most 12.
    /// Stops at pid 1 or lower (pid 1 is not listed), at a repeated pid, when <paramref name="parentOf"/>
    /// returns null, and when <paramref name="limit"/> (default 3 s, measured on <paramref name="time"/>) is
    /// spent. The list of <see cref="Walk"/>, without how it ended.
    /// </summary>
    public static IReadOnlyList<int> Chain(int selfPid, Func<int, int?> parentOf, TimeSpan? limit = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(parentOf);
        return Walk(selfPid, (pid, _) => parentOf(pid), limit, time).Pids;
    }

    /// <summary>
    /// The ancestors of <paramref name="selfPid"/> as <see cref="Chain"/> lists them, and whether the walk ended
    /// normally: at pid 1 or lower, at a repeated pid or at the hop limit. A failed read (<paramref name="parentOf"/>
    /// returned null or threw) or the spent <paramref name="limit"/> (default 3 s, measured on <paramref name="time"/>)
    /// is not a normal end. <paramref name="parentOf"/> gets the time left of the limit, so a lookup with its own wait
    /// (<see cref="PsParentTimed"/>) never runs past it; no hop starts once nothing is left.
    /// </summary>
    public static AncestryWalk Walk(
        int selfPid, Func<int, TimeSpan, int?> parentOf, TimeSpan? limit = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(parentOf);
        var clock = time ?? TimeProvider.System;
        var budget = limit ?? DefaultLimit;
        var start = clock.GetTimestamp();

        var chain = new List<int>();
        var seen = new HashSet<int> { selfPid };
        var current = selfPid;
        while (chain.Count < MaxHops)
        {
            var remaining = budget - clock.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero) return new AncestryWalk(chain, EndedNormally: false);
            int? parent;
            try { parent = parentOf(current, remaining); }
            catch { return new AncestryWalk(chain, EndedNormally: false); }
            if (parent is not { } p) return new AncestryWalk(chain, EndedNormally: false);
            if (p <= 1 || !seen.Add(p)) return new AncestryWalk(chain, EndedNormally: true);
            chain.Add(p);
            current = p;
        }
        return new AncestryWalk(chain, EndedNormally: true);
    }

    /// <summary>Linux: field 4 of <c>/proc/&lt;pid&gt;/stat</c>, or null when it cannot be read.</summary>
    public static int? ProcParent(int pid)
    {
        try
        {
            return ParseStatParent(File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The parent pid of a <c>stat</c> line: the fields after the <b>last</b> <c>)</c> (the command name, field 2,
    /// may hold spaces and parentheses) are the state and then the parent pid.
    /// </summary>
    internal static int? ParseStatParent(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0) return null;
        var fields = stat.Substring(close + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length < 2) return null;
        return int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid) ? ppid : null;
    }

    /// <summary>
    /// macOS (and any system with <c>ps</c>): <c>ps -o ppid= -p &lt;pid&gt;</c>, 1 second per hop.
    /// <paramref name="runPs"/> returns the standard output, or null when the command failed or timed out; it
    /// may throw when the command cannot start. Both give null here.
    /// </summary>
    public static Func<int, int?> PsParent(Func<int, TimeSpan, string?>? runPs = null)
    {
        var lookup = PsParentTimed(runPs);
        return pid => lookup(pid, PsPerHop);
    }

    /// <summary>
    /// <see cref="PsParent"/> for <see cref="Walk"/>: each <c>ps</c> gets 1 second or the time left of the walk,
    /// whichever is shorter.
    /// </summary>
    public static Func<int, TimeSpan, int?> PsParentTimed(Func<int, TimeSpan, string?>? runPs = null)
    {
        var run = runPs ?? RunPs;
        return (pid, remaining) =>
        {
            try
            {
                var output = run(pid, remaining < PsPerHop ? remaining : PsPerHop);
                if (output is null) return null;
                return int.TryParse(output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var ppid) && ppid > 0 ? ppid : null;
            }
            catch
            {
                return null;
            }
        };
    }

    /// <summary>The platform's parent lookup: <c>/proc</c> on Linux, <c>ps</c> on macOS, nothing elsewhere.</summary>
    public static Func<int, int?> Platform()
    {
        if (OperatingSystem.IsLinux()) return ProcParent;
        if (OperatingSystem.IsMacOS()) return PsParent();
        return _ => null;
    }

    /// <summary><see cref="Platform"/> for <see cref="Walk"/>: on macOS each <c>ps</c> is bounded by the time left.</summary>
    public static Func<int, TimeSpan, int?> PlatformTimed()
    {
        if (OperatingSystem.IsLinux()) return static (pid, _) => ProcParent(pid);
        if (OperatingSystem.IsMacOS()) return PsParentTimed();
        return static (_, _) => null;
    }

    private static string? RunPs(int pid, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("ps")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ppid=");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));

        using var process = Process.Start(psi);
        if (process is null) return null;
        try
        {
            process.StandardInput.Close();
            _ = process.StandardError.ReadToEndAsync();   // drained so a full pipe cannot block it
            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(); } catch { /* already gone */ }
                return null;
            }
            if (process.ExitCode != 0) return null;
            if (!stdout.Wait(timeout)) return null;
            return stdout.Result;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(); } catch { /* already gone */ }
            return null;
        }
    }
}

/// <summary>
/// The ancestors a <see cref="ProcessAncestry.Walk"/> found, nearest first, and whether the walk ended normally (pid 1
/// or lower, a repeat, the hop limit) rather than on a failed read or the time limit.
/// </summary>
public sealed record AncestryWalk(IReadOnlyList<int> Pids, bool EndedNormally);

/// <summary>
/// Keeps the ancestor chain for the life of the process (the ancestors of a process do not change). Only a non-empty
/// chain whose walk ended normally is kept; a failed, cut-short or empty one is returned for that call and not kept,
/// so the next call walks again.
/// </summary>
public sealed class CachedAncestry
{
    private readonly Func<AncestryWalk> _walk;
    private readonly object _gate = new();
    private IReadOnlyList<int>? _kept;

    /// <param name="walk">The walk; its result is kept only when it ended normally and is not empty.</param>
    public CachedAncestry(Func<AncestryWalk> walk)
    {
        _walk = walk ?? throw new ArgumentNullException(nameof(walk));
    }

    /// <param name="chain">A chain whose ending is not known (a test seam): a non-empty result is kept.</param>
    public CachedAncestry(Func<IReadOnlyList<int>> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        _walk = () =>
        {
            var pids = chain();
            return new AncestryWalk(pids, EndedNormally: pids.Count > 0);
        };
    }

    public IReadOnlyList<int> Get()
    {
        lock (_gate)
        {
            if (_kept is not null) return _kept;
            var result = _walk();
            if (result.EndedNormally && result.Pids.Count > 0) _kept = result.Pids;
            return result.Pids;
        }
    }
}
