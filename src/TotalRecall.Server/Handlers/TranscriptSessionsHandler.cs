// src/TotalRecall.Server/Handlers/TranscriptSessionsHandler.cs
//
// transcript_sessions: list archived sessions. In this version it only reports whether the archive is on (spec
// 2026-11-18, section 9). Registered only by OpenSqlite and OpenCortexCore.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Server.Handlers;

public sealed class TranscriptSessionsHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "project": {"type":"string","description":"Only sessions of this project"},
            "since": {"type":"string","description":"Only sessions since this ISO-8601 date or date-time"},
            "limit": {"type":"integer","minimum":1,"maximum":100,"default":20,"description":"Number of sessions (1 to 100, default 20)"},
            "include_automated": {"type":"boolean","default":false,"description":"Include automated (non-interactive) sessions"}
          }
        }
        """).RootElement.Clone();

    private readonly Func<bool> _isActive;

    /// <param name="isActive">Reads whether the archive is on; evaluated on every call.</param>
    public TranscriptSessionsHandler(Func<bool> isActive)
    {
        _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
    }

    public string Name => "transcript_sessions";

    public string Description =>
        "List archived conversation sessions. The archive is off by default (turn it on with `total-recall transcripts enable`) and listing it is not available in this version yet.";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct) =>
        TranscriptToolAnswers.For(_isActive);
}
