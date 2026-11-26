// src/TotalRecall.Server/Handlers/TranscriptSessionsHandler.cs
//
// transcript_sessions: list archived sessions newest first, labelled by their titles, automated ones hidden unless
// asked for (read spec 2026-11-23, section 3). The gate is the sentinel, read once per call: off gives Part 1's off
// answer; with no service (a composition that gives none) the answer says the archive is not set up. Otherwise the
// arguments are parsed here (every one is optional, so missing or null arguments are an empty set; argument errors are
// ArgumentExceptions, which ErrorTranslator turns into "Invalid arguments: ..."), the service opens the archive for the
// call and closes it, an archive that cannot be read is an error result, and the answer is serialised through the
// source-generated JsonContext. Between the parsing and the read, the sync brings the current session's new tail in
// (no embed step; spec 2026-11-24, section 3; TranscriptSyncCall); the answer does not report it. Registered only by
// OpenSqlite and OpenCortexCore.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Server.Handlers;

public sealed class TranscriptSessionsHandler : IToolHandler
{
    private const int DefaultLimit = 20;

    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {"type":"string","description":"Only sessions of this project"},
            "since": {"type":"string","description":"Only sessions active since this ISO-8601 date or date-time (their last activity is at or after it)"},
            "before": {"type":"string","description":"Only sessions whose last activity is before this ISO-8601 date or date-time; page back by passing the oldest listed session's endedAt (else startedAt)"},
            "limit": {"type":"integer","minimum":1,"maximum":100,"default":20,"description":"Number of sessions (1 to 100, default 20)"},
            "include_automated": {"type":"boolean","default":false,"description":"Include automated (non-interactive) sessions"}
          }
        }
        """).RootElement.Clone();

    private readonly Func<bool> _isActive;
    private readonly TranscriptReadService? _service;
    private readonly ITranscriptSync? _sync;

    /// <param name="isActive">Reads whether the archive is on; evaluated on every call.</param>
    /// <param name="service">The reads; null answers "not set up" while the archive is on.</param>
    /// <param name="sync">The sync-on-read (no embed plan); null syncs nothing.</param>
    public TranscriptSessionsHandler(Func<bool> isActive, TranscriptReadService? service = null, ITranscriptSync? sync = null)
    {
        _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
        _service = service;
        _sync = sync;
    }

    public string Name => "transcript_sessions";

    public string Description =>
        "List archived conversation sessions, newest last activity first, each with its title. The archive is off by " +
        "default (turn it on with `total-recall transcripts enable`). Automated sessions are left out unless " +
        "include_automated is true (`automatedHidden` counts them); `total` counts the matches and `more` those not listed.";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        var active = _isActive(); // read once per call
        if (!active || _service is null)
            return TranscriptToolAnswers.ForNotSetUp(active);

        var request = ParseArguments(arguments);
        TranscriptSyncCall.Run(_sync, null, Name, ct); // the tail is brought in; the answer does not report it
        var response = _service.Sessions(request, ct);
        if (response.Error is not null)
            return TranscriptToolAnswers.Text(response.Error, isError: true);

        var json = JsonSerializer.Serialize(TranscriptSessionsResponseDto.From(response), JsonContext.Default.TranscriptSessionsResponseDto);
        return TranscriptToolAnswers.Text(json, isError: false);
    }

    private static TranscriptSessionsRequest ParseArguments(JsonElement? arguments)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind == JsonValueKind.Null)
            return new TranscriptSessionsRequest(null, null, null, DefaultLimit, false);
        if (arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("transcript_sessions requires a JSON object of arguments");
        var args = arguments.Value;

        var project = ArgumentParsing.ReadOptionalString(args, "project");
        var sinceText = ArgumentParsing.ReadOptionalString(args, "since");
        DateTimeOffset? since = sinceText is null ? null : TranscriptSearchService.ParseSince(sinceText, "since");
        var beforeText = ArgumentParsing.ReadOptionalString(args, "before");
        DateTimeOffset? before = beforeText is null ? null : TranscriptSearchService.ParseSince(beforeText, "before");
        var limit = ArgumentParsing.ReadOptionalStrictInt(args, "limit", 1, TranscriptReadService.MaxSessionLimit) ?? DefaultLimit;
        var includeAutomated = ArgumentParsing.ReadOptionalBool(args, "include_automated") ?? false;
        return new TranscriptSessionsRequest(project, since, before, limit, includeAutomated);
    }
}
