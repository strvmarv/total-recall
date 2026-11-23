using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using TotalRecall.Infrastructure.Diagnostics;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// One <c>transcript_get</c> call (read spec section 2): either <paramref name="Id"/> (a chunk id from
/// <c>transcript_search</c>) or <paramref name="Session"/> (<c>&lt;host&gt;:&lt;host_session_id&gt;</c>) with
/// <paramref name="FromExchange"/> and optionally <paramref name="ToExchange"/> (default: <paramref name="FromExchange"/>).
/// <paramref name="Context"/> is 0 to 10 stored exchanges on each side; <paramref name="MaxTokens"/> is 200 to 20,000.
/// </summary>
public sealed record TranscriptGetRequest(long? Id, string? Session, int? FromExchange, int? ToExchange, int Context, int MaxTokens);

/// <summary>
/// One exchange of an answer: its number, the first stored <c>ts</c> of its parts, its chunk type, whether any part has an
/// error, whether it was asked for (false for context) and its text (the parts joined by <see cref="ExchangeText.Join"/>,
/// or a cut text ending with <see cref="TranscriptReadService.CutMarker"/>).
/// </summary>
public sealed record ExchangeView(int N, string? Ts, string ChunkType, bool HasError, bool Requested, string Text);

/// <summary>
/// What a get answers. <paramref name="Requested"/> is the asked range (<c>N</c> for one exchange, else <c>from-to</c>);
/// <paramref name="Exchanges"/> are ascending; <paramref name="ApproxTokens"/> is the sum of <c>ceil(chars / 4)</c> of the
/// returned texts; <paramref name="NextFromExchange"/> and <paramref name="NextToExchange"/> are set together, only when
/// requested exchanges after the last returned one were left out. When <paramref name="Error"/> is set every other field
/// is empty and the handler answers it as an error result.
/// </summary>
public sealed record TranscriptGetResponse(
    string Session,
    string Project,
    string Kind,
    string? Title,
    string Requested,
    int Context,
    IReadOnlyList<ExchangeView> Exchanges,
    int ApproxTokens,
    bool Truncated,
    int? NextFromExchange,
    int? NextToExchange,
    string? Note,
    string? Error);

/// <summary>
/// One <c>transcript_sessions</c> call (read spec section 3). <paramref name="Since"/> (at or after) and
/// <paramref name="Before"/> (strictly before) bound a session's last activity; <paramref name="Limit"/> is 1 to 100.
/// </summary>
public sealed record TranscriptSessionsRequest(string? Project, DateTimeOffset? Since, DateTimeOffset? Before, int Limit, bool IncludeAutomated);

/// <summary>
/// One listed session: <paramref name="Session"/> is <c>&lt;host&gt;:&lt;host_session_id&gt;</c>, <paramref name="Exchanges"/>
/// the session's exchange count (valid numbers are below it; some may hold no text) and <paramref name="Chunks"/> its
/// stored chunk count.
/// </summary>
public sealed record SessionView(
    string Session, string Project, string Kind, string? Title, string? StartedAt, string? EndedAt, int Exchanges, long Chunks);

/// <summary>
/// What a session list answers: <paramref name="Total"/> matches before the limit, <paramref name="More"/> =
/// <c>Total</c> minus the sessions returned, <paramref name="AutomatedHidden"/> the automated sessions the filters left
/// out, the sessions newest first, an optional note and, when the archive could not be read, an error (every other
/// field empty).
/// </summary>
public sealed record TranscriptSessionsResponse(
    long Total, long More, long AutomatedHidden, IReadOnlyList<SessionView> Sessions, string? Note, string? Error);

/// <summary>What a read needs from the composition, built per call: the archive's location only (no settings, no embedder).</summary>
public sealed record TranscriptReadContext(TranscriptLocation Location);

/// <summary>
/// The archive reads (read spec sections 2 to 4). <see cref="Sessions"/> lists sessions newest first. <see cref="Get"/>
/// returns a window of exchanges within a token budget: the requested exchanges first, then context alternating before and after, an exchange whole or not at all
/// (except a first requested exchange alone over the budget, which is cut and marked), with pointers to read on. The
/// archive is opened per call with <c>create: false</c>, read in one <see cref="ITranscriptStore.BeginReads"/> and closed
/// before returning; nothing is written (opening may apply a pending schema step), no embedder is used and the memory
/// store is never touched.
/// </summary>
public sealed class TranscriptReadService
{
    /// <summary>The default number of stored context exchanges on each side.</summary>
    public const int DefaultContext = 1;

