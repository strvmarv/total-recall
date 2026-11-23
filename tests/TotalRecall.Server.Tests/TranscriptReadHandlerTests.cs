// transcript_get and transcript_sessions with a read service (read spec 2026-11-23, sections 2 to 4): the answer
// shapes through the source-generated JsonContext, the error results, the argument errors (each names its argument),
// the off and not-set-up answers and the per-call gate. A real archive in a temporary directory, seeded through the
// linked ReadArchiveBuilder.

namespace TotalRecall.Server.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.Server.Handlers;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using SqliteOpener = TotalRecall.Infrastructure.Storage.SqliteConnection;

public sealed class TranscriptReadHandlerTests : IDisposable
{
    private const string OffText = "Run `total-recall transcripts enable` in a terminal to turn the archive on.";
    private const string NotSetUpText = "The transcript archive is not set up in this server.";
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tr-read-" + Guid.NewGuid().ToString("N"));

    public TranscriptReadHandlerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string File => Path.Combine(_dir, "archive.db");

    private TranscriptLocation Location => new(File, IsMainDatabase: false);

    private TranscriptReadService Service() => new(() => new TranscriptReadContext(Location));

    /// <summary>Creates the archive and seeds it through <paramref name="seed"/>.</summary>
    private void Seed(Action<ReadArchiveBuilder> seed)
    {
        var open = TranscriptStore.Open(Location, create: true);
        Assert.Equal(TranscriptOpenStatus.Opened, open.Status);
        using var store = open.Store!;
        seed(new ReadArchiveBuilder(store));
    }

    /// <summary>
    /// Session <c>claude-code:s1</c> (no title, interactive, owner/repo) with exchanges 0 to 2, and an automated session
    /// <c>claude-code:auto</c> and an interactive <c>pi:p2</c> one, each with one exchange.
    /// </summary>
    private void SeedSmall() => Seed(a =>
    {
        a.Session("claude-code", "s1", startedAt: T0, endedAt: T0.AddHours(1))
            .Exchange(0, "body zero", ts: T0)
            .Exchange(1, "body one", ts: T0.AddMinutes(1), hasError: true)
            .Exchange(2, "body two", ts: T0.AddMinutes(2))
            .Commit();
        a.Session("claude-code", "auto", kind: "automated", title: "Reviewer", endedAt: T0.AddHours(3))
            .Exchange(0, "review").Commit();
        a.Session("pi", "p2", project: "other/repo", title: "Second", startedAt: T0.AddHours(1), endedAt: T0.AddHours(2))
            .Exchange(0, "pi body").Commit();
    });

    private long ChunkId(int exchange, string hostSessionId = "s1")
    {
        using var conn = SqliteOpener.Open(File, pooling: false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT c.id FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id " +
            "WHERE s.host_session_id = $h AND c.exchange_start = $e AND c.part = 0";
        cmd.Parameters.AddWithValue("$h", hostSessionId);
        cmd.Parameters.AddWithValue("$e", exchange);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static JsonElement? Args(string? json) =>
        json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<JsonElement> Data(IToolHandler h, string? args)
    {
        var r = await h.ExecuteAsync(Args(args), CancellationToken.None);
        Assert.NotEqual(true, r.IsError);
        var content = Assert.Single(r.Content);
        Assert.Equal("text", content.Type);
        return JsonDocument.Parse(content.Text).RootElement.Clone();
    }

    private static async Task<string> ErrorText(IToolHandler h, string? args)
    {
        var r = await h.ExecuteAsync(Args(args), CancellationToken.None);
        Assert.True(r.IsError);
        var content = Assert.Single(r.Content);
        Assert.Equal("text", content.Type);
        return content.Text;
    }

    private static async Task<string> ArgumentError(IToolHandler h, string? args)
    {
        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() => h.ExecuteAsync(Args(args), CancellationToken.None));
        var r = ErrorTranslator.Translate(ex, new StringWriter());
        Assert.True(r.IsError);
        Assert.StartsWith("Invalid arguments:", Assert.Single(r.Content).Text);
        return ex.Message;
    }

    private static int Tokens(string text) => (text.Length + 3) / 4;

    // ---- transcript_get ----

    [Fact]
    public async Task Get_ById_AnswerShape()
    {
        SeedSmall();
        var json = await Data(new TranscriptGetHandler(() => true, Service()), $"{{\"id\":{ChunkId(1)}}}");

        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Equal("claude-code:s1", json.GetProperty("session").GetString());
        Assert.Equal("owner/repo", json.GetProperty("project").GetString());
        Assert.Equal("interactive", json.GetProperty("kind").GetString());
        Assert.True(json.TryGetProperty("title", out var title));
        Assert.Equal(JsonValueKind.Null, title.ValueKind);
        Assert.Equal("1", json.GetProperty("requested").GetString());
        Assert.Equal(1, json.GetProperty("context").GetInt32());
        Assert.False(json.TryGetProperty("truncated", out _));
        Assert.False(json.TryGetProperty("nextFromExchange", out _));
        Assert.False(json.TryGetProperty("nextToExchange", out _));
        Assert.False(json.TryGetProperty("note", out _));

        var ex = json.GetProperty("exchanges").EnumerateArray().ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, ex.Select(e => e.GetProperty("n").GetInt32()).ToArray());
        Assert.Equal(new[] { false, true, false }, ex.Select(e => e.GetProperty("requested").GetBoolean()).ToArray());
        Assert.Equal(new[] { "body zero", "body one", "body two" }, ex.Select(e => e.GetProperty("text").GetString()).ToArray());
        Assert.Equal(new[] { false, true, false }, ex.Select(e => e.GetProperty("hasError").GetBoolean()).ToArray());
        Assert.All(ex, e => Assert.Equal("dialogue", e.GetProperty("chunkType").GetString()));
        Assert.Equal(ReadArchiveBuilder.Iso(T0.AddMinutes(1)), ex[1].GetProperty("ts").GetString());
        Assert.Equal(
            new[] { "n", "ts", "chunkType", "hasError", "requested", "text" },
            ex[1].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Tokens("body zero") + Tokens("body one") + Tokens("body two"), json.GetProperty("approxTokens").GetInt32());
    }

