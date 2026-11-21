using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The fixture home, archives and helpers shared by the ingester tests: a real file-backed store, the real Claude Code
/// adapter (wrapped in <see cref="HookAdapter"/>) and a project directory that does not exist, so its key is its folder
/// name.
/// </summary>
internal sealed class IngestHarness : IDisposable
{
    public const string Host = "claude-code";

    private readonly List<TranscriptTestDb> _dbs = new();

    public ClaudeFixtureHome Home { get; } = new();
    public string UserHome { get; }
    public string Cwd { get; }
    public TranscriptTestDb Db { get; }

    public IngestHarness()
    {
        UserHome = Path.Combine(Home.Root, "me");
        Directory.CreateDirectory(UserHome);
        Cwd = Path.Combine(Home.Root, "work", "proj");   // never created: key "proj", source "path"
        Db = NewDb();
    }

    public TranscriptTestDb NewDb()
    {
        var db = new TranscriptTestDb(userHome: UserHome);
        _dbs.Add(db);
        return db;
    }

    public HookAdapter Adapter(Action<SessionSource>? hook = null, bool everyTime = false) =>
        new(new ClaudeCodeAdapter(Home.ProjectsDir), hook, everyTime);

    public TranscriptIngester Ingester(ITranscriptStore store, ITranscriptAdapter adapter, ProjectResolver? resolver = null) =>
        new(store, adapter, resolver ?? new ProjectResolver(), UserHome);

