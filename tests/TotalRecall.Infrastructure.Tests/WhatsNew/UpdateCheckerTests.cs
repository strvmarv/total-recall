// tests/TotalRecall.Infrastructure.Tests/WhatsNew/UpdateCheckerTests.cs
//
// UpdateChecker never reaches the network here: every checker gets a fake HttpMessageHandler, a fake
// clock and a fake delay, and every state directory is a temp directory. The few real-time tests
// (deadlines, Dispose) use a 100 ms request timeout and assert completion within 2 seconds.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public sealed class UpdateCheckerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly Uri Api = new("https://api.github.com/");

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "tr-updatecheck-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- doubles ---------------------------------------------------------------------------------

    private sealed class FakeClock
    {
        public DateTimeOffset Now;
        public readonly List<TimeSpan> Delays = new();
        public Action? OnDelay;
        public FakeClock(DateTimeOffset? start = null) { Now = start ?? T0; }

        public Task Delay(TimeSpan t, CancellationToken c)
        {
            c.ThrowIfCancellationRequested();
            Delays.Add(t);
            // A broken loop must fail the test, not spin it.
            if (Delays.Count > 5000) throw new InvalidOperationException("too many delays");
            Now += t;
            OnDelay?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed record Seen(
        HttpMethod Method, Uri? Uri, Dictionary<string, string[]> Headers, bool HasContent, DateTimeOffset At);

    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Func<DateTimeOffset> _now;
        private readonly object _gate = new();
        private readonly List<Seen> _requests = new();
        private readonly List<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _answers = new();
        private int _next;

        public FakeGitHub(FakeClock clock) { _now = () => clock.Now; }

        public Action? OnRequest;

        public List<Seen> Requests { get { lock (_gate) return _requests.ToList(); } }

        public FakeGitHub Then(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        {
            _answers.Add(answer);
            return this;
        }

        // A fresh response per request: the checker disposes each one, so one instance cannot be repeated.
        public FakeGitHub Then(HttpResponseMessage response)
        {
            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var status = response.StatusCode;
            var contentType = response.Content.Headers.ContentType;
            return Then((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(bytes) { Headers = { ContentType = contentType } },
            }));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer;
            lock (_gate)
            {
                var headers = new Dictionary<string, string[]>();
                foreach (var h in request.Headers) headers[h.Key] = h.Value.ToArray();
                // A broken loop must fail the test, not spin it.
                if (_requests.Count >= 500) throw new InvalidOperationException("too many requests");
                _requests.Add(new Seen(request.Method, request.RequestUri, headers, request.Content is not null, _now()));
                answer = _answers[Math.Min(_next, _answers.Count - 1)];
                _next++;
            }
            OnRequest?.Invoke();
            return answer(request, ct);
        }
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Release(
        string tag, bool prerelease = false, bool draft = false, string? body = "Faster sync.\n\nAdded\n- x") =>
        "{\"tag_name\":" + JsonSerializer.Serialize(tag)
        + ",\"prerelease\":" + (prerelease ? "true" : "false")
        + ",\"draft\":" + (draft ? "true" : "false")
        + ",\"body\":" + (body is null ? "null" : JsonSerializer.Serialize(body)) + "}";

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    private UpdateChecker Make(
        FakeGitHub gh, FakeClock clock, string version = "4.8.0", int interval = 24,
        TimeSpan? timeout = null, StringWriter? errors = null, string? dir = null,
        Func<DateTimeOffset>? now = null) =>
        new(version, dir ?? _dir, interval, gh, Api, now ?? (() => clock.Now), clock.Delay,
            errors ?? new StringWriter(), timeout);

    private async Task<(UpdateAttemptOutcome Outcome, LatestReleaseFile? File)> Attempt(
        string body, string version = "4.8.0", int status = 200)
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(status, body));
        var outcome = await Make(gh, clock, version).RunOnceAsync(CancellationToken.None);
        return (outcome, LatestReleaseState.Read(_dir, UpdateChannel.Of(version)));
    }

    private string RawFile(string channel = "stable") =>
        File.ReadAllText(Path.Combine(_dir, LatestReleaseState.FileName(channel)));

    private void Prior(string version, string summary, TimeSpan checkedAgo, TimeSpan attemptAgo, string channel = "stable") =>
        LatestReleaseState.Write(_dir, channel, new LatestReleaseFile(version, summary, T0 - checkedAgo, T0 - attemptAgo));

    // Stops a loop after the n-th request: the next delay cancels it.
    private static CancellationTokenSource StopAfterRequests(FakeClock clock, FakeGitHub gh, int n)
    {
        var cts = new CancellationTokenSource();
        clock.OnDelay = () => { if (gh.Requests.Count >= n) cts.Cancel(); };
        return cts;
    }

    // --- request (AC2) ---------------------------------------------------------------------------

    [Fact]
    public async Task Request_Stable_GoesToReleasesLatest_WithOnlyUserAgentAndAccept()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0")));

        await Make(gh, clock).RunOnceAsync(CancellationToken.None);

        var r = Assert.Single(gh.Requests);
        Assert.Equal(HttpMethod.Get, r.Method);
        Assert.Equal("https://api.github.com/repos/strvmarv/total-recall/releases/latest", r.Uri!.ToString());
        Assert.Equal(new[] { "Accept", "User-Agent" }, r.Headers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "total-recall/4.8.0" }, r.Headers["User-Agent"]);
        Assert.Equal(new[] { "application/vnd.github+json" }, r.Headers["Accept"]);
        Assert.False(r.HasContent);
    }

    [Fact]
    public async Task Request_Prerelease_GoesToReleasesListOf10()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, List(Release("v4.9.0-beta.1", prerelease: true))));

        await Make(gh, clock, "4.8.0-beta.1").RunOnceAsync(CancellationToken.None);

        var r = Assert.Single(gh.Requests);
        Assert.Equal("https://api.github.com/repos/strvmarv/total-recall/releases?per_page=10", r.Uri!.ToString());
        Assert.Equal(new[] { "total-recall/4.8.0-beta.1" }, r.Headers["User-Agent"]);
    }

    [Fact]
    public async Task Request_UserAgent_UsesBareVersion()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0")));

        await Make(gh, clock, "4.8.0+abc").RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "total-recall/4.8.0" }, Assert.Single(gh.Requests).Headers["User-Agent"]);
        Assert.Contains("/releases/latest", Assert.Single(gh.Requests).Uri!.ToString());
    }

    [Fact]
    public async Task Start_NoBaseAddress_TalksToApiGithubCom()
    {
        var clock = new FakeClock();
        var gotRequest = new TaskCompletionSource();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0")));
        gh.OnRequest = () => gotRequest.TrySetResult();

        using var run = UpdateChecker.Start("4.8.0", _dir, true, true, 24, () => null,
            gh, baseAddress: null, now: () => clock.Now, delay: ParkAfter(clock, gh, 1),
            errors: new StringWriter());
        Assert.NotNull(run);
        await gotRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run!.Dispose();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("api.github.com", gh.Requests[0].Uri!.Host);
    }

    // A fake delay that advances the clock, and after n requests parks until cancelled, so a started
    // loop neither spins through simulated time nor outlives its Dispose.
    private static Func<TimeSpan, CancellationToken, Task> ParkAfter(FakeClock clock, FakeGitHub gh, int n) =>
        (t, c) => gh.Requests.Count >= n ? Task.Delay(Timeout.Infinite, c) : clock.Delay(t, c);

    [Fact]
    public void CreateHandler_DisablesRedirectsAndCookies()
    {
        using var h = UpdateChecker.CreateHandler();
        Assert.False(h.AllowAutoRedirect);
        Assert.False(h.UseCookies);
    }

    // --- selection (AC3) -------------------------------------------------------------------------

    [Fact]
    public async Task Stable_ObjectRelease_RecordsVersionAndSummary()
    {
        var (outcome, file) = await Attempt(Release("v4.9.0"));

        Assert.Equal(UpdateAttemptOutcome.Answered, outcome);
        Assert.Equal("4.9.0", file!.Version);
        Assert.Equal("Faster sync.", file.Summary);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Stable_PrereleaseOrDraftObject_IsFailedAttempt(bool prerelease, bool draft)
    {
        var (outcome, file) = await Attempt(Release("v4.9.0", prerelease, draft));

        Assert.Equal(UpdateAttemptOutcome.Failed, outcome);
        Assert.Null(file!.Version);
    }

    [Fact]
    public async Task Stable_TagWithPrereleasePartButFlagFalse_NotCounted()
    {
        var (outcome, _) = await Attempt(Release("v4.9.0-rc.1", prerelease: false));

        Assert.Equal(UpdateAttemptOutcome.Failed, outcome);
    }

    [Fact]
    public async Task Prerelease_List_TakesGreatestNonDraft()
    {
        var list = List(
            Release("v4.9.0-beta.2", prerelease: true),
            Release("v4.8.0"),
            Release("v5.0.0", draft: true),
            Release("v4.10.0-rc.1+b", prerelease: true));
        var (_, file) = await Attempt(list, "4.8.0-beta.1");
        Assert.Equal("4.9.0-beta.2", file!.Version);

        var withStable = List(
            Release("v4.9.0-beta.2", prerelease: true),
            Release("v4.9.0"),
            Release("v4.8.0"));
        var (_, file2) = await Attempt(withStable, "4.8.0-beta.1");
        Assert.Equal("4.9.0", file2!.Version);
    }

    [Theory]
    [InlineData("4.9.0")]
    [InlineData("v4.9.0+b")]
    [InlineData("vx")]
    [InlineData("v")]
    [InlineData("LONG65")]
    [InlineData("NUMBER")]
    public async Task Prerelease_List_InvalidTagsSkipped(string spec)
    {
        string bad = spec switch
        {
            "LONG65" => Release("v1.0.0-" + new string('a', 65 - 6)),
            "NUMBER" => "{\"tag_name\":5}",
            _ => Release(spec),
        };

        var (outcome, file) = await Attempt(List(bad, Release("v4.7.0", prerelease: true)), "4.7.0-beta.1");

        Assert.Equal(UpdateAttemptOutcome.Answered, outcome);
        Assert.Equal("4.7.0", file!.Version);
    }

    [Fact]
    public async Task Answer_NoCountingRelease_IsFailedAttempt()
    {
        var (outcome, _) = await Attempt("[]", "4.8.0-beta.1");
        Assert.Equal(UpdateAttemptOutcome.Failed, outcome);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("\"x\"")]
    [InlineData("42")]
    [InlineData("{}")]
    public async Task Answer_NotJsonOrWrongShape_IsFailed(string body)
    {
        var (outcome, _) = await Attempt(body);
        Assert.Equal(UpdateAttemptOutcome.Failed, outcome);
    }

    [Fact]
    public async Task Answer_InvalidUtf8_IsFailedNotUnexpected()
    {
        var clock = new FakeClock();
        var bytes = Encoding.UTF8.GetBytes("{\"tag_name\":\"v4.9.0\",\"body\":\"x").Concat(new byte[] { 0xC3, 0x28 })
            .Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
        var gh = new FakeGitHub(clock).Then(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        Assert.Equal(UpdateAttemptOutcome.Failed, await Make(gh, clock).RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Answer_LoneSurrogateEscape_IsFailedNotUnexpected()
    {
        const string body = "[{\"tag_name\":\"\\ud800\",\"draft\":false,\"prerelease\":false}]";
        var channel = UpdateChannel.Of("4.8.0-beta.1");
        Prior("4.9.0", "Old.", TimeSpan.FromHours(5), TimeSpan.FromHours(4), channel);

        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, body));
        var outcome = await Make(gh, clock, "4.8.0-beta.1").RunOnceAsync(CancellationToken.None);

        Assert.Equal(UpdateAttemptOutcome.Failed, outcome);
        Assert.Equal(new LatestReleaseFile("4.9.0", "Old.", T0 - TimeSpan.FromHours(5), T0),
            LatestReleaseState.Read(_dir, channel));

        var clock2 = new FakeClock();
        var gh2 = new FakeGitHub(clock2).Then(Json(200, body));
        var errors = new StringWriter();
        var cts = StopAfterRequests(clock2, gh2, 2);
        await Make(gh2, clock2, "4.8.0-beta.1", errors: errors).RunLoopAsync(cts.Token);

        Assert.Equal(2, gh2.Requests.Count);
        Assert.Equal("", errors.ToString());
    }

    [Fact]
    public async Task Answer_LoneSurrogateInBody_StillCountsRelease_NoSummary()
    {
        var (outcome, _) = await Attempt("{\"tag_name\":\"v4.9.0\",\"body\":\"\\ud800 text\"}");

        Assert.Equal(UpdateAttemptOutcome.Answered, outcome);
        using var doc = JsonDocument.Parse(RawFile());
        Assert.Equal("4.9.0", doc.RootElement.GetProperty("version").GetString());
        Assert.False(doc.RootElement.TryGetProperty("summary", out _));
    }

    // --- summary (AC5) ---------------------------------------------------------------------------

    [Theory]
    [InlineData("A\r\n\r\nB", "A")]
    [InlineData("A\rB\r\rC", "A\nB")]
    [InlineData("\n\n  \nA\nB\n\nC", "A\nB")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("  \n \n", null)]
    public void FirstParagraph_Cases(string? body, string? expected)
    {
        Assert.Equal(expected, UpdateChecker.FirstParagraph(body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Summary_NullOrEmptyBody_NoSummaryKey(string? body)
    {
        var (outcome, _) = await Attempt(Release("v4.9.0", body: body));

        Assert.Equal(UpdateAttemptOutcome.Answered, outcome);
        using var doc = JsonDocument.Parse(RawFile());
        Assert.False(doc.RootElement.TryGetProperty("summary", out _));
        Assert.Equal("4.9.0", doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Summary_NonStringBody_NoSummaryKey()
    {
        var (outcome, _) = await Attempt("{\"tag_name\":\"v4.9.0\",\"body\":12}");

        Assert.Equal(UpdateAttemptOutcome.Answered, outcome);
        using var doc = JsonDocument.Parse(RawFile());
        Assert.False(doc.RootElement.TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task Summary_Sanitised_MarkdownAnsiControl()
    {
        var (_, file) = await Attempt(Release("v4.9.0",
            body: "\u001b[31mFixed\u001b[0m [the cache](https://x.y) bug\u0007\n\nMore"));

        Assert.Equal("Fixed the cache bug", file!.Summary);
    }

    [Fact]
    public async Task Summary_VeryLongParagraph_AtMost200()
    {
        var (_, file) = await Attempt(Release("v4.9.0", body: string.Join(" ", Enumerable.Repeat("word", 2500))));

        Assert.NotNull(file!.Summary);
        Assert.True(file.Summary!.Length <= 200);
    }

    [Fact]
    public async Task Answer_HtmlUrlIgnored_FileHasOnlyFourKeys()
    {
        var body = "{\"tag_name\":\"v4.9.0\",\"html_url\":\"https://evil.example/x\",\"url\":\"https://evil.example/y\","
                   + "\"body\":\"Hi.\",\"assets\":[{\"browser_download_url\":\"https://evil.example/z\"}]}";
        await Attempt(body);

        var raw = RawFile();
        Assert.DoesNotContain("evil.example", raw);
        using var doc = JsonDocument.Parse(raw);
        var allowed = new HashSet<string> { "version", "summary", "checkedAt", "lastAttempt" };
        foreach (var p in doc.RootElement.EnumerateObject()) Assert.Contains(p.Name, allowed);
    }

    // --- outcomes (AC4) --------------------------------------------------------------------------

    [Fact]
    public async Task Outcome200_WritesVersionSummaryAndBothTimes()
    {
        var (outcome, file) = await Attempt(Release("v4.9.0"));

        Assert.Equal(UpdateAttemptOutcome.Answered, outcome);
        Assert.Equal(new LatestReleaseFile("4.9.0", "Faster sync.", T0, T0), file);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public async Task Outcome403Or429_KeepsVersionSummary_SetsBothTimes(int status)
    {
        Prior("4.9.0", "Old.", TimeSpan.FromHours(5), TimeSpan.FromHours(5));
        var (outcome, file) = await Attempt("{\"message\":\"rate limit\"}", status: status);

        Assert.Equal(UpdateAttemptOutcome.RateLimited, outcome);
        Assert.Equal(new LatestReleaseFile("4.9.0", "Old.", T0, T0), file);
    }

    [Fact]
    public async Task Outcome403_NoPriorFile_WritesOnlyTimes()
    {
        var (outcome, _) = await Attempt("{}", status: 403);

        Assert.Equal(UpdateAttemptOutcome.RateLimited, outcome);
        using var doc = JsonDocument.Parse(RawFile());
        Assert.Equal(new[] { "checkedAt", "lastAttempt" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> FailureKind(string kind) => kind switch
    {
        "500" => (_, _) => Task.FromResult(Json(500, "oops")),
        "404" => (_, _) => Task.FromResult(Json(404, "{}")),
        "301" => (_, _) => Task.FromResult(Json(301, "")),
        "302" => (_, _) =>
        {
            var r = Json(302, "");
            r.Headers.Location = new Uri("https://evil.example/");
            return Task.FromResult(r);
        },
        "offline" => (_, _) => throw new HttpRequestException("offline"),
        "oversize" => (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1_048_577]) }),
        "oversize-stream" => (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ZeroStream(1_048_577)) }),
        "contentlength" => (_, _) =>
        {
            var r = Json(200, Release("v4.9.0"));
            r.Content.Headers.ContentLength = 2_000_000;
            return Task.FromResult(r);
        },
        "io" => (_, _) => throw new IOException("reset"),
        _ => throw new ArgumentException(kind),
    };

    [Theory]
    [InlineData("500")]
    [InlineData("404")]
    [InlineData("301")]
    [InlineData("302")]
    [InlineData("offline")]
    [InlineData("oversize")]
    [InlineData("oversize-stream")]
    [InlineData("contentlength")]
    [InlineData("io")]
    public async Task OutcomeFailure_ChangesOnlyLastAttempt(string kind)
    {
        Prior("4.9.0", "Old.", TimeSpan.FromHours(5), TimeSpan.FromHours(4));
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(FailureKind(kind));

        var outcome = await Make(gh, clock).RunOnceAsync(CancellationToken.None);

        Assert.Equal(UpdateAttemptOutcome.Failed, outcome);
        Assert.Equal(new LatestReleaseFile("4.9.0", "Old.", T0 - TimeSpan.FromHours(5), T0),
            LatestReleaseState.Read(_dir, "stable"));
    }

    [Fact]
    public async Task OutcomeFailure_NoPriorFile_WritesOnlyLastAttempt()
    {
        var (outcome, _) = await Attempt("oops", status: 500);

        Assert.Equal(UpdateAttemptOutcome.Failed, outcome);
        using var doc = JsonDocument.Parse(RawFile());
        Assert.Equal(new[] { "lastAttempt" }, doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Timeout_SlowHeaders_FailsWithinDeadline()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(200, "{}");
        });
        var sw = Stopwatch.StartNew();

        var outcome = await Make(gh, clock, timeout: TimeSpan.FromMilliseconds(100)).RunOnceAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpdateAttemptOutcome.Failed, outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), sw.Elapsed.ToString());
        Assert.Equal(T0, LatestReleaseState.Read(_dir, "stable")!.LastAttempt);
    }

    [Fact]
    public async Task Timeout_SlowBody_FailsWithinDeadline()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) }));
        var sw = Stopwatch.StartNew();

        var outcome = await Make(gh, clock, timeout: TimeSpan.FromMilliseconds(100)).RunOnceAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpdateAttemptOutcome.Failed, outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task ExpectedFailures_WriteNothingToErrors()
    {
        var errors = new StringWriter();
        foreach (var kind in new[] { "500", "404", "301", "302", "offline", "oversize", "oversize-stream", "contentlength", "io" })
        {
            var clock = new FakeClock();
            var gh = new FakeGitHub(clock).Then(FailureKind(kind));
            await Make(gh, clock, errors: errors).RunOnceAsync(CancellationToken.None);
        }
        foreach (var body in new[] { "not json", "[]", "{}" })
        {
            var clock = new FakeClock();
            var gh = new FakeGitHub(clock).Then(Json(200, body));
            await Make(gh, clock, errors: errors).RunOnceAsync(CancellationToken.None);
        }
        {
            var clock = new FakeClock();
            var gh = new FakeGitHub(clock).Then(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(200, "{}"); });
            await Make(gh, clock, errors: errors, timeout: TimeSpan.FromMilliseconds(50)).RunOnceAsync(CancellationToken.None);
        }

        Assert.Equal("", errors.ToString());
    }

    [Fact]
    public async Task ShutdownCancellation_WritesNothing()
    {
        Prior("4.9.0", "Old.", TimeSpan.FromHours(5), TimeSpan.FromHours(5));
        var before = RawFile();
        var clock = new FakeClock();
        var started = new TaskCompletionSource();
        var gh = new FakeGitHub(clock).Then(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Json(200, "{}");
        });
        using var cts = new CancellationTokenSource();

        var attempt = Make(gh, clock).RunOnceAsync(cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Equal(before, RawFile());
    }

    // --- schedule (AC1, AC4) ---------------------------------------------------------------------

    [Fact]
    public void NextDue_NullFile_IsMinValue()
    {
        Assert.Equal(DateTimeOffset.MinValue, UpdateChecker.NextDue(null, T0, 24));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(-23.0, null, 1.0)]       // checkedAt 23 h ago: due in 1 h
    [InlineData(-25.0, -0.5, 0.5)]       // answer overdue, but the last attempt was 30 min ago
    [InlineData(48.0, null, null)]       // checkedAt 2 days ahead: missing
    [InlineData(null, 25.0, null)]       // lastAttempt 25 h ahead: missing
    [InlineData(12.0, null, 36.0)]       // checkedAt 12 h ahead: kept
    [InlineData(-30.0, -29.0, -6.0)]     // both overdue: the later (answer + 24 h = -6 h)
    public void NextDue_Cases(double? checkedHours, double? attemptHours, double? dueHours)
    {
        var file = new LatestReleaseFile("4.9.0", null,
            checkedHours is { } c ? T0 + TimeSpan.FromHours(c) : null,
            attemptHours is { } a ? T0 + TimeSpan.FromHours(a) : null);

        var due = UpdateChecker.NextDue(file, T0, 24);

        if (dueHours is null) Assert.True(due <= T0 - TimeSpan.FromDays(365 * 100));
        else Assert.Equal(T0 + TimeSpan.FromHours(dueHours.Value), due);
    }

    [Fact]
    public void NextDue_IntervalIsNormalised()
    {
        var file = new LatestReleaseFile("4.9.0", null, T0 - TimeSpan.FromHours(1), null);
        Assert.Equal(T0 - TimeSpan.FromHours(1) + TimeSpan.FromHours(24), UpdateChecker.NextDue(file, T0, 0));
        Assert.Equal(T0 - TimeSpan.FromHours(1) + TimeSpan.FromHours(24), UpdateChecker.NextDue(file, T0, int.MaxValue));
    }

    [Fact]
    public void MergeWithMemory_FutureFileTime_IsDroppedBeforeMerging()
    {
        var file = new LatestReleaseFile("4.9.0", "S", T0 + TimeSpan.FromDays(3), T0 + TimeSpan.FromHours(30));

        var merged = UpdateChecker.MergeWithMemory(file, T0 - TimeSpan.FromHours(2), null, T0);

        Assert.Equal(new LatestReleaseFile("4.9.0", "S", T0 - TimeSpan.FromHours(2), null), merged);
    }

    [Fact]
    public void MergeWithMemory_FutureMemoryTime_IsDropped()
    {
        var file = new LatestReleaseFile("4.9.0", "S", T0 - TimeSpan.FromHours(5), T0 - TimeSpan.FromHours(1));

        var merged = UpdateChecker.MergeWithMemory(file, T0 + TimeSpan.FromDays(3), T0 + TimeSpan.FromHours(30), T0);

        Assert.Equal(file, merged);
        Assert.Null(UpdateChecker.MergeWithMemory(null, T0 + TimeSpan.FromDays(3), T0 + TimeSpan.FromDays(3), T0));
    }

    [Fact]
    public void MergeWithMemory_TakesTheLaterOfFileAndMemory()
    {
        var file = new LatestReleaseFile("4.9.0", "S", T0 - TimeSpan.FromHours(5), T0 - TimeSpan.FromHours(1));

        var merged = UpdateChecker.MergeWithMemory(file, T0 - TimeSpan.FromHours(2), T0 - TimeSpan.FromHours(3), T0);

        Assert.Equal(new LatestReleaseFile("4.9.0", "S", T0 - TimeSpan.FromHours(2), T0 - TimeSpan.FromHours(1)), merged);
        Assert.Null(UpdateChecker.MergeWithMemory(null, null, null, T0));
        Assert.Equal(new LatestReleaseFile(null, null, T0, T0), UpdateChecker.MergeWithMemory(null, T0, T0, T0));
    }

    [Fact]
    public async Task Loop_FirstRequest_NotBeforeSixtySeconds()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0")));
        var requestsAtFirstDelay = -1;
        var cts = StopAfterRequests(clock, gh, 1);
        var stop = clock.OnDelay;
        clock.OnDelay = () => { if (requestsAtFirstDelay < 0) requestsAtFirstDelay = gh.Requests.Count; stop!(); };

        await Make(gh, clock).RunLoopAsync(cts.Token);

        Assert.Equal(TimeSpan.FromSeconds(60), clock.Delays[0]);
        Assert.Equal(0, requestsAtFirstDelay);
        Assert.Equal(T0 + TimeSpan.FromSeconds(60), gh.Requests[0].At);
    }

    [Fact]
    public async Task Loop_RestartWithinInterval_DoesNotRefetch()
    {
        var clockA = new FakeClock();
        var ghA = new FakeGitHub(clockA).Then(Json(200, Release("v4.9.0")));
        var ctsA = StopAfterRequests(clockA, ghA, 1);
        await Make(ghA, clockA).RunLoopAsync(ctsA.Token);
        Assert.Single(ghA.Requests);

        var clockB = new FakeClock(T0 + TimeSpan.FromHours(2));
        var ghB = new FakeGitHub(clockB).Then(Json(200, Release("v4.9.0")));
        var ctsB = new CancellationTokenSource();
        clockB.OnDelay = () => { if (clockB.Now > T0 + TimeSpan.FromDays(3)) ctsB.Cancel(); };
        await Make(ghB, clockB).RunLoopAsync(ctsB.Token);

        var first = T0 + TimeSpan.FromSeconds(60);
        Assert.Equal(
            new[] { first + TimeSpan.FromHours(24), first + TimeSpan.FromHours(48) },
            ghB.Requests.Select(r => r.At).ToArray());
    }

    [Fact]
    public async Task Loop_FailedAttempt_RetriesNoSoonerThanOneHour()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(500, "oops"));
        var cts = StopAfterRequests(clock, gh, 4);

        await Make(gh, clock).RunLoopAsync(cts.Token);

        var times = gh.Requests.Select(r => r.At).ToArray();
        Assert.Equal(4, times.Length);
        Assert.Equal(T0 + TimeSpan.FromSeconds(60), times[0]);
        for (var i = 1; i < times.Length; i++) Assert.True(times[i] - times[i - 1] >= TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Loop_RateLimited_WaitsFullInterval()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(403, "{}")).Then(Json(200, Release("v4.9.0")));
        var cts = StopAfterRequests(clock, gh, 2);

        await Make(gh, clock).RunLoopAsync(cts.Token);

        var times = gh.Requests.Select(r => r.At).ToArray();
        Assert.Equal(2, times.Length);
        Assert.True(times[1] - times[0] >= TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task Loop_SleepsAtMost24HoursPerStep()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0")));
        var cts = StopAfterRequests(clock, gh, 2);

        await Make(gh, clock, interval: 8760).RunLoopAsync(cts.Token);

        Assert.All(clock.Delays, d => Assert.True(d <= TimeSpan.FromHours(24), d.ToString()));
        var times = gh.Requests.Select(r => r.At).ToArray();
        Assert.Equal(2, times.Length);
        Assert.Equal(TimeSpan.FromHours(8760), times[1] - times[0]);
    }

    [Fact]
    public async Task Loop_LongSession_ChecksAgainAfterInterval()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0"))).Then(Json(200, Release("v4.10.0")));
        var cts = StopAfterRequests(clock, gh, 2);

        await Make(gh, clock).RunLoopAsync(cts.Token);

        var times = gh.Requests.Select(r => r.At).ToArray();
        Assert.Equal(TimeSpan.FromHours(24), times[1] - times[0]);
        Assert.Equal("4.10.0", LatestReleaseState.Read(_dir, "stable")!.Version);
    }

    [Fact]
    public async Task Loop_FutureCheckedAt_DoesNotStopChecks()
    {
        LatestReleaseState.Write(_dir, "stable",
            new LatestReleaseFile("4.9.0", "S", T0 + TimeSpan.FromDays(3), T0 + TimeSpan.FromDays(3)));
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.1")));
        var cts = StopAfterRequests(clock, gh, 1);

        await Make(gh, clock).RunLoopAsync(cts.Token);

        Assert.Equal(T0 + TimeSpan.FromSeconds(60), Assert.Single(gh.Requests).At);
    }

    [Theory]
    [InlineData(500, 1)]
    [InlineData(200, 24)]
    public async Task Loop_UnwritableStateDir_DoesNotHotLoop(int status, int minHours)
    {
        Directory.CreateDirectory(_dir);
        var notADir = Path.Combine(_dir, "file-not-dir");
        File.WriteAllText(notADir, "x");
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(status, status == 200 ? Release("v4.9.0") : "oops"));
        var cts = StopAfterRequests(clock, gh, 4);

        await Make(gh, clock, dir: notADir).RunLoopAsync(cts.Token);

        var times = gh.Requests.Select(r => r.At).ToArray();
        Assert.Equal(4, times.Length);
        for (var i = 1; i < times.Length; i++)
            Assert.True(times[i] - times[i - 1] >= TimeSpan.FromHours(minHours), (times[i] - times[i - 1]).ToString());
    }

    [Fact]
    public async Task Loop_UnexpectedException_OneStderrLine_TaskCompletes()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0")));
        var errors = new StringWriter();
        var calls = 0;
        var checker = Make(gh, clock, errors: errors, now: () =>
        {
            if (++calls == 2) throw new InvalidOperationException("boom");
            return clock.Now;
        });

        await checker.RunLoopAsync(CancellationToken.None);

        var lines = errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = Assert.Single(lines);
        Assert.StartsWith("[total-recall] update check stopped:", line);
        Assert.Contains("InvalidOperationException", line);
    }

    [Fact]
    public async Task Loop_Cancelled_EndsQuietly()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0")));
        var errors = new StringWriter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Make(gh, clock, errors: errors).RunLoopAsync(cts.Token);

        Assert.Equal("", errors.ToString());
        Assert.Empty(gh.Requests);
    }

    // --- Start factory (AC6, AC7) ----------------------------------------------------------------

    [Theory]
    [InlineData("1.0.0", true, true, null)]
    [InlineData("unknown", true, true, null)]
    [InlineData("1.0.0+abc", true, true, null)]
    [InlineData(null, true, true, null)]
    [InlineData("4.8.0", false, true, null)]
    [InlineData("4.8.0", true, false, null)]
    [InlineData("4.8.0", true, true, "1")]
    [InlineData("4.8.0", true, true, "true")]
    [InlineData("4.8.0", true, true, "yes")]
    [InlineData("4.8.0", true, true, " x ")]
    public void Start_Off_ReturnsNull_NoRequest(string? version, bool notice, bool check, string? env)
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0")));

        var run = UpdateChecker.Start(version, _dir, notice, check, 24, () => env,
            gh, now: () => clock.Now, delay: clock.Delay, errors: new StringWriter());

        Assert.Null(run);
        Assert.Empty(gh.Requests);
        Assert.Empty(clock.Delays);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("False")]
    public async Task Start_EnvEmptyZeroFalse_Starts(string? env)
    {
        var clock = new FakeClock();
        var gotRequest = new TaskCompletionSource();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0")));
        gh.OnRequest = () => gotRequest.TrySetResult();

        var run = UpdateChecker.Start("4.8.0", _dir, true, true, 24, () => env,
            gh, now: () => clock.Now, delay: ParkAfter(clock, gh, 1), errors: new StringWriter());

        Assert.NotNull(run);
        await gotRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run!.Dispose();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(gh.Requests);
    }

    [Fact]
    public async Task Start_IntervalOutOfRange_Uses24()
    {
        var clock = new FakeClock();
        var second = new TaskCompletionSource();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0")));
        gh.OnRequest = () => { if (gh.Requests.Count >= 2) second.TrySetResult(); };

        var run = UpdateChecker.Start("4.8.0", _dir, true, true, 0, () => null,
            gh, now: () => clock.Now, delay: ParkAfter(clock, gh, 2), errors: new StringWriter());

        Assert.NotNull(run);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run!.Dispose();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        var times = gh.Requests.Select(r => r.At).ToArray();
        Assert.Equal(TimeSpan.FromHours(24), times[1] - times[0]);
    }

    [Fact]
    public async Task Start_Dispose_CancelsLoop()
    {
        var clock = new FakeClock();
        var gh = new FakeGitHub(clock).Then(Json(200, Release("v4.9.0")));

        var run = UpdateChecker.Start("4.8.0", _dir, true, true, 24, () => null, gh, errors: new StringWriter());

        Assert.NotNull(run);
        run!.Dispose();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(gh.Requests);
        run.Dispose(); // a second Dispose is harmless
    }

    // --- stream doubles --------------------------------------------------------------------------

    private abstract class ReadOnlyTestStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Blocks in ReadAsync until the token is cancelled.
    private sealed class BlockingStream : ReadOnlyTestStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    // A non-seekable stream of n zero bytes, so no Content-Length is known.
    private sealed class ZeroStream : ReadOnlyTestStream
    {
        private long _left;
        public ZeroStream(long n) { _left = n; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _left);
            Array.Clear(buffer, offset, n);
            _left -= n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = (int)Math.Min(buffer.Length, _left);
            buffer.Span[..n].Clear();
            _left -= n;
            return new ValueTask<int>(n);
        }
    }
}
