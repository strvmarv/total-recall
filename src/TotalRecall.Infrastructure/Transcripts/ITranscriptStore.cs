using System;
using System.Collections.Generic;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>A forgotten session's exclusion record: identifiers and the time, no text.</summary>
public sealed record ForgottenSession(string Host, string HostSessionId, string At);

/// <summary>A forgotten project's exclusion record: its key, the directories its removed sessions had, and the time.</summary>
public sealed record ForgottenProject(string ProjectKey, IReadOnlyList<string> Roots, string At);

/// <summary>
/// What <see cref="ITranscriptStore.Status"/> reports. <paramref name="Location"/> is the separate file's path, or
/// <c>main database (&lt;path&gt;)</c>. <paramref name="SessionsByKind"/> always holds <c>interactive</c> and
/// <c>automated</c>. <paramref name="IngestNotBefore"/> is the global cutoff (ISO-8601 UTC), or null.
/// </summary>
public sealed record TranscriptStatus(
    int SchemaVersion,
    string Location,
    long Sessions,
    long Chunks,
    IReadOnlyDictionary<string, long> SessionsByKind,
    IReadOnlyList<ForgottenSession> ForgottenSessions,
    IReadOnlyList<ForgottenProject> ForgottenProjects,
    string? IngestNotBefore)
{
    /// <summary>Sessions and chunks per project key and kind, most chunks first, then key, then kind; returned whole.</summary>
    public IReadOnlyList<ProjectKindCount> ByProjectAndKind { get; init; } = Array.Empty<ProjectKindCount>();
}

/// <summary>How many sessions and chunks a forget would remove (shown before the command asks).</summary>
public sealed record ForgetCount(long Sessions, long Chunks);

/// <summary>
/// What a forget removed. <paramref name="WalTruncated"/> is false when the write-ahead log could not be checkpointed
/// and truncated (another connection holds the database); then <paramref name="Warning"/> says the rows are deleted
/// but the log may still hold their text, and the caller must not report a clean delete.
/// </summary>
public sealed record ForgetResult(long Sessions, long Chunks, bool WalTruncated, string? Warning);

/// <summary>
/// The opt-in transcript archive (spec section 6). Owns its own connection. Every forget is one transaction followed by
/// the delete hygiene of section 7; exclusion records hold identifiers and directories, never conversation text.
/// </summary>
public interface ITranscriptStore : IDisposable
{
    TranscriptStatus Status();

    ForgetCount CountSession(string host, string hostSessionId);
    ForgetCount CountProject(string projectKey);
    ForgetCount CountAll();

    ForgetResult ForgetSession(string host, string hostSessionId);
    ForgetResult ForgetProject(string projectKey);
    ForgetResult ForgetAll(DateTimeOffset now);

    bool UnforgetSession(string host, string hostSessionId);
    bool UnforgetProject(string projectKey);
    bool ClearCutoff();

    /// <summary>
    /// True when a <c>session</c> record matches, or a <c>project</c> record matches by key or has a root that
    /// <paramref name="cwd"/> lies under (a directory-boundary prefix test, case-sensitive except on Windows).
    /// </summary>
    bool IsExcluded(string host, string hostSessionId, string? projectKey, string? cwd);

    /// <summary>The stored state of one session, or null when it has no row.</summary>
    SessionIngestState? GetIngestState(string host, string hostSessionId);

    /// <summary>The global ingest cutoff (parsed and as stored), or null when none is set.</summary>
    IngestCutoff? IngestNotBefore();

    /// <summary>
    /// Writes one session pass in one immediate transaction after checking, inside it, that the session is not
    /// excluded, the cutoff still equals the one the pass was built under and the session row still equals the pass's
    /// expected prior state. Anything else writes nothing and reports why.
    /// </summary>
    IngestOutcome ApplyIngest(SessionIngest ingest);

    /// <summary>True when a cutoff is set and <paramref name="timestamp"/> is earlier than it.</summary>
    bool IsBeforeCutoff(DateTimeOffset timestamp);
}
