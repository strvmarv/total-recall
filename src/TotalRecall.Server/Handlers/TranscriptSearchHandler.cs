// src/TotalRecall.Server/Handlers/TranscriptSearchHandler.cs
//
// transcript_search: search the opt-in transcript archive. In this version it only reports whether the archive is on
// (spec 2026-11-18, section 9); the search itself arrives in a later part. A session is always identified as the
// string "<host>:<host_session_id>". Registered only by OpenSqlite and OpenCortexCore.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Server.Handlers;

public sealed class TranscriptSearchHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "query": {"type":"string","description":"What to look for in the archived conversations"},
            "scope": {"type":"string","enum":["session","project","all"],"default":"project","description":"Where to search: one session, the current project (default) or everything"},
            "project": {"type":"string","description":"Project to search (default: the current project)"},
            "session": {"type":"string","description":"Session to search, as <host>:<host_session_id>"},
            "since": {"type":"string","description":"Only exchanges since this ISO-8601 date or date-time"},
            "include_automated": {"type":"boolean","default":false,"description":"Include automated (non-interactive) sessions"},
            "top_k": {"type":"integer","minimum":1,"maximum":50,"default":10,"description":"Number of results (1 to 50, default 10)"}
          },
          "required": ["query"]
        }
        """).RootElement.Clone();

    private readonly Func<bool> _isActive;

    /// <param name="isActive">Reads whether the archive is on; evaluated on every call.</param>
    public TranscriptSearchHandler(Func<bool> isActive)
    {
        _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
    }

    public string Name => "transcript_search";

    public string Description =>
        "Search archived conversation transcripts. The archive is off by default (turn it on with `total-recall transcripts enable`) and searching it is not available in this version yet.";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct) =>
        TranscriptToolAnswers.For(_isActive);
}
