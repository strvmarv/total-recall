// src/TotalRecall.Server/Handlers/TranscriptToolAnswers.cs
//
// The fixed answers of the three archive tools (transcript_search, transcript_get, transcript_sessions): the archive is
// off by default; when it is on, a tool built without its service says the archive is not set up in this server (a
// composition that gives none; the registered tools always have one). The gate is a Func<bool> read on every call, so
// the answer follows `total-recall transcripts enable|disable` without a restart. The strings are hard-coded JSON (no
// DTO, nothing for the AOT trimmer to see). Text also builds the plain text result every archive tool answers with.

using System;
using System.Threading.Tasks;

namespace TotalRecall.Server.Handlers;

internal static class TranscriptToolAnswers
{
    public const string Off =
        "{\"enabled\":false,\"hint\":\"Run `total-recall transcripts enable` in a terminal to turn the archive on.\"}";

    public const string OnNotSetUp =
        "{\"enabled\":true,\"available\":false,\"hint\":\"The transcript archive is not set up in this server.\"}";

    /// <summary>The answer of an archive tool when no service was given: off, or not set up.</summary>
    public static Task<ToolCallResult> ForNotSetUp(Func<bool> isActive) =>
        Text(isActive() ? OnNotSetUp : Off, isError: false);

    /// <summary>A result of one text content; <paramref name="isError"/> marks an error result.</summary>
    public static Task<ToolCallResult> Text(string text, bool isError) =>
        Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = text } },
            IsError = isError,
        });
}
