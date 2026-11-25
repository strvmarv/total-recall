using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// Spec section 2, steps 2 to 4: which marker is trusted. The chain, the process start and the log are
/// injected; the marker files, the transcript root and the session files are real temporary files.
/// (Console swaps need the shared collection.)
/// </summary>
[Collection("process-environment")]
public sealed class LiveSessionFinderTests : IDisposable
{
    private const string Host = "claude-code";
    private const string Id = "aaaaaaaa-1111-4222-8333-444444444444";
    private const string OuterId = "bbbbbbbb-1111-4222-8333-444444444444";

    private readonly string _tmp;
    private readonly string _data;
    private readonly string _root;
    private readonly List<string> _log = new();

    public LiveSessionFinderTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "tr-live-find-" + Guid.NewGuid().ToString("N"));
        _data = Path.Combine(_tmp, "data");
        _root = Path.Combine(_tmp, "projects");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeAdapter(string host, string root) : ITranscriptAdapter
    {
        public string Host { get; } = host;
        public string Root { get; } = root;
        public bool Detect() => throw new NotSupportedException();
        public SessionEnumeration EnumerateSessions() => throw new NotSupportedException();
        public SessionHead ReadHead(SessionSource source) => throw new NotSupportedException();
        public bool IsLineStart(SessionSource source, long offset) => throw new NotSupportedException();
        public IEnumerable<TranscriptRecord> ReadRecords(SessionSource source, long startOffset, ReadStats stats, System.Threading.CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private ITranscriptAdapter[] Adapters => new ITranscriptAdapter[] { new FakeAdapter(Host, _root) };

    private LiveSessionFinder Finder(IReadOnlyList<int> chain, Func<int, DateTimeOffset?>? start = null, Action<string>? log = null) =>
        new(_data, new CachedAncestry(() => chain), start ?? (_ => null), log ?? _log.Add);

    /// <summary>Creates <c>&lt;root&gt;/&lt;project&gt;/&lt;id&gt;.jsonl</c> and returns its path.</summary>
    private string Session(string project, string id, string? root = null)
    {
        var dir = Path.Combine(root ?? _root, project);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, id + ".jsonl");
        File.WriteAllText(file, "{}\n");
        return file;
    }

    private static JsonObject Marker(int pid, string id, string path, string cwd = "/work/proj", string host = Host, DateTimeOffset? at = null) => new()
    {
        ["v"] = 1,
        ["host"] = host,
        ["host_pid"] = pid,
        ["session_id"] = id,
        ["transcript_path"] = path,
        ["cwd"] = cwd,
        ["at"] = (at ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
    };

    private string MarkerPath(int pid, string host = Host) => Path.Combine(_data, "live", $"{host}-{pid}.json");

    private string Put(int pid, JsonObject marker, string host = Host) => PutRaw(pid, marker.ToJsonString(), host);

    private string PutRaw(int pid, string content, string host = Host)
    {
        var path = MarkerPath(pid, host);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // --- the order ---------------------------------------------------------------------------------------

    [Fact]
    public void Find_NearestAncestorFirst()
    {
        var inner = Session("-p", Id);
        var outer = Session("-p", OuterId);
        Put(100, Marker(100, Id, inner));
        Put(200, Marker(200, OuterId, outer));

        var found = Finder(new[] { 100, 200 }).Find(Adapters);

        Assert.NotNull(found);
        Assert.Equal(CurrentKind.Marker, found!.Kind);
        Assert.Equal("marker", found.Wire);
        Assert.Equal(Host, found.Host);
        Assert.Equal(Id, found.HostSessionId);
        Assert.Equal(inner, found.TranscriptPath);
        Assert.Equal("/work/proj", found.Cwd);
        Assert.Equal(Host + ":" + Id, found.Ref);
        Assert.Empty(_log);
    }

    [Fact]
    public void Find_NoAncestors_NoMarkerFile_NoAdapters_IsNull()
    {
        Assert.Null(Finder(Array.Empty<int>()).Find(Adapters));
        Assert.Null(Finder(new[] { 100 }).Find(Adapters));   // no live/ directory at all
        Put(100, Marker(100, Id, Session("-p", Id)));
        Assert.Null(Finder(new[] { 100 }).Find(Array.Empty<ITranscriptAdapter>()));
        Assert.Empty(_log);                                  // a missing file is not a refusal
    }

    [Fact]
    public void Find_TwoSessionsOneDirectory_EachResolvesItsOwn()
    {
        // engine -> shim -> hostA, and engine -> shim -> hostB; both hosts run in one directory, markers for both
        var a = Session("-same", Id);
        var b = Session("-same", OuterId);
        Put(1001, Marker(1001, Id, a));
        Put(1002, Marker(1002, OuterId, b));

        var viaA = Finder(new[] { 500, 1001, 900 }).Find(Adapters);   // 500 = the shim
        var viaB = Finder(new[] { 501, 1002, 900 }).Find(Adapters);

        Assert.Equal(Id, viaA!.HostSessionId);
        Assert.Equal(OuterId, viaB!.HostSessionId);
    }

    [Fact]
    public void Find_MarkerForNonAncestor_IsNeverUsed()
    {
        Put(777, Marker(777, Id, Session("-p", Id)));   // a valid marker of some other host process
        Assert.Null(Finder(new[] { 100, 200 }).Find(Adapters));
    }

    // --- refusals ----------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Refusals() => new[]
    {
        "dotdot", "dot", "symlink-dir", "symlink-file", "wrong-name", "deep", "id-mismatch", "pid-mismatch",
        "oversize", "malformed", "not-object", "wrong-v", "wrong-host", "other-root", "relative-path", "relative-cwd",
        "stale-at", "bad-id", "missing-file", "path-not-string", "cwd-missing", "first-component-is-a-file",
    }.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Find_RefusalsSkipAndContinue(string kind)
    {
        var isLink = kind is "symlink-dir" or "symlink-file";
        if (isLink && OperatingSystem.IsWindows()) return;   // creating links needs a privilege there

        const int near = 100, far = 200;
        var farFile = Session("-far", OuterId);
        Put(far, Marker(far, OuterId, farFile));

        var good = Session("-p", Id);
        var elsewhere = Path.Combine(_tmp, "elsewhere");
        string name = MarkerPath(near);
        DateTimeOffset? nearStart = null;

        switch (kind)
        {
            case "dotdot":
                Put(near, Marker(near, Id, Path.Combine(_root, "-p", "..", "-p", Id + ".jsonl")));
                break;
            case "dot":
                Put(near, Marker(near, Id, Path.Combine(_root, ".", "-p", Id + ".jsonl")));
                break;
            case "symlink-dir":
                {
                    Directory.CreateDirectory(Path.Combine(elsewhere, "real"));
                    File.WriteAllText(Path.Combine(elsewhere, "real", Id + ".jsonl"), "{}\n");
                    Directory.CreateSymbolicLink(Path.Combine(_root, "-link"), Path.Combine(elsewhere, "real"));
                    Put(near, Marker(near, Id, Path.Combine(_root, "-link", Id + ".jsonl")));
                    break;
                }
            case "symlink-file":
                {
                    Directory.CreateDirectory(elsewhere);
                    var real = Path.Combine(elsewhere, "real.jsonl");
                    File.WriteAllText(real, "{}\n");
                    Directory.CreateDirectory(Path.Combine(_root, "-l"));
                    File.CreateSymbolicLink(Path.Combine(_root, "-l", Id + ".jsonl"), real);
                    Put(near, Marker(near, Id, Path.Combine(_root, "-l", Id + ".jsonl")));
                    break;
                }
            case "wrong-name":
                Put(near, Marker(near, Id, Session("-p", "other")));
                break;
            case "deep":
                {
                    var dir = Path.Combine(_root, "-p", "subagents");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, Id + ".jsonl"), "{}\n");
                    Put(near, Marker(near, Id, Path.Combine(dir, Id + ".jsonl")));
                    break;
                }
            case "id-mismatch":
                Put(near, Marker(near, "cccccccc-1111-4222-8333-444444444444", good));
                break;
            case "pid-mismatch":
                Put(near, Marker(near + 1, Id, good));
                break;
            case "oversize":
                {
                    var m = Marker(near, Id, good);
                    m["pad"] = "";
                    var padLen = 16 * 1024 + 1 - Encoding.UTF8.GetByteCount(m.ToJsonString());
                    m["pad"] = new string('x', padLen);
                    Assert.Equal(16 * 1024 + 1, Encoding.UTF8.GetByteCount(m.ToJsonString()));
                    Put(near, m);
                    break;
                }
            case "malformed":
                PutRaw(near, "{\"v\":1, nope");
                break;
            case "not-object":
                PutRaw(near, "[1,2,3]");
                break;
            case "wrong-v":
                {
                    var m = Marker(near, Id, good);
                    m["v"] = 2;
                    Put(near, m);
                    break;
                }
            case "wrong-host":
                Put(near, Marker(near, Id, good, host: "cursor"));
                break;
            case "other-root":
                {
                    var f = Session("-p", Id, Path.Combine(_tmp, "other-root"));
                    Put(near, Marker(near, Id, f));
                    break;
                }
            case "relative-path":
                Put(near, Marker(near, Id, Path.Combine("projects", "-p", Id + ".jsonl")));
                break;
            case "relative-cwd":
                Put(near, Marker(near, Id, good, cwd: "work/proj"));
                break;
            case "stale-at":
                Put(near, Marker(near, Id, good, at: DateTimeOffset.UtcNow.AddSeconds(-60)));
                nearStart = DateTimeOffset.UtcNow;
                break;
            case "bad-id":
                Put(near, Marker(near, "a/b", good));
                break;
            case "missing-file":
                Put(near, Marker(near, Id, Path.Combine(_root, "-p", Id + ".jsonl")));
                File.Delete(good);
                break;
            case "path-not-string":
                {
                    var m = Marker(near, Id, good);
                    m["transcript_path"] = 5;
                    Put(near, m);
                    break;
                }
            case "cwd-missing":
                {
                    var m = Marker(near, Id, good);
                    m.Remove("cwd");
                    Put(near, m);
                    break;
                }
            case "first-component-is-a-file":
                // a path whose first component below the root is a FILE, not a directory
                File.WriteAllText(Path.Combine(_root, "-file"), "x");
                Put(near, Marker(near, Id, Path.Combine(_root, "-file", Id + ".jsonl")));
                break;
            default:
                throw new InvalidOperationException(kind);
        }

        var finder = Finder(new[] { near, far }, pid => pid == near ? nearStart : null);

        var found = finder.Find(Adapters);

        Assert.NotNull(found);
        Assert.Equal(OuterId, found!.HostSessionId);   // the refused one is skipped, the walk goes on
        Assert.Equal(farFile, found.TranscriptPath);
        var line = Assert.Single(_log);
        Assert.StartsWith("[total-recall] transcripts live marker: ignoring ", line);
        Assert.Contains(name, line);

        // once per file per process: a second look logs nothing more
        Assert.NotNull(finder.Find(Adapters));
        Assert.Single(_log);
        // and the log never carries the file's content
        Assert.DoesNotContain("elsewhere", line);
        Assert.DoesNotContain(Id, line.Replace(name, ""));
    }

    [Fact]
    public void Find_StaleAtWithinTwoSecondsOfSlack_IsAccepted()
    {
        var f = Session("-p", Id);
        var start = DateTimeOffset.UtcNow;
        Put(100, Marker(100, Id, f, at: start.AddSeconds(-1.5)));
        Assert.NotNull(Finder(new[] { 100 }, _ => start).Find(Adapters));
        Put(100, Marker(100, Id, f, at: start.AddSeconds(-2.5)));
        Assert.Null(Finder(new[] { 100 }, _ => start).Find(Adapters));
    }

    [Fact]
    public void Find_NestedHost_NearerRefused_ClimbsToOuter()
    {
        // Documents the limit of spec section 1: an inner host whose marker is refused
        // resolves to the outer session's marker, because the walk climbs on.
        var outer = Session("-p", OuterId);
        Put(100, Marker(100, Id, Path.Combine(_root, "-p", "..", "-p", Id + ".jsonl")));   // refused
        Put(200, Marker(200, OuterId, outer));

        var found = Finder(new[] { 100, 200 }).Find(Adapters);

        Assert.Equal(OuterId, found!.HostSessionId);
    }

    [Fact]
    public void Find_RootThatIsALink_IsAccepted()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = Path.Combine(_tmp, "real-projects");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_tmp, "linked-projects");
        Directory.CreateSymbolicLink(link, real);
        var file = Session("-p", Id, link);
        Put(100, Marker(100, Id, file));

        var found = Finder(new[] { 100 }).Find(new ITranscriptAdapter[] { new FakeAdapter(Host, link) });

        Assert.Equal(Id, found!.HostSessionId);   // the root is the user's own business; only what is below it is checked
        Assert.Empty(_log);
    }

    [Fact]
    public void Find_RootWithATrailingSeparator_IsAccepted()
    {
        var f = Session("-p", Id);
        Put(100, Marker(100, Id, f));
        var found = Finder(new[] { 100 }).Find(new ITranscriptAdapter[] { new FakeAdapter(Host, _root + Path.DirectorySeparatorChar) });
        Assert.Equal(Id, found!.HostSessionId);
    }

    [Fact]
    public void Find_ProcessStartUnknown_SkipsTheAgeCheck()
    {
        var f = Session("-p", Id);
        Put(100, Marker(100, Id, f, at: DateTimeOffset.UtcNow.AddDays(-400)));
        Assert.NotNull(Finder(new[] { 100 }, _ => null).Find(Adapters));
        Assert.Empty(_log);
    }

    [Fact]
    public void Find_DefaultProcessStart_RealShortLivedProcess()
    {
        // No injected processStart: the default reads Process.GetProcessById(pid).StartTime.
        var psi = new ProcessStartInfo("node") { UseShellExecute = false };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("setTimeout(()=>{},60000)");
        using var proc = Process.Start(psi)!;
        try
        {
            var f = Session("-p", Id);
            var start = proc.StartTime.ToUniversalTime();
            var finder = new LiveSessionFinder(_data, new CachedAncestry(() => new[] { proc.Id }), log: _log.Add);

            Put(proc.Id, Marker(proc.Id, Id, f, at: new DateTimeOffset(start).AddSeconds(-30)));
            Assert.Null(finder.Find(Adapters));              // written before the process started: a reused pid
            Assert.Single(_log);

            Put(proc.Id, Marker(proc.Id, Id, f, at: DateTimeOffset.UtcNow));
            var found = new LiveSessionFinder(_data, new CachedAncestry(() => new[] { proc.Id }), log: _log.Add).Find(Adapters);
            Assert.Equal(Id, found!.HostSessionId);          // written after it started
        }
        finally
        {
            try { proc.Kill(); } catch { }
        }
    }

    [Fact]
    public void Find_DefaultProcessStart_VanishedPid_SkipsTheAgeCheck()
    {
        // a pid that no longer exists cannot give a start time: the check is skipped, not a refusal
        var f = Session("-p", Id);
        var pid = int.MaxValue - 7;
        Put(pid, Marker(pid, Id, f, at: DateTimeOffset.UtcNow.AddDays(-1)));
        var finder = new LiveSessionFinder(_data, new CachedAncestry(() => new[] { pid }), log: _log.Add);
        Assert.NotNull(finder.Find(Adapters));
    }

    [Fact]
    public void Find_LogsOncePerFileAndNeverToStdout()
    {
        PutRaw(100, "garbage");
        PutRaw(200, "[]");
        var finder = new LiveSessionFinder(_data, new CachedAncestry(() => new[] { 100, 200 }), _ => null);   // default log

        var origOut = Console.Out;
        var origErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            Assert.Null(finder.Find(Adapters));
            Assert.Null(finder.Find(Adapters));
            Assert.Null(finder.Find(Adapters));
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }

        Assert.Equal("", stdout.ToString());
        var lines = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);                       // once per file, not per call
        Assert.Contains(lines, l => l.Contains("claude-code-100.json"));
        Assert.Contains(lines, l => l.Contains("claude-code-200.json"));
        Assert.All(lines, l => Assert.DoesNotContain("garbage", l));
    }

    [Fact]
    public void Find_UsesTheCachedChainOnce()
    {
        var calls = 0;
        var f = Session("-p", Id);
        Put(100, Marker(100, Id, f));
        var finder = new LiveSessionFinder(_data, new CachedAncestry(() => { calls++; return new[] { 100 }; }), _ => null, _log.Add);
        Assert.NotNull(finder.Find(Adapters));
        Assert.NotNull(finder.Find(Adapters));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Find_EachAdapterIsTriedForEachPid_HostNamesTheFile()
    {
        // a second adapter with another host id: its marker file is <host>-<pid>.json, found beside the first's
        var otherRoot = Path.Combine(_tmp, "other-root");
        var otherFile = Session("-p", "sess-1", otherRoot);
        var m = Marker(100, "sess-1", otherFile, host: "other");
        Put(100, m, host: "other");

        var found = Finder(new[] { 100 }).Find(new ITranscriptAdapter[] { new FakeAdapter(Host, _root), new FakeAdapter("other", otherRoot) });

        Assert.Equal("other", found!.Host);
        Assert.Equal("other:sess-1", found.Ref);
    }

    [Fact]
    public void CurrentSession_None_AndWire()
    {
        Assert.Equal(CurrentKind.None, CurrentSession.None.Kind);
        Assert.Equal("none", CurrentSession.None.Wire);
        Assert.Null(CurrentSession.None.Ref);
        Assert.Equal("guessed", new CurrentSession(CurrentKind.Guessed, Host, Id, "/x", "/y").Wire);
        Assert.Equal(Host + ":" + Id, new CurrentSession(CurrentKind.Guessed, Host, Id, "/x", "/y").Ref);
    }
}
