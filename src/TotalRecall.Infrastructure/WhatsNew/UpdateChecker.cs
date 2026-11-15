// src/TotalRecall.Infrastructure/WhatsNew/UpdateChecker.cs
//
// The background update check of the MCP engine: asks GitHub for the newest release of the channel
// the running version belongs to and records the answer in <dataDir>/whats-new/latest-<channel>.json
// (LatestReleaseState). It is started from exactly one place, Program.RunServeAsync, never from the
// composition root (`ui`, `dump-catalog` and tests reach that too), and it never touches the MCP
// handshake or session_start: nothing here waits on the network on a caller's path.
//
// Request: an unauthenticated GET with a User-Agent of total-recall/<bare version> and an Accept
// header, nothing else; redirects are off. One 3 second deadline covers sending and reading, the
// answer is capped at 1 MB and parsed with JsonDocument (no reflection). What GitHub returns is
// data: only a tag that passes UpdateChannel.IsValidReleaseVersion and the sanitised first
// paragraph of its body are kept; html_url and every other field are ignored.
//
// Schedule: first request 60 seconds after start, then one request per interval (24 hours by
// default) once an answer (or a 403/429) was recorded, and one per hour after a failure. The last
// attempt and answer times are also kept in memory and the later of memory and file wins, so a state
// directory that cannot be written does not turn the loop into a hot loop. A time in the file more
// than a day in the future counts as missing, and no single sleep exceeds 24 hours.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Infrastructure.WhatsNew;

public enum UpdateAttemptOutcome { Answered, RateLimited, Failed }

/// <summary>The running background loop. Dispose cancels it and does not wait.</summary>
public sealed class UpdateCheckerRun : IDisposable
{
    private readonly CancellationTokenSource _cts;

    internal UpdateCheckerRun(Task completion, CancellationTokenSource cts)
    {
        Completion = completion;
        _cts = cts;
    }

    public Task Completion { get; }

    public void Dispose()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

public sealed class UpdateChecker
{
    public const string DefaultBaseAddress = "https://api.github.com/";
    public const string StablePath = "repos/strvmarv/total-recall/releases/latest";
    public const string PrereleasePath = "repos/strvmarv/total-recall/releases?per_page=10";
    public const int MaxResponseBytes = 1_048_576;

    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxSleep = TimeSpan.FromHours(24);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);

    private readonly string _version;
    private readonly string _channel;
    private readonly string _stateDir;
    private readonly int _intervalHours;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TextWriter _errors;
    private readonly TimeSpan? _requestTimeout;

    // The last attempt and answer this process made, kept apart from the file: a file that cannot be
    // written must not make the loop forget it just asked.
    private DateTimeOffset? _lastAnswer;
    private DateTimeOffset? _lastAttempt;

    public UpdateChecker(
        string version,
        string stateDir,
        int intervalHours,
        HttpMessageHandler handler,
        Uri baseAddress,
        Func<DateTimeOffset> now,
        Func<TimeSpan, CancellationToken, Task> delay,
        TextWriter errors,
        TimeSpan? requestTimeout = null)
    {
        var plus = version.IndexOf('+');
        _version = plus >= 0 ? version[..plus] : version;
        _channel = UpdateChannel.Of(_version);
        _stateDir = stateDir;
        _intervalHours = UpdateSettings.NormalizeIntervalHours(intervalHours);
        _http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _now = now;
        _delay = delay;
        _errors = errors;
        _requestTimeout = requestTimeout;
    }

    public static SocketsHttpHandler CreateHandler() =>
        new() { AllowAutoRedirect = false, UseCookies = false };

