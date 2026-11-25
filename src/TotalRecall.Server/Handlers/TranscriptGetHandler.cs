// src/TotalRecall.Server/Handlers/TranscriptGetHandler.cs
//
// transcript_get: read archived exchanges, either around one chunk by id (as returned by transcript_search) or a range
// of a session, within a token budget (read spec 2026-11-23, section 2). The gate is the sentinel, read once per call:
// off gives Part 1's off answer; with no service (a composition that gives none) the answer says the archive is not set
// up. Otherwise the arguments are parsed here (argument errors are ArgumentExceptions, which ErrorTranslator turns into
// "Invalid arguments: ..."), the service opens the archive for the call and closes it, an unknown target or an archive
// that cannot be read is an error result, and the answer is serialised through the source-generated JsonContext. The
// two alternatives are described in the text, not expressed with oneOf, so every client accepts the schema.
// Between the parsing and the read, the sync brings the current session's new tail in (no embed step; spec
// 2026-11-24, section 3; TranscriptSyncCall); the answer does not report it. Registered only by OpenSqlite and
// OpenCortexCore.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Server.Handlers;

public sealed class TranscriptGetHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id": {"type":"integer","description":"A chunk id returned by transcript_search"},
            "session": {"type":"string","description":"Session as <host>:<host_session_id>; use with from_exchange and to_exchange instead of id"},
            "from_exchange": {"type":"integer","description":"First exchange of the range (with session)"},
            "to_exchange": {"type":"integer","description":"Last exchange of the range (with session; default from_exchange, at most 200 exchanges wide)"},
            "context": {"type":"integer","minimum":0,"maximum":10,"default":1,"description":"Exchanges of context around the result (0 to 10, default 1)"},
            "max_tokens": {"type":"integer","minimum":200,"maximum":20000,"default":2000,"description":"The token budget of the answer (200 to 20000, default 2000; about 4 characters per token). When requested exchanges are left out the answer gives nextFromExchange and nextToExchange: read on with session, from_exchange: nextFromExchange, to_exchange: nextToExchange and context: 0"}
          }
        }
        """).RootElement.Clone();

    private readonly Func<bool> _isActive;
    private readonly TranscriptReadService? _service;
    private readonly ITranscriptSync? _sync;

    /// <param name="isActive">Reads whether the archive is on; evaluated on every call.</param>
    /// <param name="service">The reads; null answers "not set up" while the archive is on.</param>
    /// <param name="sync">The sync-on-read (no embed plan); null syncs nothing.</param>
    public TranscriptGetHandler(Func<bool> isActive, TranscriptReadService? service = null, ITranscriptSync? sync = null)
    {
        _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
        _service = service;
        _sync = sync;
    }

    public string Name => "transcript_get";

    public string Description =>
        "Read archived conversation exchanges: give either id (a chunk id from transcript_search) or session with " +
        "from_exchange and optionally to_exchange. The archive is off by default (turn it on with " +
        "`total-recall transcripts enable`). The answer holds the requested exchanges (flagged `requested`) with " +
        "`context` exchanges on each side, within max_tokens; `truncated` says something asked for was left out, and " +
        "nextFromExchange and nextToExchange say how to read on.";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        var active = _isActive(); // read once per call
        if (!active || _service is null)
            return TranscriptToolAnswers.ForNotSetUp(active);

        var request = ParseArguments(arguments);
        TranscriptSyncCall.Run(_sync, null, Name, ct); // the tail is brought in; the answer does not report it
        var response = _service.Get(request, ct);
        if (response.Error is not null)
            return TranscriptToolAnswers.Text(response.Error, isError: true);

        var json = JsonSerializer.Serialize(TranscriptGetResponseDto.From(response), JsonContext.Default.TranscriptGetResponseDto);
        return TranscriptToolAnswers.Text(json, isError: false);
    }

    /// <summary>
    /// The request of <paramref name="arguments"/>. Missing or null arguments are an empty set, which the service refuses
    /// as neither form; arguments that are not an object, or a value of the wrong type or range, are argument errors.
    /// The combination rules (both or neither form, a range) are the service's.
    /// </summary>
    private static TranscriptGetRequest ParseArguments(JsonElement? arguments)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind == JsonValueKind.Null)
            return new TranscriptGetRequest(
                null, null, null, null, TranscriptReadService.DefaultContext, TranscriptReadService.DefaultMaxTokens);
        if (arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("transcript_get requires a JSON object of arguments");
        var args = arguments.Value;

        var id = ArgumentParsing.ReadOptionalStrictInt64(args, "id", 1, long.MaxValue);
        var session = ArgumentParsing.ReadOptionalString(args, "session");
        var from = ArgumentParsing.ReadOptionalStrictInt(args, "from_exchange", 0, int.MaxValue);
        var to = ArgumentParsing.ReadOptionalStrictInt(args, "to_exchange", 0, int.MaxValue);
        var context = ArgumentParsing.ReadOptionalStrictInt(args, "context", 0, TranscriptReadService.MaxContext)
            ?? TranscriptReadService.DefaultContext;
        var maxTokens = ArgumentParsing.ReadOptionalStrictInt(
                args, "max_tokens", TranscriptReadService.MinMaxTokens, TranscriptReadService.MaxMaxTokens)
            ?? TranscriptReadService.DefaultMaxTokens;
        return new TranscriptGetRequest(id, session, from, to, context, maxTokens);
    }
}
