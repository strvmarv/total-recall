// src/TotalRecall.Server/Handlers/TranscriptToolAnswers.cs
//
// The fixed answers of the three archive tools (transcript_search, transcript_get, transcript_sessions): the archive is
// off by default; when it is on, transcript_get and transcript_sessions answer "not available in this version yet"
// (they are Part 4's) and a transcript_search built without a search service says it is not set up (a composition
// that gives none; the registered tool always has one). The gate is a Func<bool>
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
        "{\"enabled\":true,\"available\":false,\"hint\":\"This tool is not available in this version yet.\"}";

    public const string OnSearchNotSetUp =
        "{\"enabled\":true,\"available\":false,\"hint\":\"The archive search is not set up in this server.\"}";

    public static Task<ToolCallResult> For(Func<bool> isActive) => Answer(isActive, OnNotAvailable);

    /// <summary>The answer of transcript_search when no search service was given.</summary>
    public static Task<ToolCallResult> ForSearchWithoutService(Func<bool> isActive) => Answer(isActive, OnSearchNotSetUp);

    private static Task<ToolCallResult> Answer(Func<bool> isActive, string onText)
    {
        var text = isActive() ? onText : Off;
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = text } },
            IsError = false,
        });
    }
}