    [Fact]
    public async Task Get_BySessionRange_RequestedText()
    {
        SeedSmall();
        var json = await Data(new TranscriptGetHandler(() => true, Service()),
            "{\"session\":\"claude-code:s1\",\"from_exchange\":1,\"to_exchange\":2,\"context\":0,\"max_tokens\":2000.0}");
        Assert.Equal("1-2", json.GetProperty("requested").GetString());
        Assert.Equal(0, json.GetProperty("context").GetInt32());
        Assert.Equal(new[] { 1, 2 },
            json.GetProperty("exchanges").EnumerateArray().Select(e => e.GetProperty("n").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Get_Truncated_ShowsNextFromAndNextTo()
    {
        var body = new string('x', 600); // 150 tokens; two do not fit 200
        Seed(a =>
        {
            var s = a.Session("claude-code", "long", title: "Long");
            for (var n = 0; n < 5; n++) s.Exchange(n, body);
            s.Commit();
        });

        var json = await Data(new TranscriptGetHandler(() => true, Service()),
            "{\"session\":\"claude-code:long\",\"from_exchange\":0,\"to_exchange\":3,\"max_tokens\":200}");

        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, json.GetProperty("nextFromExchange").GetInt32());
        Assert.Equal(3, json.GetProperty("nextToExchange").GetInt32());
        Assert.Equal(new[] { 0 },
            json.GetProperty("exchanges").EnumerateArray().Select(e => e.GetProperty("n").GetInt32()).ToArray());
        Assert.Equal(150, json.GetProperty("approxTokens").GetInt32());
        Assert.Contains("from_exchange: 1, to_exchange: 3, context: 0", json.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Get_ErrorResult_ForUnknownSession()
    {
        SeedSmall();
        var h = new TranscriptGetHandler(() => true, Service());
        Assert.Equal("no archived session claude-code:nope; use transcript_sessions to list sessions",
            await ErrorText(h, "{\"session\":\"claude-code:nope\",\"from_exchange\":0}"));
        Assert.Equal("no archived chunk has id 999999 (it may have been forgotten); use transcript_sessions to list sessions",
            await ErrorText(h, "{\"id\":999999}"));
    }

    [Fact]
    public async Task Get_ErrorResult_ForAnAbsentArchive_CreatesNoFile()
    {
        var text = await ErrorText(new TranscriptGetHandler(() => true, Service()), "{\"session\":\"pi:x\",\"from_exchange\":0}");
        Assert.Equal(
            "no archived session pi:x; use transcript_sessions to list sessions (nothing is archived yet; run total-recall transcripts backfill)",
            text);
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public async Task Get_ErrorResult_WhenTheArchiveCannotBeOpened()
    {
        SeedSmall();
        // A separate archive file named as the main database: Open refuses it.
        var svc = new TranscriptReadService(() => new TranscriptReadContext(new TranscriptLocation(File, IsMainDatabase: true)));
        var text = await ErrorText(new TranscriptGetHandler(() => true, svc), "{\"id\":1}");
        Assert.StartsWith("the transcript archive could not be opened", text);
    }

    [Theory]
    [InlineData("{\"id\":1,\"context\":1.5}", "context")]
    [InlineData("{\"id\":1,\"context\":11}", "context")]
    [InlineData("{\"id\":1,\"max_tokens\":100}", "max_tokens")]
    [InlineData("{\"id\":1,\"max_tokens\":200.9}", "max_tokens")]
    [InlineData("{\"id\":0}", "id")]
    [InlineData("{\"id\":\"5\"}", "id")]
    [InlineData("{\"session\":\"claude-code:s1\",\"from_exchange\":\"0\"}", "from_exchange")]
    [InlineData("{\"session\":\"claude-code:s1\",\"from_exchange\":-1}", "from_exchange")]
    [InlineData("{\"session\":\"claude-code:s1\",\"from_exchange\":0,\"to_exchange\":\"2\"}", "to_exchange")]
    [InlineData("{\"session\":5,\"from_exchange\":0}", "session")]
    public async Task Get_Arguments_ErrorNamesTheArgument(string args, string name)
    {
        SeedSmall();
        var message = await ArgumentError(new TranscriptGetHandler(() => true, Service()), args);
        Assert.Contains(name, message);
    }

    [Fact]
    public async Task Get_Arguments_BothForms()
    {
        var message = await ArgumentError(new TranscriptGetHandler(() => true, Service()),
            "{\"id\":1,\"session\":\"claude-code:s1\",\"from_exchange\":0}");
        Assert.Contains("id", message);
        Assert.Contains("session", message);
        Assert.Contains("not both", message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData(null)]
    public async Task Get_Arguments_NeitherForm_EmptyNullOrMissing(string? args)
    {
        var message = await ArgumentError(new TranscriptGetHandler(() => true, Service()), args);
        Assert.Equal(
            "give either id (a chunk id from transcript_search) or session (<host>:<host_session_id>) with from_exchange",
            message);
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public async Task Get_Arguments_AnArray()
    {
        var message = await ArgumentError(new TranscriptGetHandler(() => true, Service()), "[1]");
        Assert.Contains("arguments", message);
    }

    // ---- transcript_sessions ----

    [Fact]
    public async Task Sessions_AnswerShape()
    {
        SeedSmall();
        var h = new TranscriptSessionsHandler(() => true, Service());

        var limited = await Data(h, "{\"limit\":1}");
        Assert.True(limited.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, limited.GetProperty("total").GetInt64());
        Assert.Equal(1, limited.GetProperty("more").GetInt64());
        Assert.Equal(1, limited.GetProperty("automatedHidden").GetInt64());
        Assert.Contains("narrow with project, since or before", limited.GetProperty("note").GetString());
        var top = Assert.Single(limited.GetProperty("sessions").EnumerateArray());
        Assert.Equal(
            new[] { "session", "project", "kind", "title", "startedAt", "endedAt", "exchanges", "chunks" },
            top.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("pi:p2", top.GetProperty("session").GetString());
        Assert.Equal("other/repo", top.GetProperty("project").GetString());
        Assert.Equal("interactive", top.GetProperty("kind").GetString());
        Assert.Equal("Second", top.GetProperty("title").GetString());
        Assert.Equal(ReadArchiveBuilder.Iso(T0.AddHours(1)), top.GetProperty("startedAt").GetString());
        Assert.Equal(ReadArchiveBuilder.Iso(T0.AddHours(2)), top.GetProperty("endedAt").GetString());
        Assert.Equal(1, top.GetProperty("exchanges").GetInt32());
        Assert.Equal(1, top.GetProperty("chunks").GetInt64());

        var all = await Data(h, "{\"include_automated\":true}");
        Assert.Equal(3, all.GetProperty("total").GetInt64());
        Assert.False(all.TryGetProperty("more", out _));
        Assert.False(all.TryGetProperty("automatedHidden", out _));
        Assert.False(all.TryGetProperty("note", out _));
        var rows = all.GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(new[] { "claude-code:auto", "pi:p2", "claude-code:s1" }, rows.Select(r => r.GetProperty("session").GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("startedAt").ValueKind);
        Assert.Equal(3, rows[2].GetProperty("exchanges").GetInt32());
    }

    [Fact]
    public async Task Sessions_FiltersReachTheService()
    {
        SeedSmall();
        var h = new TranscriptSessionsHandler(() => true, Service());
        var json = await Data(h, "{\"project\":\"owner/repo\",\"since\":\"2026-11-18\",\"before\":\"2026-11-18T09:30:00Z\"}");
        Assert.Equal(0, json.GetProperty("total").GetInt64());
        Assert.False(json.TryGetProperty("note", out _)); // a filter that matches nothing in a non-empty archive

        json = await Data(h, "{\"project\":\"owner/repo\",\"before\":\"2026-11-18T10:00:00.0000001Z\"}");
        Assert.Equal("claude-code:s1", Assert.Single(json.GetProperty("sessions").EnumerateArray()).GetProperty("session").GetString());

        // since alone (T0 + 1.5 h): only the session whose last activity is later, pi:p2 at T0 + 2 h
        json = await Data(h, "{\"since\":\"" + T0.AddMinutes(90).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "\"}");
        Assert.Equal("pi:p2", Assert.Single(json.GetProperty("sessions").EnumerateArray()).GetProperty("session").GetString());
        Assert.Equal(1, json.GetProperty("total").GetInt64());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Sessions_NoArguments_Lists(string? args)
    {
        SeedSmall();
        var json = await Data(new TranscriptSessionsHandler(() => true, Service()), args);
        Assert.Equal(2, json.GetProperty("total").GetInt64());
        Assert.Equal(2, json.GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public async Task Sessions_EmptyArchive_NoteAndNoSessions()
    {
        var json = await Data(new TranscriptSessionsHandler(() => true, Service()), null);
        Assert.Equal(0, json.GetProperty("total").GetInt64());
        Assert.Empty(json.GetProperty("sessions").EnumerateArray());
        Assert.Equal("nothing is archived yet; run total-recall transcripts backfill", json.GetProperty("note").GetString());
        Assert.False(System.IO.File.Exists(File));
    }

    [Theory]
    [InlineData("{\"limit\":0}", "limit")]
    [InlineData("{\"limit\":101}", "limit")]
    [InlineData("{\"limit\":1.5}", "limit")]
    [InlineData("{\"since\":\"yesterday\"}", "since")]
    [InlineData("{\"before\":\"yesterday\"}", "before")]
    [InlineData("{\"include_automated\":\"yes\"}", "include_automated")]
    [InlineData("{\"project\":5}", "project")]
    [InlineData("[]", "arguments")]
    public async Task Sessions_Arguments_ErrorNamesTheArgument(string args, string name)
    {
        SeedSmall();
        var message = await ArgumentError(new TranscriptSessionsHandler(() => true, Service()), args);
        Assert.Contains(name, message);
        if (name == "before") Assert.DoesNotContain("since", message);
    }

    // ---- the gate ----

    [Fact]
    public async Task Off_BothTools_AnswerOffText_AndOpenNoFile()
    {
        var get = new TranscriptGetHandler(() => false, Service());
        var sessions = new TranscriptSessionsHandler(() => false, Service());
        foreach (var (h, args) in new (IToolHandler, string?)[]
                 { (get, "{\"id\":1}"), (get, null), (sessions, null), (sessions, "{\"limit\":0}") })
        {
            var json = await Data(h, args);
            Assert.Equal(2, json.EnumerateObject().Count());
            Assert.False(json.GetProperty("enabled").GetBoolean());
            Assert.Equal(OffText, json.GetProperty("hint").GetString());
        }
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public async Task OnWithoutService_AnswerNotSetUpText()
    {
        foreach (IToolHandler h in new IToolHandler[] { new TranscriptGetHandler(() => true), new TranscriptSessionsHandler(() => true) })
        {
            var r = await h.ExecuteAsync(null, CancellationToken.None);
            Assert.NotEqual(true, r.IsError);
            Assert.Equal(
                "{\"enabled\":true,\"available\":false,\"hint\":\"" + NotSetUpText + "\"}",
                Assert.Single(r.Content).Text);
        }
    }

    [Fact]
    public async Task GateIsReadPerCall()
    {
        SeedSmall();
        var on = false;
        var calls = 0;
        var sessions = new TranscriptSessionsHandler(() => { calls++; return on; }, Service());
        var getCalls = 0;
        var get = new TranscriptGetHandler(() => { getCalls++; return on; }, Service());

        Assert.False((await Data(sessions, null)).GetProperty("enabled").GetBoolean());
        Assert.Equal(1, calls);
        on = true;
        Assert.Equal(2, (await Data(sessions, null)).GetProperty("total").GetInt64());
        Assert.Equal(2, calls);
        Assert.Equal(3, (await Data(get, $"{{\"id\":{ChunkId(1)}}}")).GetProperty("exchanges").GetArrayLength());
        Assert.Equal(1, getCalls);
        on = false;
        Assert.False((await Data(sessions, null)).GetProperty("enabled").GetBoolean());
        Assert.False((await Data(get, "{\"id\":1}")).GetProperty("enabled").GetBoolean());
        Assert.Equal(3, calls);
        Assert.Equal(2, getCalls);
    }
}
