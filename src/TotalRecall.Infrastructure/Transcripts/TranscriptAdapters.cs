using System.Collections.Generic;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// The hosts whose session files the <c>session_start</c> pass archives (search the archive, spec section 4, call 5).
/// Claude Code today; a later part adds pi here. Each call builds new adapters (construction reads nothing but the
/// environment that locates the host's directory).
/// </summary>
public static class TranscriptAdapters
{
    public static IReadOnlyList<ITranscriptAdapter> All() => new ITranscriptAdapter[] { new ClaudeCodeAdapter() };
}