    /// <summary>
    /// Starts the background loop, or returns null when the check is off (a build with no real
    /// version, the settings, or the environment variable) or cannot be started. Never throws.
    /// </summary>
    public static UpdateCheckerRun? Start(
        string? version,
        string stateDir,
        bool noticeEnabled,
        bool checkEnabled,
        int intervalHours,
        Func<string?> noUpdateCheckEnv,
        HttpMessageHandler? handler = null,
        Uri? baseAddress = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TextWriter? errors = null)
    {
        var sink = errors ?? Console.Error;
        try
        {
            if (version is null || SemVer.IsDevBuild(version)) return null;
            if (!UpdateSettings.CheckOn(noticeEnabled, checkEnabled, noUpdateCheckEnv())) return null;

            var checker = new UpdateChecker(
                version, stateDir, intervalHours,
                handler ?? CreateHandler(),
                baseAddress ?? new Uri(DefaultBaseAddress),
                now ?? (() => DateTimeOffset.UtcNow),
                delay ?? ((t, c) => Task.Delay(t, c)),
                sink);

            var cts = new CancellationTokenSource();
            try
            {
                var token = cts.Token;
                var task = Task.Run(() => checker.RunLoopAsync(token));
                return new UpdateCheckerRun(task, cts);
            }
            catch
            {
                cts.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            try { sink.WriteLine("[total-recall] update check not started: " + ex.GetType().Name + ": " + ex.Message); }
            catch { }
            return null;
        }
    }

    // --- one attempt -----------------------------------------------------------------------------

    public async Task<UpdateAttemptOutcome> RunOnceAsync(CancellationToken ct)
    {
        var prev = LatestReleaseState.Read(_stateDir, _channel);
        var outcome = UpdateAttemptOutcome.Failed;
        (string Version, string? Summary)? release = null;

        try
        {
            var path = _channel == UpdateChannel.Stable ? StablePath : PrereleasePath;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.TryAddWithoutValidation("User-Agent", "total-recall/" + _version);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            // One deadline over sending and reading the answer.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_requestTimeout ?? RequestTimeout);
            var token = deadline.Token;

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 403 || status == 429)
            {
                outcome = UpdateAttemptOutcome.RateLimited;
            }
            else if (status == 200 && !(response.Content.Headers.ContentLength > MaxResponseBytes))
            {
                var bytes = await ReadBoundedAsync(response.Content, token).ConfigureAwait(false);
                // Strict decoding: invalid UTF-8 is a bad answer, not an unexpected exception.
                var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                using var doc = JsonDocument.Parse(text);
                release = SelectRelease(doc.RootElement, _channel == UpdateChannel.Stable);
                if (release is not null) outcome = UpdateAttemptOutcome.Answered;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The deadline passed.
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or JsonException or InvalidDataException or DecoderFallbackException)
        {
            // An expected failure: offline, a reset, a bad answer.
        }

        var now = _now();
        switch (outcome)
        {
            case UpdateAttemptOutcome.Answered:
                _lastAnswer = now;
                _lastAttempt = now;
                LatestReleaseState.Write(_stateDir, _channel,
                    new LatestReleaseFile(release!.Value.Version, release.Value.Summary, now, now));
                break;
            case UpdateAttemptOutcome.RateLimited:
                _lastAnswer = now;
                _lastAttempt = now;
                LatestReleaseState.Write(_stateDir, _channel,
                    new LatestReleaseFile(prev?.Version, prev?.Summary, now, now));
                break;
            default:
                _lastAttempt = now;
                LatestReleaseState.Write(_stateDir, _channel,
                    new LatestReleaseFile(prev?.Version, prev?.Summary, prev?.CheckedAt, now));
                break;
        }
        return outcome;
    }

    // Reads at most MaxResponseBytes; one more byte is InvalidDataException.
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var n = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) break;
            if (buffer.Length + n > MaxResponseBytes) throw new InvalidDataException("response too large");
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    // --- selection -------------------------------------------------------------------------------