    /// <summary>The most context exchanges on each side.</summary>
    public const int MaxContext = 10;

    /// <summary>The default token budget of one answer.</summary>
    public const int DefaultMaxTokens = 2000;

    /// <summary>The smallest token budget.</summary>
    public const int MinMaxTokens = 200;

    /// <summary>The largest token budget.</summary>
    public const int MaxMaxTokens = 20000;

    /// <summary>The widest asked range, in exchange numbers.</summary>
    public const int MaxRangeWidth = 200;

    /// <summary>The last line of a cut exchange.</summary>
    public const string CutMarker = "[cut: raise max_tokens to read the rest]";

    /// <summary>Added to an unknown-target error when the archive holds no chunk at all.</summary>
    public const string NothingArchivedSuffix = " (nothing is archived yet; run total-recall transcripts backfill)";

    private readonly Func<TranscriptReadContext?> _context;
    // Unused today; kept for the spec's constructor signature.
    private readonly TimeProvider _time;
    private readonly Func<TranscriptLocation, TranscriptOpenResult> _open;

    /// <param name="context">Builds the call's context; evaluated on every call. Null or a throw is an error answer.</param>
    /// <param name="time">A clock; unused today.</param>
    public TranscriptReadService(Func<TranscriptReadContext?> context, TimeProvider? time = null)
        : this(context, time, null)
    {
    }

