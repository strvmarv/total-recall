using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// One <c>transcript_search</c> call (search the archive, spec section 5). <paramref name="Scope"/> is
/// <c>session</c>, <c>project</c>, <c>all</c> or null (inferred); <paramref name="Session"/> is
/// <c>&lt;host&gt;:&lt;host_session_id&gt;</c>; <paramref name="TopK"/> is 1 to 50.
/// </summary>
public sealed record TranscriptSearchRequest(
    string Query,
    string? Scope,
    string? Project,
    string? Session,
    DateTimeOffset? Since,
    bool IncludeAutomated,
    int TopK);

/// <summary>
/// One result. <paramref name="Session"/> is <c>&lt;host&gt;:&lt;host_session_id&gt;</c>; <paramref name="Via"/> lists
/// the candidate lists the chunk came from (<c>meaning</c>, <c>phrase</c>, <c>words</c>); <paramref name="Score"/> is
/// relative (the top hit is 1.0); <paramref name="Text"/> is the chunk body without its header line.
/// </summary>
public sealed record TranscriptSearchHit(
    long Id,
    string Session,
    string Project,
    string Kind,
    string? Title,
    string? Ts,
    string ChunkType,
    bool HasError,
    string Exchanges,
    IReadOnlyList<string> Via,
    double Score,
    string Text);

/// <summary>
/// What a search answers. <paramref name="ScopeNote"/> is set only when the effective scope differs from the asked one;
/// <paramref name="MeaningReason"/> only when meaning is off; <paramref name="TruncatedHits"/> is the number of hits
/// left out by the text cap (0 when none). <paramref name="Error"/> is set when the archive could not be opened or
/// read; the handler answers it as an error result.
/// </summary>
public sealed record TranscriptSearchResponse(
    string Query,
    string Scope,
    string? ScopeNote,
    bool MeaningOn,
    string? MeaningReason,
    long PendingEmbedding,
    IReadOnlyList<TranscriptSearchHit> Results,
    int TruncatedHits,
    string? Note,
    string? Error);

/// <summary>
/// What a search needs from the composition, built per call: the archive's location, the <c>[transcripts]</c>
/// settings, the storage mode, the effective <c>[embedding]</c> config and the composition's embedder.
/// </summary>
public sealed record TranscriptSearchContext(
    TranscriptLocation Location,
    TranscriptsSettings Settings,
    string StorageMode,
    Core.Config.EmbeddingConfig Embedding,
    IEmbedder Embedder);

/// <summary>
/// The archive search (search the archive, spec section 5): the effective scope, the meaning decision through
/// <see cref="EmbedGate"/>, the vector, phrase and words candidates under the same filters, the archive's own ranking
/// (<see cref="RankFusion"/>), <c>pendingEmbedding</c> and the 40,000-character text cap. The archive is opened per
/// call with <c>create: false</c> and closed before returning; nothing is written (opening may apply a pending schema
/// step) and the memory store is never touched.
/// </summary>
public sealed class TranscriptSearchService
{
    /// <summary>The note of an absent or empty archive.</summary>
    public const string NothingArchivedNote = "nothing is archived yet; run total-recall transcripts backfill";

    /// <summary>The most characters of hit text in one answer.</summary>
    public const int TextCap = 40_000;

    /// <summary>Candidates per list are <c>max(MinCandidates, 4 x top_k)</c>.</summary>
    public const int MinCandidates = 40;

    /// <summary>
    /// The phrase list is skipped for a query of more tokens than this. A 10,000-character query makes a phrase of
    /// about 1,700 tokens; FTS5 executes it, but an exact match of a sequence that long is pointless (the words list,
    /// capped at 12 tokens, and the meaning list still run). An error string or a sentence is far below the cap.
    /// </summary>
    public const int MaxPhraseTokens = 32;

    private const string LogPrefix = "[total-recall] transcript_search";