    /// <summary>The source as enumeration would report it now.</summary>
    public static SessionSource Stat(PlacedSession s)
    {
        var fi = new FileInfo(s.Path);
        return new SessionSource(s.Id, fi.FullName, fi.Length,
            new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    public SessionIngestResult Ingest(TranscriptTestDb db, PlacedSession s, ITranscriptAdapter? adapter = null, ProjectResolver? resolver = null) =>
        Ingester(db.Store, adapter ?? Adapter(), resolver).IngestSession(Stat(s), CancellationToken.None);

    /// <summary>The file as it is now, ingested once into a fresh archive.</summary>
    public ArchiveSnapshot Once(PlacedSession s, ProjectResolver? resolver = null)
    {
        var db = NewDb();
        var result = Ingest(db, s, resolver: resolver);
        Assert.True(result.Status is SessionIngestStatus.New, "the once-ingested reference was " + result.Status);
        return ArchiveSnapshot.Read(db.Path);
    }

    public static void AssertSame(ArchiveSnapshot expected, ArchiveSnapshot actual, string context = "")
    {
        AssertRows("tx_session", expected.Sessions, actual.Sessions, context);
        AssertRows("tx_chunk", expected.Chunks, actual.Chunks, context);
    }

    private static void AssertRows(string table, IReadOnlyList<string> expected, IReadOnlyList<string> actual, string context)
    {
        if (expected.SequenceEqual(actual)) return;
        var n = Math.Max(expected.Count, actual.Count);
        for (var i = 0; i < n; i++)
        {
            var e = i < expected.Count ? expected[i] : "<missing>";
            var a = i < actual.Count ? actual[i] : "<missing>";
            if (e != a)
                Assert.Fail($"{context} {table} row {i} differs ({expected.Count} expected rows, {actual.Count} actual)\nexpected: {e}\nactual:   {a}");
        }
    }

    /// <summary>The offset of the line holding <paramref name="marker"/> (the byte after the previous LF).</summary>
    public static long LineStartOf(byte[] bytes, string marker)
    {
        var m = System.Text.Encoding.UTF8.GetBytes(marker);
        var at = bytes.AsSpan().IndexOf(m);
        Assert.True(at >= 0, "marker not found: " + marker);
        var lf = bytes.AsSpan(0, at).LastIndexOf((byte)0x0A);
        return lf + 1;
    }

    public long Sessions(TranscriptTestDb db) => db.Scalar("SELECT count(*) FROM tx_session");
    public long Chunks(TranscriptTestDb db) => db.Scalar("SELECT count(*) FROM tx_chunk");

    public static List<string> Contents(TranscriptTestDb db)
    {
        var list = new List<string>();
        using var conn = db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM tx_chunk ORDER BY exchange_start, part";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>A git repository at <paramref name="path"/> with an optional <c>origin</c> remote.</summary>
    public static string MakeRepo(string path, string? remoteUrl)
    {
        var git = Path.Combine(path, ".git");
        Directory.CreateDirectory(git);
        SetRemote(path, remoteUrl);
        return path;
    }

    public static void SetRemote(string repo, string? remoteUrl)
    {
        var config = remoteUrl is null
            ? "[core]\n\trepositoryformatversion = 0\n"
            : $"[core]\n\trepositoryformatversion = 0\n[remote \"origin\"]\n\turl = {remoteUrl}\n";
        File.WriteAllText(Path.Combine(repo, ".git", "config"), config);
    }

    public void Dispose()
    {
        foreach (var db in _dbs) db.Dispose();
        Home.Dispose();
    }
}

/// <summary>
/// <see cref="TranscriptIngester.IngestSession"/>: the modes of spec section 7 (fresh, unchanged, resume, rewritten,
/// changed), the watermark, the exclusion and cutoff rules, and the guarantee that a grown session equals the same
/// file ingested once.
/// </summary>
public sealed class TranscriptIngesterTests : IDisposable
{
    private const string Host = IngestHarness.Host;

    private readonly IngestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private TranscriptTestDb Db => _h.Db;
    private ClaudeFixtureHome Home => _h.Home;

    private static string Iso(DateTimeOffset t) => TranscriptTime.Iso(t);

    private string? Col(string column, string id) =>
        Db.Text($"SELECT CAST({column} AS TEXT) FROM tx_session WHERE host_session_id = '{id}'");

    // ------------------------------------------------------------------
    // Fresh, unchanged, grown
    // ------------------------------------------------------------------

    [Fact]
    public void Fresh_StoresSessionAndChunks_New()
    {
        var s = Home.Place("decision", _h.Cwd, "fresh-1");
        var bytes = File.ReadAllBytes(s.Path);

        var result = _h.Ingest(Db, s);

        Assert.Equal(SessionIngestStatus.New, result.Status);
        Assert.Equal(1, _h.Sessions(Db));
        Assert.Equal(_h.Chunks(Db), result.ChunksNew);
        Assert.Equal(0, result.ChunksChanged + result.ChunksUnchanged + result.ChunksRemoved);

        // Units: 0 "Please look ...", 1 "/develop 148", 2 "Now run the tests ...", 3 the queued prompt (the last).
        Assert.Equal("interactive", Col("kind", s.Id));
        Assert.Equal("Please look at the schema migration and plan a fix.", Col("title", s.Id));
        Assert.Equal("3", Col("settled_user_turns", s.Id));
        Assert.Equal("4", Col("exchange_count", s.Id));
        Assert.Equal(
            IngestHarness.LineStartOf(bytes, "\"uuid\":\"00000000-0000-4000-8000-000000000020\"").ToString(),
            Col("ingested_offset", s.Id));
        Assert.Null(Col("built_under_cutoff", s.Id));
        Assert.Equal("proj", Col("project_key", s.Id));
        Assert.Equal("path", Col("project_key_source", s.Id));
        Assert.Equal(_h.Cwd, Col("cwd", s.Id));
        Assert.Equal(s.Path, Col("source_path", s.Id));
        Assert.Equal(bytes.Length.ToString(), Col("source_size", s.Id));
        var firstLine = bytes.AsSpan(0, bytes.AsSpan().IndexOf((byte)0x0A)).ToArray();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(firstLine)).ToLowerInvariant(), Col("source_fingerprint", s.Id));
        Assert.Equal(Iso(Home.BaseTime.AddSeconds(1)), Col("started_at", s.Id));
        Assert.Equal(Iso(Home.BaseTime.AddSeconds(26)), Col("ended_at", s.Id));

        var contents = IngestHarness.Contents(Db);
        Assert.StartsWith($"claude-code session fresh-1 | project proj | 2026-01-01 | interactive | dialogue | exchanges 0-0\n\nUser: Please look",
            contents[0]);
        Assert.Contains(contents, c => c.Contains("| exchanges 3-3") && c.Contains("zqxqueued"));
    }

    [Fact]
    public void SecondRun_Unchanged_NoRead_IdsAndEmbeddedKept()
    {
        var s = Home.Place("decision", _h.Cwd, "twice-1");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        Db.Exec("UPDATE tx_chunk SET embedded = 1");
        var before = ArchiveSnapshot.Read(Db.Path, includeIds: true);

        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Unchanged, result.Status);
        Assert.Equal(0, adapter.ReadHeadCalls);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        Assert.Equal(0, adapter.IsLineStartCalls);
        Assert.Equal(_h.Chunks(Db), Db.Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1"));
        IngestHarness.AssertSame(before, ArchiveSnapshot.Read(Db.Path, includeIds: true));
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("compaction")]
    public void Grown_AtEveryCutPoint_EqualsIngestedOnce(string fixture)
    {
        var s = Home.Place(fixture, _h.Cwd, "grown-" + fixture);
        var full = File.ReadAllBytes(s.Path);
        var once = _h.Once(s);

        // Every line boundary (0 and the full length included) and one offset in the middle of every line.
        var cuts = new SortedSet<int> { 0 };
        var start = 0;
        for (var i = 0; i < full.Length; i++)
        {
            if (full[i] != 0x0A) continue;
            cuts.Add(start + (i - start) / 2 + 1);
            cuts.Add(i + 1);
            start = i + 1;
        }

        var resumed = 0;
        foreach (var cut in cuts)
        {
            using var db = new TranscriptTestDb();
            Home.Write(s, full[..cut]);
            _h.Ingest(db, s);

            Home.Write(s, full);
            var adapter = _h.Adapter();
            var second = _h.Ingest(db, s, adapter);
            Assert.True(second.Status is SessionIngestStatus.New or SessionIngestStatus.Updated or SessionIngestStatus.Unchanged,
                $"cut {cut}: {second.Status}");
            if (adapter.ReadOffsets.Count > 0 && adapter.ReadOffsets[0] > 0) resumed++;

            IngestHarness.AssertSame(once, ArchiveSnapshot.Read(db.Path), $"{fixture} cut {cut}/{full.Length}:");
        }

        // The cuts exercise the resumed pass, not only fresh and rewritten ones.
        Assert.True(resumed >= 3, $"only {resumed} cuts resumed");
    }

    [Fact]
    public void KindFlip_OnGrowth_RedoesAsRewritten()
    {
        var s = Home.Place("automated-opening", _h.Cwd, "kind-1");
        // A compaction boundary and an assistant-only unit, so the lone user turn lies in a settled unit.
        Home.AppendLine(s,
            "{\"parentUuid\":null,\"isSidechain\":false,\"type\":\"system\",\"subtype\":\"compact_boundary\"," +
            "\"content\":\"Conversation compacted\",\"uuid\":\"00000000-0000-4000-8000-0000000000b1\"," +
            "\"timestamp\":\"2026-01-01T00:00:05.000Z\",\"cwd\":\"" + s.Cwd + "\",\"sessionId\":\"" + s.Id + "\"}");
        Home.AppendLine(s,
            "{\"parentUuid\":null,\"isSidechain\":false,\"type\":\"assistant\",\"message\":{\"role\":\"assistant\"," +
            "\"content\":[{\"type\":\"text\",\"text\":\"Continuing after the boundary. zqxkindflip\"}]}," +
            "\"uuid\":\"00000000-0000-4000-8000-0000000000b2\",\"timestamp\":\"2026-01-01T00:00:06.000Z\"," +
            "\"cwd\":\"" + s.Cwd + "\",\"sessionId\":\"" + s.Id + "\"}");

        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        Assert.Equal("automated", Col("kind", s.Id));
        Assert.Equal("2", Col("exchange_count", s.Id));
        Assert.Equal("1", Col("settled_user_turns", s.Id));
        var watermark = long.Parse(Col("ingested_offset", s.Id)!);
        Assert.True(watermark > 0);

        Home.AppendExchange(s, "A second question from a person.", "A second answer.", 100);
        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Updated, result.Status);
        Assert.Equal(new long[] { watermark, 0 }, adapter.ReadOffsets);   // resumed, then redone from the start
        Assert.Equal("interactive", Col("kind", s.Id));
        var contents = IngestHarness.Contents(Db);
        Assert.Equal(3, contents.Count);
        Assert.All(contents, c => Assert.Contains("| interactive |", c));
        Assert.DoesNotContain(contents, c => c.Contains("| automated |"));
        IngestHarness.AssertSame(_h.Once(s), ArchiveSnapshot.Read(Db.Path));
    }

