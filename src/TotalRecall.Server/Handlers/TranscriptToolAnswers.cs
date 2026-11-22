// src/TotalRecall.Server/Handlers/TranscriptToolAnswers.cs
//
// The fixed answers of the three archive tools (transcript_search, transcript_get, transcript_sessions): the archive is
// off by default; when it is on, transcript_get and transcript_sessions (and a transcript_search built without a
// search service) answer "not available". The gate is a Func<bool>
// read on every call, so the answer follows `total-recall transcripts enable|disable` without a restart. The strings
// are hard-coded JSON (no DTO, nothing for the AOT trimmer to see).

using System;
using System.Threading.Tasks;

namespace TotalRecall.Server.Handlers;

internal static class TranscriptToolAnswers
{
    public const string Off =
        "{\"enabled\":false,\"hint\":\"Run `total-recall transcripts enable` in a terminal to turn the archive on.\"}";

    public const string OnNotAvailable =
        "{\"enabled\":true,\"available\":false,\"hint\":\"Searching the archive is not available in this version yet.\"}";

    public static Task<ToolCallResult> For(Func<bool> isActive)
    {
        var text = isActive() ? OnNotAvailable : Off;
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = text } },
            IsError = false,
        });
    }
}