    private readonly Func<TranscriptSearchContext?> _context;
    private readonly TimeProvider _time;
    private readonly Func<TranscriptLocation, TranscriptOpenResult> _open;

    /// <param name="context">Builds the call's context; evaluated on every search. Null or a throw is an error answer.</param>
    /// <param name="time">The clock of the recency boost.</param>
    public TranscriptSearchService(Func<TranscriptSearchContext?> context, TimeProvider? time = null)
        : this(context, time, null)
    {
    }

    /// <param name="open">A test seam that opens the archive; defaults to <see cref="TranscriptStore.Open"/> with <c>create: false</c>.</param>
    internal TranscriptSearchService(
        Func<TranscriptSearchContext?> context, TimeProvider? time, Func<TranscriptLocation, TranscriptOpenResult>? open)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _time = time ?? TimeProvider.System;
        _open = open ?? (static location => TranscriptStore.Open(location, create: false));
    }

    // ------------------------------------------------------------------
    // Argument helpers (the handler uses them too)
    // ------------------------------------------------------------------

    /// <summary>
    /// A full ISO-8601 instant (<c>2026-03-02T10:30:00Z</c>, an offset, fractions; no offset is read as UTC) or a date
    /// alone (read as 00:00:00 UTC).
    /// </summary>
    /// <param name="argumentName">The argument the text came from; the error names it (<c>since</c>, <c>before</c>).</param>
    /// <exception cref="ArgumentException">Anything else.</exception>
    public static DateTimeOffset ParseSince(string text, string argumentName = "since")
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.Trim();
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTimeOffset.TryParseExact(t, SinceFormats, CultureInfo.InvariantCulture, styles, out var value))
            return value.ToUniversalTime();
        throw new ArgumentException($"{argumentName} must be an ISO-8601 date (2026-11-18) or date-time (2026-11-18T10:00:00Z); got '{text}'");
    }

    private static readonly string[] SinceFormats =
    {
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    };

    /// <summary>Splits <c>&lt;host&gt;:&lt;host_session_id&gt;</c> at the first colon.</summary>
    /// <exception cref="ArgumentException">No colon, or an empty host or id.</exception>
    public static (string Host, string Id) SplitSession(string session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var colon = session.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == session.Length - 1)
            throw new ArgumentException($"session must be <host>:<host_session_id> (for example claude-code:3f2a...); got '{session}'");
        return (session[..colon], session[(colon + 1)..]);
    }

    /// <summary>The part of a chunk's content after the first blank line (the header line's facts are fields); the whole content when it has none.</summary>
    public static string ChunkText(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var blank = content.IndexOf("\n\n", StringComparison.Ordinal);
        return blank < 0 ? content : content[(blank + 2)..];
    }

    // ------------------------------------------------------------------
    // The search
    // ------------------------------------------------------------------

    /// <exception cref="ArgumentException">An empty query, a <c>top_k</c> out of range, an unknown scope, <c>scope=session</c> without a session, a malformed session.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public TranscriptSearchResponse Search(TranscriptSearchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("query must be a non-empty string");
        if (request.TopK is < 1 or > 50)
            throw new ArgumentException("top_k must be between 1 and 50");

        var plan = Plan(request);
        ct.ThrowIfCancellationRequested();

        TranscriptSearchContext? context;
        try
        {
            context = _context();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain(LogPrefix + ": the archive's settings", ex);
            return Failure(request, plan, "the transcript archive cannot be located: " + ex.Message);
        }
        if (context is null)
            return Failure(request, plan, "the transcript archive is not available in this storage mode");

        var open = _open(context.Location);
        switch (open.Status)
        {
            case TranscriptOpenStatus.Absent:
                return Empty(request, plan, Gate(context, null, null), NothingArchivedNote);
            case TranscriptOpenStatus.Refused:
            case TranscriptOpenStatus.Failed:
            case TranscriptOpenStatus.Opened when open.Store is null:
                return Failure(request, plan, "the transcript archive could not be opened: " + (open.Message ?? open.Status.ToString()));
        }

        using var store = open.Store!;
        try
        {
            return Run(request, plan, context, store, ct);
        }
        // Every argument check is done above, before the archive is opened, so an ArgumentException from here on is a
        // fault of the store or a read (logged and answered like any other), never the caller's argument error.
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            ExceptionLogger.LogChain(LogPrefix + ": reading the archive", ex);
            return Failure(request, plan, "the transcript archive could not be read: " + ex.Message);
        }
    }

    private TranscriptSearchResponse Run(
        TranscriptSearchRequest request, ScopePlan plan, TranscriptSearchContext context, ITranscriptStore store, CancellationToken ct)
    {
        // The fingerprint reads open their own transaction, so they run before the search reads (one connection).
        // Checking it outside the read transaction is deliberate and harmless: a fingerprint is never rewritten while a
        // vector row exists (spec section 2), so a Match or Mismatch seen here still holds when the reads run. The only
        // window is an Unstamped archive whose first vectors another process commits in between; that one call may
        // then compare the query with those few vectors, and the next call sees the stamp.
        var gate = Gate(context, store.CheckFingerprint, store.ReadEmbedFingerprint);

        // A first, short read: an empty archive or an unknown session ends here, before any model is loaded.
        using (var pre = store.BeginSearchReads())
        {
            if (!pre.AnyChunk())
                return Empty(request, plan, gate, NothingArchivedNote);
            if (plan.Session is { } named && pre.ResolveSession(named.Host, named.Id) is null)
                return Empty(request, plan, gate, UnknownSession(request.Session!));
        }
        ct.ThrowIfCancellationRequested();

        // The query is embedded outside any read transaction (a first model load takes seconds), and only when the
        // gate allows meaning: never for a remote provider without the opt-in or a fingerprint mismatch.
        var meaningOn = gate.MeaningUsable;
        var reason = meaningOn ? null : gate.Text;
        float[]? queryVector = null;
        if (meaningOn)
        {
            try
            {
                var v = context.Embedder.EmbedQuery(request.Query);
                if (v is null || v.Length != EmbedGate.ArchiveDimensions)
                {
                    meaningOn = false;
                    reason = EmbedGate.Dimension(v?.Length ?? 0).Text;
                }
                else
                {
                    queryVector = v;
                }
            }
            catch (Exception ex) when (IsQueryTimeFailure(ex, ct))
            {
                ExceptionLogger.LogChain(LogPrefix + ": embedding the query (searching by words)", ex);
                meaningOn = false;
                reason = ex is InvalidOperationException
                    ? EmbedGate.NotReady(ex.Message).Text
                    : EmbedGate.Failed(ex.Message).Text;
            }
        }
        ct.ThrowIfCancellationRequested();

        var k = Math.Max(MinCandidates, 4 * request.TopK);
        IReadOnlyList<long> vector = Array.Empty<long>();
        IReadOnlyList<long> phrase = Array.Empty<long>();
        IReadOnlyList<long> words = Array.Empty<long>();
        long pending;
        IReadOnlyDictionary<long, ChunkRow> rows;

        // Every read of the answer in one read transaction, disposed before anything else touches the store.
        using (var reads = store.BeginSearchReads())
        {
            long? sessionId = null;
            if (plan.Session is { } named)
            {
                sessionId = reads.ResolveSession(named.Host, named.Id);
                if (sessionId is null) // removed since the first read
                    return Empty(request, plan, gate, UnknownSession(request.Session!));
            }

            var filter = new ChunkFilter(
                sessionId,
                plan.Project,
                request.Since?.ToUnixTimeSeconds(),
                InteractiveOnly: !request.IncludeAutomated && plan.Scope != "session");

            if (queryVector is not null)
            {
                try
                {
                    vector = reads.Vector(queryVector, filter, k).Select(h => h.ChunkId).ToArray();
                }
                catch (Exception ex) when (IsQueryTimeFailure(ex, ct))
                {
                    ExceptionLogger.LogChain(LogPrefix + ": the vector search (searching by words)", ex);
                    meaningOn = false;
                    reason = $"words only: the vector search failed ({ex.Message})";
                }
            }

            var phraseMatch = TranscriptQueryBuilder.Phrase(request.Query);
            if (phraseMatch is not null && TokenCount(phraseMatch) <= MaxPhraseTokens)
                phrase = reads.Text(phraseMatch, filter, k).Select(h => h.ChunkId).ToArray();
            if (TranscriptQueryBuilder.Words(request.Query) is { } wordsMatch)
                words = reads.Text(wordsMatch, filter, k).Select(h => h.ChunkId).ToArray();

            pending = reads.CountInScope(filter, onlyUnembedded: meaningOn);
            rows = reads.Chunks(vector.Concat(phrase).Concat(words).Distinct().ToArray());
        }

        var info = rows.ToDictionary(r => r.Key, r => new RankInfo(r.Value.SessionId, r.Value.ChunkType, r.Value.HasError, r.Value.Ts));
        var ranked = RankFusion.Rank(
            vector, phrase, words, info, _time.GetUtcNow(),
            ErrorQueryDetector.LooksLikeError(request.Query), request.TopK,
            applySessionCap: plan.Scope != "session");

        var hits = new List<TranscriptSearchHit>(ranked.Count);
        var used = 0;
        var truncated = 0;
        for (var i = 0; i < ranked.Count; i++)
        {
            var row = rows[ranked[i].ChunkId];
            var text = ChunkText(row.Content);
            if (used + text.Length > TextCap)
            {
                truncated = ranked.Count - i;
                break;
            }
            used += text.Length;
            hits.Add(new TranscriptSearchHit(
                row.Id,
                row.SessionRef,
                row.Project,
                row.Kind,
                row.Title,
                row.Ts,
                row.ChunkType,
                row.HasError,
                Exchanges(row.ExchangeStart, row.ExchangeEnd),
                ranked[i].Via,
                Math.Round(ranked[i].Score, 6),
                text));
        }

        return new TranscriptSearchResponse(
            request.Query, plan.Scope, plan.Note, meaningOn, meaningOn ? null : reason, pending, hits, truncated, null, null);
    }

    // ------------------------------------------------------------------
    // Scope
    // ------------------------------------------------------------------

    private sealed record ScopePlan(string Scope, string? Note, (string Host, string Id)? Session, string? Project);

    /// <summary>
    /// The effective scope (spec section 5): an explicit scope wins; otherwise a session argument means session, a
    /// project argument project, nothing all. <c>scope=project</c> with no project is all, with a note;
    /// <c>scope=session</c> with no session is an argument error; <c>scope=all</c> ignores the project (and the
    /// session); a session scope ignores the project (the named session decides it).
    /// </summary>
    private static ScopePlan Plan(TranscriptSearchRequest r)
    {
        var session = string.IsNullOrWhiteSpace(r.Session) ? null : r.Session;
        var project = string.IsNullOrWhiteSpace(r.Project) ? null : r.Project;
        (string Host, string Id)? named = session is null ? null : SplitSession(session);

        var scope = r.Scope;
        if (scope is null)
            scope = named is not null ? "session" : project is not null ? "project" : "all";

        switch (scope)
        {
            case "session":
                if (named is null)
                    throw new ArgumentException("scope session needs a session argument (<host>:<host_session_id>)");
                return new ScopePlan("session", null, named, null);
            case "project":
                return project is null
                    ? new ScopePlan("all", "scope project was asked without a project argument (the current project is not detected yet), so every project was searched", null, null)
                    : new ScopePlan("project", null, null, project);
            case "all":
                return new ScopePlan("all", null, null, null);
            default:
                throw new ArgumentException($"scope must be session, project or all; got '{scope}'");
        }
    }

    // ------------------------------------------------------------------
    // The meaning decision
    // ------------------------------------------------------------------

    /// <summary>
    /// The gate of spec section 3 for a search: readiness from the embedder's own check, the fingerprint from its
    /// descriptor (no model is loaded; <c>EmbedderInspection</c> is not used, the composition's embedder is given).
    /// The tool is answered only while the sentinel says the archive is on, so the archive counts as active here.
    /// </summary>
    private static EmbedGateResult Gate(
        TranscriptSearchContext context,
        Func<ArchiveFingerprint, FingerprintState>? check,
        Func<ArchiveFingerprint?>? stored)
    {
        var provider = FSharpOption<string>.get_IsSome(context.Embedding.Provider) ? context.Embedding.Provider.Value : "local";
        var postgres = string.Equals(context.StorageMode, "postgres", StringComparison.OrdinalIgnoreCase);
        var allowRemote = context.Settings.AllowRemoteEmbedding;

        ArchiveFingerprint? current;
        EmbedderReadiness readiness;
        try
        {
            var d = context.Embedder.Descriptor;
            current = new ArchiveFingerprint(d.Provider, d.Model, d.Revision, d.Dimensions);
            readiness = (context.Embedder as IEmbedderReadiness)?.CheckReady() ?? new EmbedderReadiness(true, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain(LogPrefix + ": reading the embedder", ex);
            current = null;
            readiness = new EmbedderReadiness(false, ex.Message);
        }

        // Rules 1 and 2 need no store read; a remote provider without the opt-in stops here.
        var preliminary = EmbedGate.Evaluate(new EmbedGateInput(
            true, postgres, provider, allowRemote, null, null, null, null, new EmbedderReadiness(true, null), false, false));
        if (!preliminary.CanEmbed)
            return preliminary;

        FingerprintState? state = null;
        ArchiveFingerprint? storedPrint = null;
        if (current is not null && check is not null)
        {
            state = check(current);
            if (state == FingerprintState.Mismatch)
                storedPrint = stored?.Invoke();
        }

        return EmbedGate.Evaluate(new EmbedGateInput(
            ArchiveActive: true,
            Postgres: postgres,
            Provider: provider,
            AllowRemoteEmbedding: allowRemote,
            Dimensions: current?.Dimensions,
            Fingerprint: state,
            Stored: storedPrint,
            Current: current,
            Readiness: readiness,
            MemoryReindexRunning: false,
            ServerPass: false));
    }

    /// <summary>Any failure of the meaning path falls back to words; a cancellation of the call does not.</summary>
    private static bool IsQueryTimeFailure(Exception ex, CancellationToken ct) =>
        ex is not OutOfMemoryException && !(ex is OperationCanceledException && ct.IsCancellationRequested);

    // ------------------------------------------------------------------
    // Answers
    // ------------------------------------------------------------------

    private static TranscriptSearchResponse Empty(TranscriptSearchRequest r, ScopePlan plan, EmbedGateResult gate, string note) =>
        new(r.Query, plan.Scope, plan.Note, gate.MeaningUsable, gate.MeaningUsable ? null : gate.Text, 0,
            Array.Empty<TranscriptSearchHit>(), 0, note, null);

    private static TranscriptSearchResponse Failure(TranscriptSearchRequest r, ScopePlan plan, string error) =>
        new(r.Query, plan.Scope, plan.Note, false, null, 0, Array.Empty<TranscriptSearchHit>(), 0, null, error);

    private static string UnknownSession(string session) => $"no archived session {session}";

    private static string Exchanges(int start, int end) =>
        start == end
            ? start.ToString(CultureInfo.InvariantCulture)
            : start.ToString(CultureInfo.InvariantCulture) + "-" + end.ToString(CultureInfo.InvariantCulture);

    // The phrase is one quoted string of tokens separated by single spaces (tokens hold only letters and digits).
    private static int TokenCount(string phrase) => phrase.Count(c => c == ' ') + 1;
}