    [Fact]
    public void DeletedSource_KeepsRowAndChunks()
    {
        var s = Home.Place("decision", _h.Cwd, "deleted-1");
        var stale = IngestHarness.Stat(s);
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        var before = ArchiveSnapshot.Read(Db.Path, includeIds: true);
        Assert.NotEmpty(before.Chunks);

        Home.Delete(s);
        var ingester = _h.Ingester(Db.Store, _h.Adapter());
        var same = ingester.IngestSession(stale, CancellationToken.None);
        var other = ingester.IngestSession(stale with { Size = stale.Size + 10, MtimeMs = stale.MtimeMs + 1000 }, CancellationToken.None);

        Assert.Equal(SessionIngestStatus.Unchanged, same.Status);
        Assert.Equal(SessionIngestStatus.Skipped, other.Status);
        IngestHarness.AssertSame(before, ArchiveSnapshot.Read(Db.Path, includeIds: true));
    }

    // ------------------------------------------------------------------
    // Exclusion
    // ------------------------------------------------------------------

    [Fact]
    public void ForgottenSession_FileExists_NotOpened()
    {
        var s = Home.Place("decision", _h.Cwd, "forgot-1");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        Db.Store.ForgetSession(Host, s.Id);
        var never = Home.Place("decision", _h.Cwd, "forgot-2");
        Db.Store.ForgetSession(Host, never.Id);

        var adapter = _h.Adapter();
        var again = _h.Ingest(Db, s, adapter);
        var first = _h.Ingest(Db, never, adapter);

        Assert.Equal(SessionIngestStatus.Excluded, again.Status);
        Assert.Equal(SessionIngestStatus.Excluded, first.Status);
        Assert.Equal(0, adapter.ReadHeadCalls);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        Assert.True(File.Exists(s.Path));
        Assert.Equal(0, _h.Sessions(Db));
        Assert.Equal(0, _h.Chunks(Db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForgottenProjectByKey_AndByRoot_StayOut(bool byRoot)
    {
        var repo = IngestHarness.MakeRepo(Path.Combine(Home.Root, "repos", "proj"), "https://github.com/Owner/Repo.git");
        var sub = Path.Combine(repo, "sub");
        Directory.CreateDirectory(sub);
        var s = Home.Place("decision", sub, "fproj-1");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        Assert.Equal("owner/repo", Col("project_key", s.Id));
        Assert.Equal(repo, Col("project_root", s.Id));
        Db.Store.ForgetProject("owner/repo");

        PlacedSession target = s;
        if (byRoot)
        {
            // The repository is re-keyed, so only the stored root can keep a session under it out.
            IngestHarness.SetRemote(repo, "https://github.com/Other/Thing.git");
            Assert.Equal("other/thing", ProjectIdentityRule.Derive(sub, "x", new ProjectResolver(), _h.UserHome).Key);
            target = Home.Place("decision", sub, "fproj-2");
        }

        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, target, adapter);

        Assert.Equal(SessionIngestStatus.Excluded, result.Status);
        Assert.Equal(1, adapter.ReadHeadCalls);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        Assert.Equal(0, _h.Sessions(Db));
        Assert.Equal(0, _h.Chunks(Db));
    }

    [Fact]
    public void ForgottenProject_SiblingWorktreeRemovedLater_StaysOut()
    {
        // /src/app has the remote; `git worktree add ../app-hotfix` makes a sibling whose .git file leads back to it.
        var repo = IngestHarness.MakeRepo(Path.Combine(Home.Root, "src", "app"), "https://github.com/Owner/App.git");
        var worktree = Path.Combine(Home.Root, "src", "app-hotfix");
        var admin = Path.Combine(repo, ".git", "worktrees", "app-hotfix");
        Directory.CreateDirectory(admin);
        File.WriteAllText(Path.Combine(admin, "commondir"), "../..\n");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {admin}\n");
        var s = Home.Place("decision", worktree, "sibling-1");

        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        Assert.Equal("owner/app", Col("project_key", s.Id));
        Assert.Equal(repo, Col("project_root", s.Id));
        Db.Store.ForgetProject("owner/app");

        // `git worktree remove`: the session's file stays, its directory and the admin entry go.
        Directory.Delete(worktree, recursive: true);
        Directory.Delete(admin, recursive: true);
        Assert.Equal("app-hotfix", ProjectIdentityRule.Derive(worktree, "x", new ProjectResolver(), _h.UserHome).Key);
        Assert.True(File.Exists(s.Path));

        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Excluded, result.Status);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        Assert.Equal(0, _h.Sessions(Db));
        Assert.Equal(0, _h.Chunks(Db));
    }

    // ------------------------------------------------------------------
    // Cutoff
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1000, true)]
    [InlineData(1, false)]
    public void MtimeAtOrBeforeCutoff_Excluded_NotRead(int offsetMs, bool excluded)
    {
        var s = Home.Place("decision", _h.Cwd, "mtime-1");
        var cutoff = Home.BaseTime.AddDays(1);
        Db.Store.ForgetAll(cutoff);
        Home.SetMtime(s, cutoff.AddMilliseconds(offsetMs));

        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter);

        if (excluded)
        {
            Assert.Equal(SessionIngestStatus.Excluded, result.Status);
            Assert.Equal(0, adapter.ReadHeadCalls);
            Assert.Equal(0, adapter.IsLineStartCalls);
            Assert.Equal(0, adapter.ReadRecordsCalls);
        }
        else
        {
            // Read, but every record is before the cutoff: nothing kept, nothing stored.
            Assert.Equal(SessionIngestStatus.Empty, result.Status);
            Assert.Equal(1, adapter.ReadRecordsCalls);
        }
        Assert.Equal(0, _h.Sessions(Db));
    }

    private PlacedSession StraddleAfterCutoff(string id)
    {
        var s = Home.Place("straddle", _h.Cwd, id);
        Home.SetMtime(s, Home.BaseTime.AddDays(1));
        Db.Store.ForgetAll(Home.BaseTime.AddSeconds(50));
        return s;
    }

    [Fact]
    public void Straddle_OnlyRecordsAfterCutoff()
    {
        var s = StraddleAfterCutoff("straddle-1");

        var result = _h.Ingest(Db, s);

        Assert.Equal(SessionIngestStatus.New, result.Status);
        var contents = IngestHarness.Contents(Db);
        Assert.Equal(2, contents.Count);
        Assert.Contains("\n\nAssistant: Second answer after the cutoff. zqxstraddle4", contents[0]);
        Assert.DoesNotContain("User:", contents[0]);
        Assert.Contains("zqxstraddle5", contents[1]);
        Assert.Contains("zqxstraddle6", contents[1]);
        foreach (var gone in new[] { "zqxstraddle1", "zqxstraddle2", "zqxstraddle3" })
            Assert.DoesNotContain(contents, c => c.Contains(gone));

        var raw = Db.Store.IngestNotBefore()!.Raw;
        Assert.Equal(Iso(Home.BaseTime.AddSeconds(50)), raw);
        Assert.Equal(raw, Col("built_under_cutoff", s.Id));
        Assert.Equal(Iso(Home.BaseTime.AddSeconds(60)), Col("started_at", s.Id));
        Assert.Equal("Third question after the cutoff. zqxstraddle5", Col("title", s.Id));
    }

    [Fact]
    public void UnforgetCutoff_UnchangedFile_HiddenRecordsReturn()
    {
        var s = StraddleAfterCutoff("straddle-2");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);

        Assert.True(Db.Store.ClearCutoff());
        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Updated, result.Status);
        Assert.Equal(new long[] { 0 }, adapter.ReadOffsets);
        Assert.Contains(IngestHarness.Contents(Db), c => c.Contains("zqxstraddle1"));
        IngestHarness.AssertSame(_h.Once(s), ArchiveSnapshot.Read(Db.Path));
    }

    [Fact]
    public void CutoffHidesEverything_RowReset_Empty()
    {
        var s = Home.Place("automated-opening", _h.Cwd, "hide-1");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        Assert.Equal("automated", Col("kind", s.Id));
        Assert.True(_h.Chunks(Db) > 0);

        // A cutoff after the last record, written directly (ForgetAll would delete the row), and a file touched after it.
        var raw = Iso(Home.BaseTime.AddHours(1));
        Db.Exec($"INSERT INTO tx_state (key, value) VALUES ('ingest_not_before', '{raw}')");
        Home.SetMtime(s, Home.BaseTime.AddHours(2));

        var result = _h.Ingest(Db, s);

        Assert.Equal(SessionIngestStatus.Empty, result.Status);
        Assert.Equal(0, _h.Chunks(Db));
        Assert.Equal(1, _h.Sessions(Db));
        Assert.Equal("0", Col("ingested_offset", s.Id));
        Assert.Equal("0", Col("exchange_count", s.Id));
        Assert.Equal("0", Col("settled_user_turns", s.Id));
        Assert.Null(Col("title", s.Id));
        Assert.Null(Col("started_at", s.Id));
        Assert.Null(Col("ended_at", s.Id));
        Assert.Equal("automated", Col("kind", s.Id));
        Assert.Equal(raw, Col("built_under_cutoff", s.Id));
        Assert.Equal(new FileInfo(s.Path).Length.ToString(), Col("source_size", s.Id));

        Assert.True(Db.Store.ClearCutoff());
        var adapter = _h.Adapter();
        var after = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Updated, after.Status);
        Assert.Equal(new long[] { 0 }, adapter.ReadOffsets);
        IngestHarness.AssertSame(_h.Once(s), ArchiveSnapshot.Read(Db.Path));
    }

    [Fact]
    public void ResetRow_GrowsUnderSameCutoff_NumbersFromZero()
    {
        // An interactive session that stays interactive, so no kind redo can hide the numbering.
        var s = Home.Place("decision", _h.Cwd, "hide-2");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        Db.Exec($"INSERT INTO tx_state (key, value) VALUES ('ingest_not_before', '{Iso(Home.BaseTime.AddHours(1))}')");
        Home.SetMtime(s, Home.BaseTime.AddHours(2));
        Assert.Equal(SessionIngestStatus.Empty, _h.Ingest(Db, s).Status);

        // The same cutoff, a record after it: exchange_count 0 makes this a rewritten pass, numbered from 0, not -1.
        Home.AppendExchange(s, "A question after the cutoff.", "An answer after the cutoff.", 3 * 3600);
        var result = _h.Ingest(Db, s);

        Assert.Equal(SessionIngestStatus.Updated, result.Status);
        Assert.Equal("1", Col("exchange_count", s.Id));
        Assert.Equal(0, Db.Scalar("SELECT min(exchange_start) FROM tx_chunk"));
        Assert.Equal(1, _h.Chunks(Db));
        Assert.Contains("| exchanges 0-0", IngestHarness.Contents(Db)[0]);
    }

    // ------------------------------------------------------------------
    // Changed source
    // ------------------------------------------------------------------

    private (PlacedSession S, byte[] Full, ArchiveSnapshot Before) Stored(string id)
    {
        var s = Home.Place("decision", _h.Cwd, id);
        var full = File.ReadAllBytes(s.Path);
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        return (s, full, ArchiveSnapshot.Read(Db.Path, includeIds: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacedFile_Changed_ChunksKept(bool onlyFirstLineDiffers)
    {
        var (s, full, before) = Stored(onlyFirstLineDiffers ? "replaced-2" : "replaced-1");
        if (onlyFirstLineDiffers)
        {
            // One byte of the first line changed and the file grown: only the fingerprint shows the replacement.
            var replaced = (byte[])full.Clone();
            var at = replaced.AsSpan().IndexOf("zqxqueueop"u8) + 9;
            replaced[at] = (byte)'q';
            Home.Write(s, replaced);
            Home.AppendExchange(s, "Another question.", "Another answer.", 400);
        }
        else
        {
            Home.Replace(s, "compaction");
        }

        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Changed, result.Status);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        IngestHarness.AssertSame(before, ArchiveSnapshot.Read(Db.Path, includeIds: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedFile_Changed(bool keepWatermark)
    {
        var (s, full, before) = Stored(keepWatermark ? "truncated-2" : "truncated-1");
        int end;
        if (keepWatermark)
        {
            // Drop only the last line: the stored offset is still a line start, only the size shows the truncation.
            end = Array.LastIndexOf(full, (byte)0x0A, full.Length - 2) + 1;
            Assert.True(end > long.Parse(Col("ingested_offset", s.Id)!));
        }
        else
        {
            end = 0;
            for (int i = 0, lines = 0; i < full.Length; i++)
                if (full[i] == 0x0A && ++lines == 5) { end = i + 1; break; }
        }
        Home.Write(s, full[..end]);

        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Changed, result.Status);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        IngestHarness.AssertSame(before, ArchiveSnapshot.Read(Db.Path, includeIds: true));
    }

    [Fact]
    public void OffsetNotAtLineStart_Changed()
    {
        var (s, full, before) = Stored("shifted-1");
        // Same first line, one more byte (a leading space on line 2): the byte before the stored offset is now '}'.
        var secondLine = Array.IndexOf(full, (byte)0x0A) + 1;
        var shifted = full[..secondLine].Concat(new[] { (byte)' ' }).Concat(full[secondLine..]).ToArray();
        Home.Write(s, shifted);
        var offset = long.Parse(Col("ingested_offset", s.Id)!);
        Assert.NotEqual((byte)0x0A, shifted[offset - 1]);

        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Changed, result.Status);
        Assert.Equal(1, adapter.IsLineStartCalls);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        IngestHarness.AssertSame(before, ArchiveSnapshot.Read(Db.Path, includeIds: true));
    }

    [Fact]
    public void SameSizeNewMtime_IsRead()
    {
        var (s, full, _) = Stored("samesize-1");
        var stored = IngestHarness.Stat(s);
        var edited = (byte[])full.Clone();
        var at = edited.AsSpan().IndexOf("Stopped as asked."u8);
        edited[at] = (byte)'Z';   // same size, after the watermark
        Home.Write(s, edited);
        Home.SetMtime(s, DateTimeOffset.FromUnixTimeMilliseconds(stored.MtimeMs).AddSeconds(5));

        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Updated, result.Status);
        Assert.Equal(1, adapter.ReadRecordsCalls);
        Assert.Contains(IngestHarness.Contents(Db), c => c.Contains("Ztopped as asked."));
        IngestHarness.AssertSame(_h.Once(s), ArchiveSnapshot.Read(Db.Path));
    }

    [Fact]
    public void GrownSameMtime_IsRead()
    {
        var (s, _, _) = Stored("samemtime-1");
        var stored = IngestHarness.Stat(s);
        Home.AppendExchange(s, "A later question.", "A later answer.", 500);
        Home.SetMtime(s, DateTimeOffset.FromUnixTimeMilliseconds(stored.MtimeMs));
        Assert.Equal(stored.MtimeMs, IngestHarness.Stat(s).MtimeMs);

        var result = _h.Ingest(Db, s);

        Assert.Equal(SessionIngestStatus.Updated, result.Status);
        IngestHarness.AssertSame(_h.Once(s), ArchiveSnapshot.Read(Db.Path));
    }

    [Fact]
    public void ResumeFindsNoUnit_RedoneAsRewritten()
    {
        var (s, full, _) = Stored("nounit-1");
        var watermark = long.Parse(Col("ingested_offset", s.Id)!);
        // Same first line, a line start at the stored offset, a larger file, but nothing kept from the offset on.
        var noKept = Home.Render("no-kept", s.Cwd, s.Id);
        var noKeptRest = noKept[(Array.IndexOf(noKept, (byte)0x0A) + 1)..];
        var grown = full[..(int)watermark].Concat(noKeptRest).Concat(noKeptRest).ToArray();
        Assert.True(grown.Length > full.Length);
        Home.Write(s, grown);

        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Updated, result.Status);
        Assert.Equal(new[] { watermark, 0L }, adapter.ReadOffsets);
        Assert.Equal("3", Col("exchange_count", s.Id));
        IngestHarness.AssertSame(_h.Once(s), ArchiveSnapshot.Read(Db.Path));
    }

    // ------------------------------------------------------------------
    // Nothing kept, partial lines
    // ------------------------------------------------------------------

    [Fact]
    public void NoKeptContent_StoresNothing_Empty()
    {
        var s = Home.Place("no-kept", _h.Cwd, "nokept-1");
        var adapter = _h.Adapter();

        var first = _h.Ingest(Db, s, adapter);
        var second = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Empty, first.Status);
        Assert.Equal(SessionIngestStatus.Empty, second.Status);
        Assert.Equal(2, adapter.ReadRecordsCalls);   // read again each run: nothing records that it was empty
        Assert.Equal(0, _h.Sessions(Db));
    }

    [Fact]
    public void NoCompleteFirstLine_Skipped()
    {
        var s = Home.Place("decision", _h.Cwd, "nofirst-1");
        var full = File.ReadAllBytes(s.Path);
        Home.Write(s, full[..Array.IndexOf(full, (byte)0x0A)]);

        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter);

        Assert.Equal(SessionIngestStatus.Skipped, result.Status);
        Assert.Equal(1, adapter.ReadHeadCalls);
        Assert.Equal(0, adapter.ReadRecordsCalls);
        Assert.Equal(0, _h.Sessions(Db));
    }

    [Fact]
    public void PartialTrailingLine_NotConsumed_ThenReadOnce()
    {
        var s = Home.Place("decision", _h.Cwd, "partial-1");
        var full = File.ReadAllBytes(s.Path);
        var cut = full.Length - 10;   // inside the last line ("Stopped as asked.")
        Home.Write(s, full[..cut]);

        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s).Status);
        Assert.DoesNotContain(IngestHarness.Contents(Db), c => c.Contains("Stopped as asked."));
        Assert.Equal(cut.ToString(), Col("source_size", s.Id));

        Home.Write(s, full);
        Assert.Equal(SessionIngestStatus.Updated, _h.Ingest(Db, s).Status);

        Assert.Single(IngestHarness.Contents(Db), c => c.Contains("Stopped as asked."));
        IngestHarness.AssertSame(_h.Once(s), ArchiveSnapshot.Read(Db.Path));
    }

    [Fact]
    public void CancelledDuringTheRead_Throws_AndWritesNothing()
    {
        var s = Home.Place("decision", _h.Cwd, "cancel-1");
        using var cts = new CancellationTokenSource();
        var adapter = _h.Adapter(_ => cts.Cancel());

        Assert.ThrowsAny<OperationCanceledException>(() =>
            _h.Ingester(Db.Store, adapter).IngestSession(IngestHarness.Stat(s), cts.Token));

        Assert.Equal(1, adapter.HookFired);
        Assert.Equal(0, _h.Sessions(Db));
        Assert.Equal(0, _h.Chunks(Db));
    }

    [Fact]
    public void GrownBetweenStatAndRead_IsReadAgainNextPass()
    {
        var s = Home.Place("decision", _h.Cwd, "statread-1");
        var full = File.ReadAllBytes(s.Path);
        var cut = (int)IngestHarness.LineStartOf(full, "\"uuid\":\"00000000-0000-4000-8000-000000000019\"");
        Home.Write(s, full[..cut]);
        var stat = IngestHarness.Stat(s);

        var adapter = _h.Adapter(_ => Home.Write(s, full));
        var first = _h.Ingester(Db.Store, adapter).IngestSession(stat, CancellationToken.None);

        Assert.Equal(SessionIngestStatus.New, first.Status);
        Assert.Equal(1, adapter.HookFired);
        Assert.Equal(cut.ToString(), Col("source_size", s.Id));   // the stat before the read, not what was read

        var second = _h.Ingest(Db, s, adapter);

        Assert.NotEqual(SessionIngestStatus.Unchanged, second.Status);
        Assert.Equal(2, adapter.ReadRecordsCalls);
        IngestHarness.AssertSame(_h.Once(s), ArchiveSnapshot.Read(Db.Path));
    }

    // ------------------------------------------------------------------
    // Project key
    // ------------------------------------------------------------------

    [Fact]
    public void ProjectKeyChange_RewritesEveryHeader_EqualsIngestedOnce()
    {
        var repo = IngestHarness.MakeRepo(Path.Combine(Home.Root, "repos", "Proj"), null);
        var s = Home.Place("decision", repo, "rekey-1");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s, resolver: new ProjectResolver()).Status);
        Assert.Equal("proj", Col("project_key", s.Id));
        Assert.Equal("path", Col("project_key_source", s.Id));

        IngestHarness.SetRemote(repo, "https://github.com/Owner/Repo.git");
        Home.AppendExchange(s, "One more question.", "One more answer.", 200);
        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter, new ProjectResolver());

        Assert.Equal(SessionIngestStatus.Updated, result.Status);
        Assert.Equal(new long[] { 0 }, adapter.ReadOffsets);
        Assert.Equal("owner/repo", Col("project_key", s.Id));
        Assert.Equal("remote", Col("project_key_source", s.Id));
        Assert.All(IngestHarness.Contents(Db), c => Assert.Contains("| project owner/repo |", c));
        IngestHarness.AssertSame(_h.Once(s, new ProjectResolver()), ArchiveSnapshot.Read(Db.Path));
    }

    [Fact]
    public void PathKeyNeverReplacesRemoteKey()
    {
        var repo = IngestHarness.MakeRepo(Path.Combine(Home.Root, "repos", "gone"), "https://github.com/Owner/Repo.git");
        var s = Home.Place("decision", repo, "keep-1");
        Assert.Equal(SessionIngestStatus.New, _h.Ingest(Db, s, resolver: new ProjectResolver()).Status);
        Assert.Equal("owner/repo", Col("project_key", s.Id));
        Assert.Equal(repo, Col("project_root", s.Id));
        var watermark = long.Parse(Col("ingested_offset", s.Id)!);

        Directory.Delete(repo, recursive: true);
        var derived = ProjectIdentityRule.Derive(repo, "x", new ProjectResolver(), _h.UserHome);
        Assert.Equal(("gone", "path"), (derived.Key, derived.KeySource));   // what a re-key would have used
        Home.AppendExchange(s, "One more question.", "One more answer.", 200);

        var adapter = _h.Adapter();
        var result = _h.Ingest(Db, s, adapter, new ProjectResolver());

        Assert.Equal(SessionIngestStatus.Updated, result.Status);
        Assert.Equal(new[] { watermark }, adapter.ReadOffsets);   // resumed, not rewritten
        Assert.Equal("owner/repo", Col("project_key", s.Id));
        Assert.Equal("remote", Col("project_key_source", s.Id));
        Assert.Equal(repo, Col("project_root", s.Id));
        Assert.All(IngestHarness.Contents(Db), c => Assert.Contains("| project owner/repo |", c));
    }
}
