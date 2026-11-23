using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.TestSupport.Transcripts;

/// <summary>
/// Seeds an archive for the read tests through the public <see cref="ITranscriptStore.ApplyIngest"/> only (this folder is
/// compiled into the Server and Cli test projects, which cannot see Infrastructure internals). Times are written in the
/// archive's own format (<c>yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'</c>, UTC), because the session list compares them as text.
/// </summary>
public sealed class ReadArchiveBuilder
{
    private readonly ITranscriptStore _store;

    public ReadArchiveBuilder(ITranscriptStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public SessionBuilder Session(
        string host,
        string id,
        string project = "owner/repo",
        string kind = "interactive",
        string? title = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null) =>
        new(_store, host, id, project, kind, title, startedAt, endedAt);

    /// <summary>The archive's timestamp text of <paramref name="t"/>, or null.</summary>
    public static string? Iso(DateTimeOffset? t) =>
        t?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>One session's exchanges, written with one <see cref="ITranscriptStore.ApplyIngest"/> by <see cref="Commit"/>.</summary>
public sealed class SessionBuilder
{
    private readonly ITranscriptStore _store;
    private readonly string _host;
    private readonly string _id;
    private readonly string _project;
    private readonly string _kind;
    private readonly string? _title;
    private readonly DateTimeOffset? _startedAt;
    private readonly DateTimeOffset? _endedAt;
    private readonly List<IngestChunk> _chunks = new();

    internal SessionBuilder(
        ITranscriptStore store, string host, string id, string project, string kind, string? title,
        DateTimeOffset? startedAt, DateTimeOffset? endedAt)
    {
        _store = store;
        _host = host;
        _id = id;
        _project = project;
        _kind = kind;
        _title = title;
        _startedAt = startedAt;
        _endedAt = endedAt;
    }

    /// <summary>Adds exchange <paramref name="n"/> stored as one part.</summary>
    public SessionBuilder Exchange(
        int n, string body, DateTimeOffset? ts = null, string chunkType = "dialogue", bool hasError = false)
    {
        _chunks.Add(new IngestChunk(n, n, 0, chunkType, hasError, ReadArchiveBuilder.Iso(ts), 1, Content(n, chunkType, ts, body)));
        return this;
    }

    /// <summary>
    /// Adds exchange <paramref name="n"/> stored in several parts, one per body, in order. The caller writes each later
    /// part's <c>User (continued): &lt;question&gt;</c> prefix itself.
    /// </summary>
    public SessionBuilder Parts(int n, params string[] bodies) => PartsCore(n, bodies, reverseInsertion: false);

    /// <summary>
    /// Like <see cref="Parts"/>, but the parts are handed to the ingest last first, so the rows are stored in descending
    /// part order and only an ORDER BY on the part reads them back ascending.
    /// </summary>
    public SessionBuilder PartsStoredReversed(int n, params string[] bodies) => PartsCore(n, bodies, reverseInsertion: true);

    private SessionBuilder PartsCore(int n, string[] bodies, bool reverseInsertion)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        var parts = new List<IngestChunk>();
        for (var part = 0; part < bodies.Length; part++)
            parts.Add(new IngestChunk(n, n, part, "dialogue", false, null, 1, Content(n, "dialogue", null, bodies[part])));
        if (reverseInsertion) parts.Reverse();
        _chunks.AddRange(parts);
        return this;
    }

    /// <summary>Makes the session a reset row: no chunks and an exchange count of 0.</summary>
    public SessionBuilder Reset()
    {
        _chunks.Clear();
        return this;
    }

    /// <summary>Writes the session and its chunks with one <see cref="ITranscriptStore.ApplyIngest"/>.</summary>
    public IngestOutcome Commit()
    {
        var exchangeCount = _chunks.Count == 0 ? 0 : _chunks.Max(c => c.ExchangeEnd) + 1;
        return _store.ApplyIngest(new SessionIngest(
            _host, _id, _project, "remote", "/w/repo", "/w/repo/sub", ReadArchiveBuilder.Iso(_startedAt), ReadArchiveBuilder.Iso(_endedAt), _kind, _title,
            "/logs/" + _id + ".jsonl", 4096, 1700000000000, "fp-1", 10, exchangeCount, 2, null,
            _store.GetIngestState(_host, _id), 0, _chunks.ToList()));
    }

    private string Content(int n, string chunkType, DateTimeOffset? ts, string body)
    {
        var id8 = _id.Length > 8 ? _id[..8] : _id;
        var date = ts is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "unknown";
        var number = n.ToString(CultureInfo.InvariantCulture);
        return $"{_host} session {id8} | project {_project} | {date} | {_kind} | {chunkType} | exchanges {number}-{number}"
            + "\n\n" + body;
    }
}
