using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>Spec section 2, step 1: the ancestors of the engine, read through an injected parent function.</summary>
public sealed class ProcessAncestryTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
    }

    private static Func<int, int?> Map(params (int Pid, int? Parent)[] edges)
    {
        var d = edges.ToDictionary(e => e.Pid, e => e.Parent);
        return pid => d.TryGetValue(pid, out var p) ? p : null;
    }

    [Fact]
    public void Chain_StopsAtPid1_AtRepeat_AtNull_AndAt12Hops()
    {
        // pid 1 ends the chain and is not listed
        Assert.Equal(new[] { 20, 10 }, ProcessAncestry.Chain(30, Map((30, 20), (20, 10), (10, 1), (1, 0))));
        // a parent of 0 (or lower) ends it too
        Assert.Equal(new[] { 20 }, ProcessAncestry.Chain(30, Map((30, 20), (20, 0))));
        // a repeat ends it (a cycle back to a listed pid, and back to self)
        Assert.Equal(new[] { 20, 10 }, ProcessAncestry.Chain(30, Map((30, 20), (20, 10), (10, 20))));
        Assert.Equal(new[] { 20 }, ProcessAncestry.Chain(30, Map((30, 20), (20, 30))));
        // a failed read (null) ends it, whether at the first hop or later
        Assert.Empty(ProcessAncestry.Chain(30, Map()));
        Assert.Equal(new[] { 20 }, ProcessAncestry.Chain(30, Map((30, 20))));
        // at most 12 ancestors, nearest first, the engine itself excluded
        var chain = ProcessAncestry.Chain(1000, pid => pid - 1);
        Assert.Equal(Enumerable.Range(988, 12).Reverse().ToArray(), chain);
    }

    [Fact]
    public void Chain_StopsAtTheTotalDeadline()
    {
        var clock = new ManualClock();
        var calls = 0;
        int? Slow(int pid)
        {
            calls++;
            clock.Advance(TimeSpan.FromSeconds(2));
            return pid - 1;
        }

        // hop 1 starts at 0 s, hop 2 at 2 s, hop 3 would start at 4 s >= 3 s: not made
        var chain = ProcessAncestry.Chain(100, Slow, TimeSpan.FromSeconds(3), clock);
        Assert.Equal(new[] { 99, 98 }, chain);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Chain_DefaultLimitIsThreeSeconds()
    {
        var clock = new ManualClock();
        int? Slow(int pid)
        {
            clock.Advance(TimeSpan.FromSeconds(1.5));
            return pid - 1;
        }
        // 0 s, 1.5 s: made; 3.0 s: spent (checked between hops)
        Assert.Equal(new[] { 99, 98 }, ProcessAncestry.Chain(100, Slow, time: clock));
    }

    [Fact]
    public void ProcParent_RealChildAndGrandchild_OnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;   // /proc only

        // a node child that spawns a node grandchild which sleeps: neither is exec-collapsed
        const string grand = "setTimeout(()=>{},60000)";
        var child = "const {spawn}=require('child_process');" +
                    $"const g=spawn(process.execPath,['-e','{grand}'],{{stdio:'ignore'}});" +
                    "console.log(g.pid);setTimeout(()=>{},60000)";
        var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(child);
        using var proc = Process.Start(psi)!;
        int grandPid = 0;
        try
        {
            var line = proc.StandardOutput.ReadLine();
            Assert.True(int.TryParse(line, out grandPid), "the child prints its child's pid: " + line);

            Assert.Equal(proc.Id, ProcessAncestry.ProcParent(grandPid));
            Assert.Equal(Environment.ProcessId, ProcessAncestry.ProcParent(proc.Id));
            Assert.Equal(new[] { proc.Id, Environment.ProcessId },
                ProcessAncestry.Chain(grandPid, ProcessAncestry.ProcParent).Take(2));
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (grandPid > 0) { try { Process.GetProcessById(grandPid).Kill(); } catch { } }
        }
    }

    [Fact]
    public void ProcParent_NoSuchPid_IsNull()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Null(ProcessAncestry.ProcParent(int.MaxValue));
    }

    [Fact]
    public void ProcParent_CommandNameWithSpaceAndParenthesis()
    {
        Assert.Equal(77, ProcessAncestry.ParseStatParent("123 (a b) c) S 77 123 123 0 -1 4194560"));
        Assert.Equal(1, ProcessAncestry.ParseStatParent("1 (systemd) S 1 1 1 0"));
        Assert.Null(ProcessAncestry.ParseStatParent("123 (a b"));
        Assert.Null(ProcessAncestry.ParseStatParent("123 (a) S"));
        Assert.Null(ProcessAncestry.ParseStatParent("123 (a) S xyz 1"));
        Assert.Null(ProcessAncestry.ParseStatParent(""));
    }

    [Fact]
    public void PsParent_UsesRunner_Timeout_Failure()
    {
        var seen = new List<(int Pid, TimeSpan Timeout)>();
        var ok = ProcessAncestry.PsParent((pid, timeout) => { seen.Add((pid, timeout)); return " 4242\n"; });
        Assert.Equal(4242, ok(99));
        Assert.Equal((99, TimeSpan.FromSeconds(1)), Assert.Single(seen));   // 1 s per hop

        Assert.Null(ProcessAncestry.PsParent((_, _) => null)(99));                          // timeout / failure
        Assert.Null(ProcessAncestry.PsParent((_, _) => throw new IOException("no ps"))(99)); // cannot start
        Assert.Null(ProcessAncestry.PsParent((_, _) => "")(99));
        Assert.Null(ProcessAncestry.PsParent((_, _) => "not a number\n")(99));
        Assert.Null(ProcessAncestry.PsParent((_, _) => "-5\n")(99));
        Assert.Null(ProcessAncestry.PsParent((_, _) => "0\n")(99));
    }

    [Fact]
    public void PsParent_DefaultRunner_ReadsTheRealParent()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        if (!File.Exists("/bin/ps") && !File.Exists("/usr/bin/ps")) return;
        // `ps -o ppid= -p <pid>` is the macOS form; procps on Linux accepts it as well
        var viaPs = ProcessAncestry.PsParent()(Environment.ProcessId);
        // A ps that cannot answer (a restricted container, a minimal ps without -o) gives null, which the finder
        // treats as the end of the chain: that is allowed. An answer must be the real parent.
        if (viaPs is null) return;
        Assert.True(viaPs > 0);
        if (OperatingSystem.IsLinux()) Assert.Equal(ProcessAncestry.ProcParent(Environment.ProcessId), viaPs);
    }

    [Fact]
    public void Platform_ReturnsAFunction()
    {
        var f = ProcessAncestry.Platform();
        Assert.NotNull(f);
        if (OperatingSystem.IsLinux()) Assert.NotNull(f(Environment.ProcessId));
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Assert.Null(f(Environment.ProcessId));
    }

    [Fact]
    public void CachedAncestry_KeepsNonEmpty_RetriesEmpty()
    {
        var calls = 0;
        var results = new Queue<IReadOnlyList<int>>(new IReadOnlyList<int>[]
        {
            Array.Empty<int>(), Array.Empty<int>(), new[] { 5, 4 }, new[] { 9 },
        });
        var cached = new CachedAncestry(() => { calls++; return results.Dequeue(); });

        Assert.Empty(cached.Get());
        Assert.Empty(cached.Get());          // an empty result is not kept: asked again
        Assert.Equal(2, calls);
        Assert.Equal(new[] { 5, 4 }, cached.Get());
        Assert.Equal(new[] { 5, 4 }, cached.Get());   // kept for the life of the process
        Assert.Equal(3, calls);
    }
}
