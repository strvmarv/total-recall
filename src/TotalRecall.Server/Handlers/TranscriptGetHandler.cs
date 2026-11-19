// src/TotalRecall.Server/Handlers/TranscriptGetHandler.cs
//
// transcript_get: read archived exchanges, either one chunk by id (as returned by transcript_search) or a range of a
// session. In this version it only reports whether the archive is on (spec 2026-11-18, section 9). The two
// alternatives are described in the text, not expressed with oneOf, so every client accepts the schema. Registered
// only by OpenSqlite and OpenCortexCore.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
            "to_exchange": {"type":"integer","description":"Last exchange of the range (with session)"},
            "context": {"type":"integer","minimum":0,"maximum":10,"default":1,"description":"Exchanges of context around the result (0 to 10, default 1)"}
          }
        }
        """).RootElement.Clone();

    private readonly Func<bool> _isActive;

    /// <param name="isActive">Reads whether the archive is on; evaluated on every call.</param>
    public TranscriptGetHandler(Func<bool> isActive)
    {
        _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
    }

    public string Name => "transcript_get";

    public string Description =>
        "Read archived conversation exchanges: give either id (a chunk id from transcript_search) or session with from_exchange and to_exchange. The archive is off by default (turn it on with `total-recall transcripts enable`) and reading it is not available in this version yet.";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct) =>
        TranscriptToolAnswers.For(_isActive);
}
