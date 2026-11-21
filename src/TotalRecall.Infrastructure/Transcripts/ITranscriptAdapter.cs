using System;
using System.Collections.Generic;
using System.Threading;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>A host's session files, read as the common <see cref="TranscriptRecord"/> (spec section 2).</summary>
public interface ITranscriptAdapter
{
    /// <summary>The host id, e.g. <c>claude-code</c>.</summary>
    string Host { get; }

    /// <summary>The resolved directory the session files live under.</summary>
    string Root { get; }

    /// <summary>Cheap: the session directory exists.</summary>
    bool Detect();

    /// <summary>Every valid session file, duplicates of an id included, unordered in meaning.</summary>
    SessionEnumeration EnumerateSessions();

    /// <summary>Cheap: reads the first lines only.</summary>
    SessionHead ReadHead(SessionSource source);

    /// <summary>True when <paramref name="offset"/> is 0 or the byte before it is <c>0x0A</c>.</summary>
    bool IsLineStart(SessionSource source, long offset);

    /// <summary>The records of the file's complete lines from <paramref name="startOffset"/> (a byte offset at a line start).</summary>
    IEnumerable<TranscriptRecord> ReadRecords(SessionSource source, long startOffset, ReadStats stats, CancellationToken ct);
}

public sealed record SessionSource(string HostSessionId, string Path, long Size, long MtimeMs);

/// <summary><see cref="Skipped"/> counts the names that fail the session id pattern.</summary>
public sealed record SessionEnumeration(IReadOnlyList<SessionSource> Sources, int Skipped);

/// <summary><see cref="Fingerprint"/> is null when the file has no complete first line yet, or its first line is over the limit.</summary>
public sealed record SessionHead(string? Cwd, string? Entrypoint, DateTimeOffset? FirstTimestamp, string? Fingerprint);

public sealed record TranscriptRecord(RecordKind Kind, long StartOffset, long EndOffset, DateTimeOffset? Ts, string? Text, string? ToolName);

public enum RecordKind
{
    UserText,
    AssistantText,
    ToolStub,
    ToolError,
    CompactionBoundary,
    CompactionSummary,
}

/// <summary>Counts what a read passed over without turning it into a record.</summary>
public sealed class ReadStats
{
    public int SkippedLines { get; set; }
}