    internal static (string Version, string? Summary)? SelectRelease(JsonElement root, bool stableRunning)
    {
        IEnumerable<JsonElement> candidates = root.ValueKind switch
        {
            JsonValueKind.Object => new[] { root },
            JsonValueKind.Array => root.EnumerateArray(),
            _ => Array.Empty<JsonElement>(),
        };

        string? bestVersion = null;
        ParsedVersion best = default;
        JsonElement bestItem = default;
        foreach (var item in candidates)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (IsTrue(item, "draft")) continue;
            if (!item.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) continue;
            var tagText = tag.GetString();
            if (tagText is null || tagText.Length < 2 || tagText[0] != 'v') continue;
            var version = tagText[1..];
            if (!UpdateChannel.IsValidReleaseVersion(version)) continue;
            if (!SemVer.TryParse(version, out var parsed)) continue;
            if (stableRunning && (IsTrue(item, "prerelease") || parsed.Prerelease.Length > 0)) continue;

            if (bestVersion is null || SemVer.Compare(parsed, best) > 0)
            {
                bestVersion = version;
                best = parsed;
                bestItem = item;
            }
        }
        if (bestVersion is null) return null;

        string? summary = null;
        if (bestItem.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
        {
            var clean = SummarySanitizer.Sanitize(FirstParagraph(body.GetString()));
            if (clean.Length > 0) summary = clean;
        }
        return (bestVersion, summary);
    }

    private static bool IsTrue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    internal static string? FirstParagraph(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
        var paragraph = new List<string>();
        while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
        {
            paragraph.Add(lines[i]);
            i++;
        }
        return paragraph.Count == 0 ? null : string.Join("\n", paragraph);
    }

    // --- schedule --------------------------------------------------------------------------------

    /// <summary>
    /// When the next request is due: the later of the answer time plus the interval and the last
    /// attempt plus one hour. A time more than a day in the future counts as missing. Due when the
    /// result is at most <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset NextDue(LatestReleaseFile? file, DateTimeOffset now, int intervalHours)
    {
        var limit = now + FutureTolerance;
        var checkedAt = file?.CheckedAt is { } c && c <= limit ? c : (DateTimeOffset?)null;
        var lastAttempt = file?.LastAttempt is { } l && l <= limit ? l : (DateTimeOffset?)null;
        var hours = UpdateSettings.NormalizeIntervalHours(intervalHours);

        var dueAnswer = checkedAt is { } a ? a + TimeSpan.FromHours(hours) : DateTimeOffset.MinValue;
        var dueRetry = lastAttempt is { } t ? t + RetryAfterFailure : DateTimeOffset.MinValue;
        return dueAnswer > dueRetry ? dueAnswer : dueRetry;
    }

    /// <summary>Drops file times later than now + FutureTolerance, then takes the later non-null time of each pair.</summary>
    internal static LatestReleaseFile? MergeWithMemory(
        LatestReleaseFile? file, DateTimeOffset? lastAnswer, DateTimeOffset? lastAttempt, DateTimeOffset now)
    {
        var limit = now + FutureTolerance;
        var fileAnswer = file?.CheckedAt is { } c && c <= limit ? c : (DateTimeOffset?)null;
        var fileAttempt = file?.LastAttempt is { } l && l <= limit ? l : (DateTimeOffset?)null;
        var answer = Later(fileAnswer, lastAnswer);
        var attempt = Later(fileAttempt, lastAttempt);

        if (file is null && answer is null && attempt is null) return null;
        return new LatestReleaseFile(file?.Version, file?.Summary, answer, attempt);
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);

    /// <summary>The loop. Never throws and never writes to standard output.</summary>
    public async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await _delay(FirstDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var now = _now();
                var file = MergeWithMemory(
                    LatestReleaseState.Read(_stateDir, _channel), _lastAnswer, _lastAttempt, now);
                var next = NextDue(file, now, _intervalHours);
                if (next <= now)
                {
                    await RunOnceAsync(ct).ConfigureAwait(false);
                    continue;
                }
                var wait = next - now;
                await _delay(wait < MaxSleep ? wait : MaxSleep, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            try { _errors.WriteLine("[total-recall] update check stopped: " + ex.GetType().Name + ": " + ex.Message); }
            catch { }
        }
    }
}
