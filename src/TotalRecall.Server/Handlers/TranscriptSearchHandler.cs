// src/TotalRecall.Server/Handlers/TranscriptSearchHandler.cs
//
// transcript_search: search the opt-in transcript archive by meaning and by words (search the archive, spec section
// 5). The gate is the sentinel, read on every call: off gives Part 1's off answer. With no service (a composition that
// gives none) the answer says the search is not set up. Otherwise the arguments are parsed here (argument errors are
// ArgumentExceptions, which ErrorTranslator turns into "Invalid arguments: ..."), the service opens the archive for the
// call and closes it, and the answer is serialised through the source-generated JsonContext. A session is always
// identified as "<host>:<host_session_id>". Registered only by OpenSqlite and OpenCortexCore.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Server.Handlers;

public sealed class TranscriptSearchHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "query": {"type":"string","description":"What to look for in the archived conversations"},
            "scope": {"type":"string","enum":["session","project","all"],"description":"Where to search: one session, one project or everything; when omitted, session if `session` is given, else project if `project` is given, else all"},
            "project": {"type":"string","description":"Project key to search, as archived (for example owner/repo); without `scope` it selects project scope"},
            "session": {"type":"string","description":"Session to search, as <host>:<host_session_id>"},
            "since": {"type":"string","description":"Only exchanges since this ISO-8601 date or date-time"},
            "include_automated": {"type":"boolean","default":false,"description":"Include automated (non-interactive) sessions"},
            "top_k": {"type":"integer","minimum":1,"maximum":50,"default":10,"description":"Number of results (1 to 50, default 10)"}
          },
          "required": ["query"]
        }
        """).RootElement.Clone();

    private readonly Func<bool> _isActive;
    private readonly TranscriptSearchService? _service;

    /// <param name="isActive">Reads whether the archive is on; evaluated on every call.</param>
    /// <param name="service">The search; null answers "not set up" while the archive is on.</param>
    public TranscriptSearchHandler(Func<bool> isActive, TranscriptSearchService? service = null)
    {
        _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
        _service = service;
    }

    public string Name => "transcript_search";

    public string Description =>
        "Search archived conversation transcripts by meaning and by words. The archive is off by default (turn it on with " +
        "`total-recall transcripts enable`). With no `scope`, a `session` argument searches that session, else a `project` " +
        "argument searches that project, else everything is searched. Automated sessions are left out unless " +
        "include_automated is true (a named session is always searched). Each hit carries the chunk text, its session, " +
        "which searches found it (`via`) and a relative score (the top hit is 1.0); `pendingEmbedding` counts the chunks " +
        "in scope that only the word search could cover.";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        var active = _isActive(); // read once per call
        if (!active || _service is null)
            return TranscriptToolAnswers.ForSearchWithoutService(() => active);

        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("transcript_search requires a JSON object of arguments with a query");
        var args = arguments.Value;

        var query = ArgumentParsing.ReadRequiredString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("query must be a non-empty string");

        var scope = ArgumentParsing.ReadOptionalString(args, "scope");
        var project = ArgumentParsing.ReadOptionalString(args, "project");
        var session = ArgumentParsing.ReadOptionalString(args, "session");
        if (!string.IsNullOrWhiteSpace(session))
            TranscriptSearchService.SplitSession(session);
        var sinceText = ArgumentParsing.ReadOptionalString(args, "since");
        DateTimeOffset? since = sinceText is null ? null : TranscriptSearchService.ParseSince(sinceText);
        var includeAutomated = ReadOptionalBool(args, "include_automated") ?? false;
        var topK = ArgumentParsing.ReadOptionalInt(args, "top_k", 1, 50) ?? 10;

        var response = _service.Search(
            new TranscriptSearchRequest(query, scope, project, session, since, includeAutomated, topK), ct);

        if (response.Error is not null)
        {
            return Task.FromResult(new ToolCallResult
            {
                Content = new[] { new ToolContent { Type = "text", Text = response.Error } },
                IsError = true,
            });
        }

        var json = JsonSerializer.Serialize(TranscriptSearchResponseDto.From(response), JsonContext.Default.TranscriptSearchResponseDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = json } },
            IsError = false,
        });
    }

    private static bool? ReadOptionalBool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"{name} must be a boolean"),
        };
    }
}