    /// <param name="open">A test seam that opens the archive; defaults to <see cref="TranscriptStore.Open"/> with <c>create: false</c>.</param>
    internal TranscriptReadService(
        Func<TranscriptReadContext?> context, TimeProvider? time, Func<TranscriptLocation, TranscriptOpenResult>? open)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _time = time ?? TimeProvider.System;
        _open = open ?? (static location => TranscriptStore.Open(location, create: false));
    }

    /// <summary>The error of a chunk id with no chunk; <paramref name="anyChunk"/> false adds <see cref="NothingArchivedSuffix"/>.</summary>
    public static string UnknownChunk(long id, bool anyChunk) =>
        "no archived chunk has id " + id.ToString(CultureInfo.InvariantCulture)
        + " (it may have been forgotten); use transcript_sessions to list sessions"
        + (anyChunk ? "" : NothingArchivedSuffix);

    /// <summary>The error of a session with no row; <paramref name="anyChunk"/> false adds <see cref="NothingArchivedSuffix"/>.</summary>
    public static string UnknownSession(string sessionRef, bool anyChunk)
    {
        ArgumentNullException.ThrowIfNull(sessionRef);
        return "no archived session " + sessionRef + "; use transcript_sessions to list sessions"
            + (anyChunk ? "" : NothingArchivedSuffix);
    }

    // ------------------------------------------------------------------
    // The get
    // ------------------------------------------------------------------

    /// <exception cref="ArgumentException">
    /// Both or neither of <c>id</c> and <c>session</c>; <c>from_exchange</c> or <c>to_exchange</c> with <c>id</c>;
    /// <c>session</c> without <c>from_exchange</c>; a malformed session; <c>id</c> below 1; <c>from_exchange</c> below 0;
    /// <c>to_exchange</c> below <c>from_exchange</c>; a range wider than <see cref="MaxRangeWidth"/>; <c>context</c> or
    /// <c>max_tokens</c> out of range. Checked before the archive is opened.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public TranscriptGetResponse Get(TranscriptGetRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = Plan(request);
        ct.ThrowIfCancellationRequested();

        return Execute(
            "transcript_get",
            () => Failure(target.Unknown(anyChunk: false)),
            Failure,
            store => Run(request, target, store, ct));
    }

    /// <summary>
    /// The shared frame of a read call: builds the context, opens the archive (never creating it) and runs
    /// <paramref name="run"/> over it, answering every fault as <paramref name="failure"/> (logged under the tool's
    /// prefix) and an absent file as <paramref name="absent"/>. Arguments are checked by the caller before this runs.
    /// </summary>
    private T Execute<T>(string tool, Func<T> absent, Func<string, T> failure, Func<ITranscriptStore, T> run)
    {
        var prefix = "[total-recall] " + tool;
        TranscriptReadContext? context;
        try
        {
            context = _context();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain(prefix + ": the archive's settings", ex);
            return failure("the transcript archive cannot be located: " + ex.Message);
        }
        if (context is null)
            return failure("the transcript archive is not available in this storage mode");

        var open = _open(context.Location);
        switch (open.Status)
        {
            case TranscriptOpenStatus.Absent:
                return absent();
            case TranscriptOpenStatus.Refused:
            case TranscriptOpenStatus.Failed:
            case TranscriptOpenStatus.Opened when open.Store is null:
                var why = open.Message ?? open.Status.ToString();
                Console.Error.WriteLine(prefix + ": the archive could not be opened: " + why);
                return failure("the transcript archive could not be opened: " + why);
        }

        using var store = open.Store!;
        try
        {
            return run(store);
        }
        // Every argument check is done before the archive is opened, so an ArgumentException from here on is a
        // fault of the store or a read (logged and answered like any other), never the caller's argument error.
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            ExceptionLogger.LogChain(prefix + ": reading the archive", ex);
            return failure("the transcript archive could not be read: " + ex.Message);
        }
    }

    private static TranscriptGetResponse Run(TranscriptGetRequest request, Target target, ITranscriptStore store, CancellationToken ct)
    {
        using var reads = store.BeginReads();

        // Resolve the target: the named thing is checked first, so a forgotten one is unknown whatever else is archived.
        SessionRow session;
        int from, to;
        if (target.ChunkId is { } chunkId)
        {
            var chunk = reads.ChunkById(chunkId);
            var owner = chunk is null ? null : reads.SessionById(chunk.SessionId);
            if (chunk is null || owner is null)
                return Failure(UnknownChunk(chunkId, reads.AnyChunk()));
            session = owner;
            from = chunk.ExchangeStart;
            to = chunk.ExchangeEnd;
        }
        else
        {
            var named = reads.SessionByRef(target.Host!, target.HostSessionId!);
            if (named is null)
                return Failure(UnknownSession(target.SessionRef!, reads.AnyChunk()));
            session = named;
            from = target.From;
            to = target.To;
        }
        ct.ThrowIfCancellationRequested();

        var asked = from == to
            ? from.ToString(CultureInfo.InvariantCulture)
            : from.ToString(CultureInfo.InvariantCulture) + "-" + to.ToString(CultureInfo.InvariantCulture);
        TranscriptGetResponse Answer(IReadOnlyList<ExchangeView> views, int tokens, bool truncated, int? next, string? note) =>
            new(session.Ref, session.Project, session.Kind, session.Title, asked, request.Context,
                views.OrderBy(v => v.N).ToArray(), tokens, truncated, next, next is null ? null : to, note, null);

        var requested = reads.ExchangesInRange(session.Id, from, to);
        if (requested.Count == 0)
            return Answer(Array.Empty<ExchangeView>(), 0, false, null, EmptyRangeNote(from, to, session.ExchangeCount));

        // (1) The requested exchanges, ascending, each whole; the first that does not fit ends the answer (no context).
        var budget = request.MaxTokens;
        var views = new List<ExchangeView>();
        var used = 0;
        for (var i = 0; i < requested.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var n = requested[i];
            var view = Read(reads, session.Id, n, requested: true);
            var tokens = ExchangeText.Tokens(view.Text);
            if (used + tokens <= budget)
            {
                views.Add(view);
                used += tokens;
                continue;
            }

            if (i == 0)
            {
                // Alone over the budget: cut so that text and marker fit, and nothing follows it.
                var cut = view with { Text = Cut(view.Text, budget) };
                int? after = requested.Count > 1 ? requested[1] : null;
                return Answer(new[] { cut }, ExchangeText.Tokens(cut.Text), true, after,
                    CutNote(n, budget, session.Ref, after, to));
            }

            return Answer(views, used, true, n, EndedNote(n, budget, session.Ref, to));
        }

        // (2) The context: one before, one after (before first), nearer before farther; the first that does not fit
        // ends the window. An exchange's text is read only when the loop reaches it.
        var before = reads.ExchangesBefore(session.Id, requested[0], request.Context);
        var afterList = reads.ExchangesAfter(session.Id, requested[^1], request.Context);
        var leftOut = 0;
        var full = false;
        for (var k = 0; k < request.Context; k++)
        {
            foreach (var side in new[] { before, afterList })
            {
                if (k >= side.Count) continue;
                if (full)
                {
                    leftOut++;
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                var view = Read(reads, session.Id, side[k], requested: false);
                var tokens = ExchangeText.Tokens(view.Text);
                if (used + tokens <= budget)
                {
                    views.Add(view);
                    used += tokens;
                }
                else
                {
                    full = true;
                    leftOut++;
                }
            }
        }

        return Answer(views, used, leftOut > 0, null, leftOut > 0 ? ContextNote(leftOut, budget) : null);
    }

    // ------------------------------------------------------------------
    // The session list
    // ------------------------------------------------------------------

    /// <summary>The most sessions one list returns.</summary>
    public const int MaxSessionLimit = 100;

    /// <summary>
    /// Lists archived sessions that hold at least one chunk, newest last activity first (read spec section 3).
    /// <see cref="TranscriptSessionsResponse.Total"/> counts the matches before the limit.
    /// </summary>
    /// <exception cref="ArgumentException"><c>limit</c> outside 1 to <see cref="MaxSessionLimit"/>; checked before the archive is opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public TranscriptSessionsResponse Sessions(TranscriptSessionsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > MaxSessionLimit)
            throw new ArgumentException(
                $"limit must be between 1 and {I(MaxSessionLimit)}; got {I(request.Limit)}");
        ct.ThrowIfCancellationRequested();

        return Execute(
            "transcript_sessions",
            () => SessionsAnswer(0, 0, 0, Array.Empty<SessionView>(), TranscriptSearchService.NothingArchivedNote),
            SessionsFailure,
            store =>
            {
                using var reads = store.BeginReads();
                var list = reads.ListSessions(
                    new SessionFilter(request.Project, request.Since, request.Before, request.IncludeAutomated), request.Limit);
                ct.ThrowIfCancellationRequested();
                var views = list.Rows
                    .Select(r => new SessionView(r.Ref, r.Project, r.Kind, r.Title, r.StartedAt, r.EndedAt, r.ExchangeCount, r.ChunkCount))
                    .ToArray();
                var more = list.Total - views.Length;

                string? note = null;
                // An empty list of an archive without any chunk says why; a filter that matches nothing in a non-empty one does not.
                if (views.Length == 0 && list.Total == 0 && !reads.AnyChunk())
                    note = TranscriptSearchService.NothingArchivedNote;
                else if (more > 0)
                    note = $"only the newest {I(request.Limit)} of {list.Total.ToString(CultureInfo.InvariantCulture)} sessions are listed; "
                        + "narrow with project, since or before to list others";
                return SessionsAnswer(list.Total, more, list.AutomatedHidden, views, note);
            });
    }

    private static TranscriptSessionsResponse SessionsAnswer(
        long total, long more, long automatedHidden, IReadOnlyList<SessionView> sessions, string? note) =>
        new(total, more, automatedHidden, sessions, note, null);

    private static TranscriptSessionsResponse SessionsFailure(string error) =>
        new(0, 0, 0, Array.Empty<SessionView>(), null, error);

    private static ExchangeView Read(ITranscriptReads reads, long sessionId, int n, bool requested)
    {
        var parts = reads.ChunksOfExchange(sessionId, n);
        return new ExchangeView(
            n,
            parts.Select(p => p.Ts).FirstOrDefault(ts => ts is not null),
            parts.Count > 0 ? parts[0].ChunkType : TranscriptChunker.DialogueType,
            parts.Any(p => p.HasError),
            requested,
            ExchangeText.Join(parts));
    }

    /// <summary>The text cut on a safe boundary so that it, a newline and <see cref="CutMarker"/> are at most <c>4 x maxTokens</c> characters.</summary>
    private static string Cut(string text, int maxTokens)
    {
        var keep = TextCut.SafeLength(text, 4 * maxTokens - CutMarker.Length - 1);
        return string.Concat(text.AsSpan(0, keep), "\n", CutMarker);
    }

    // ------------------------------------------------------------------
    // Arguments
    // ------------------------------------------------------------------

    /// <summary>The validated target: a chunk id, or a session with its asked range.</summary>
    private sealed record Target(long? ChunkId, string? SessionRef, string? Host, string? HostSessionId, int From, int To)
    {
        public string Unknown(bool anyChunk) =>
            ChunkId is { } id ? UnknownChunk(id, anyChunk) : UnknownSession(SessionRef!, anyChunk);
    }

    private static Target Plan(TranscriptGetRequest r)
    {
        if (r.Id is not null && r.Session is not null)
            throw new ArgumentException("give either id or session, not both");
        if (r.Id is null && r.Session is null)
            throw new ArgumentException("give either id (a chunk id from transcript_search) or session (<host>:<host_session_id>) with from_exchange");

        Target target;
        if (r.Id is { } id)
        {
            if (r.FromExchange is not null || r.ToExchange is not null)
                throw new ArgumentException("from_exchange and to_exchange go with session, not with id");
            if (id < 1)
                throw new ArgumentException($"id must be a chunk id of 1 or more; got {id.ToString(CultureInfo.InvariantCulture)}");
            target = new Target(id, null, null, null, 0, 0);
        }
        else
        {
            if (r.FromExchange is not { } from)
                throw new ArgumentException("session needs from_exchange (and optionally to_exchange)");
            var (host, hostSessionId) = TranscriptSearchService.SplitSession(r.Session!);
            if (from < 0)
                throw new ArgumentException($"from_exchange must be 0 or more; got {from.ToString(CultureInfo.InvariantCulture)}");
            var to = r.ToExchange ?? from;
            if (to < from)
                throw new ArgumentException(
                    $"to_exchange must not be below from_exchange; got from_exchange {from.ToString(CultureInfo.InvariantCulture)}, to_exchange {to.ToString(CultureInfo.InvariantCulture)}");
            var width = (long)to - from + 1;
            if (width > MaxRangeWidth)
                throw new ArgumentException(
                    $"a range may be at most {MaxRangeWidth.ToString(CultureInfo.InvariantCulture)} exchanges wide; from_exchange {from.ToString(CultureInfo.InvariantCulture)} to to_exchange {to.ToString(CultureInfo.InvariantCulture)} is {width.ToString(CultureInfo.InvariantCulture)}");
            target = new Target(null, r.Session, host, hostSessionId, from, to);
        }

        if (r.Context is < 0 or > MaxContext)
            throw new ArgumentException($"context must be between 0 and {MaxContext.ToString(CultureInfo.InvariantCulture)}; got {r.Context.ToString(CultureInfo.InvariantCulture)}");
        if (r.MaxTokens is < MinMaxTokens or > MaxMaxTokens)
            throw new ArgumentException(
                $"max_tokens must be between {MinMaxTokens.ToString(CultureInfo.InvariantCulture)} and {MaxMaxTokens.ToString(CultureInfo.InvariantCulture)}; got {r.MaxTokens.ToString(CultureInfo.InvariantCulture)}");
        return target;
    }

    // ------------------------------------------------------------------
    // Answers and notes
    // ------------------------------------------------------------------

    private static TranscriptGetResponse Failure(string error) =>
        new("", "", "", null, "", 0, Array.Empty<ExchangeView>(), 0, false, null, null, null, error);

    private static string ReadOn(string sessionRef, int from, int to) =>
        $"read on with session: {sessionRef}, from_exchange: {I(from)}, to_exchange: {I(to)}, context: 0";

    private static string EndedNote(int next, int maxTokens, string sessionRef, int to) =>
        $"max_tokens {I(maxTokens)} was reached before exchange {I(next)}, so the answer ends there and no context was added; "
        + ReadOn(sessionRef, next, to);

    private static string CutNote(int n, int maxTokens, string sessionRef, int? next, int to) =>
        $"exchange {I(n)} alone is over max_tokens {I(maxTokens)}: it was returned cut, is not read on, and no context was added; "
        + "raise max_tokens to read it whole"
        + (next is { } from ? "; to read the rest of the range, " + ReadOn(sessionRef, from, to) : "");

    private static string ContextNote(int leftOut, int maxTokens) =>
        $"{I(leftOut)} context exchange{(leftOut == 1 ? "" : "s")} left out to fit max_tokens {I(maxTokens)}; "
        + "raise max_tokens to see more context";

    private static string EmptyRangeNote(int from, int to, int exchangeCount)
    {
        var asked = from == to ? $"exchange {I(from)}" : $"exchanges {I(from)}-{I(to)}";
        var count = $"the session has {I(exchangeCount)} exchange{(exchangeCount == 1 ? "" : "s")}";
        if (exchangeCount > 0)
            count += $" (numbered 0 to {I(exchangeCount - 1)}; some may hold no text)";
        return $"no stored exchange in {asked}; {count}";
    }

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
}
