using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Memory;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>How far a backfill run is: <see cref="Done"/> of <see cref="Total"/> sessions processed.</summary>
public sealed record BackfillProgress(int Done, int Total);

/// <summary>
/// What a backfill run did. <see cref="Seen"/> counts the valid session files and the names the enumeration skipped;
/// <see cref="Skipped"/> adds the duplicate ids that lost and the files with no complete first line yet;
/// <see cref="ChangedSessions"/> lists <c>&lt;host&gt;:&lt;id&gt;</c> of each session whose file the host replaced or truncated.
/// </summary>
public sealed record BackfillSummary(
    int Seen,
    int New,
    int Updated,
    int Unchanged,
    int Excluded,
    int Empty,
    int Skipped,
    int Changed,
    int Retry,
    int Failed,
    int ChunksNew,
    int ChunksChanged,
    int ChunksUnchanged,
    int ChunksRemoved,
    int SkippedLines,
    IReadOnlyList<string> ChangedSessions,
    bool Cancelled);

/// <summary>
/// A loop around <see cref="TranscriptIngester"/> over every session of one adapter, newest file first (spec section 1).
/// It never touches the store itself, and one failing file is logged, counted and never stops the others.
/// </summary>
public sealed class TranscriptBackfill
{
    private readonly ITranscriptAdapter _adapter;
    private readonly TranscriptIngester _ingester;

    /// <param name="userHome">The user's home directory; null means the UserProfile folder, resolved here ("" when that is empty).</param>
    public TranscriptBackfill(ITranscriptStore store, ITranscriptAdapter adapter, ProjectResolver? resolver = null, string? userHome = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
        var home = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? string.Empty;
        _ingester = new TranscriptIngester(store, adapter, resolver ?? new ProjectResolver(), home);
    }

    public BackfillSummary Run(CancellationToken ct, Action<BackfillProgress>? progress = null)
    {
        var counts = new Counts();
        if (!_adapter.Detect())
            return counts.ToSummary(seen: 0, cancelled: false);

        var enumeration = _adapter.EnumerateSessions();
        var seen = enumeration.Sources.Count + enumeration.Skipped;
        counts.Skipped += enumeration.Skipped;

        var work = ChooseSources(enumeration.Sources, counts)
            .OrderByDescending(s => s.MtimeMs)
            .ThenBy(s => s.Path, StringComparer.Ordinal)
            .ToList();

        var cancelled = false;
        var done = 0;
        foreach (var source in work)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            try
            {
                counts.Add(_ingester.IngestSession(source, ct), _adapter.Host, source.HostSessionId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ExceptionLogger.LogChain("[total-recall] transcripts backfill: " + source.Path, ex);
                counts.Failed++;
            }

            done++;
            progress?.Invoke(new BackfillProgress(done, work.Count));
        }

        return counts.ToSummary(seen, cancelled);
    }

    /// <summary>
    /// One source per id: the file whose path is the stored <c>source_path</c> of the session, else the newest by mtime
    /// (then by path, so the choice is stable). Every loser is counted skipped.
    /// </summary>
    private List<SessionSource> ChooseSources(IReadOnlyList<SessionSource> sources, Counts counts)
    {
        var chosen = new List<SessionSource>(sources.Count);
        foreach (var group in sources.GroupBy(s => s.HostSessionId, StringComparer.Ordinal))
        {
            var candidates = group.ToList();
            if (candidates.Count == 1)
            {
                chosen.Add(candidates[0]);
                continue;
            }

            var stored = StoredPathOrNull(group.Key);
            var winner = candidates.FirstOrDefault(c => stored is not null && string.Equals(c.Path, stored, StringComparison.Ordinal))
                         ?? candidates.OrderByDescending(c => c.MtimeMs).ThenBy(c => c.Path, StringComparer.Ordinal).First();
            chosen.Add(winner);
            counts.Skipped += candidates.Count - 1;
        }

        return chosen;
    }

    private string? StoredPathOrNull(string hostSessionId)
    {
        try
        {
            return _ingester.StoredSourcePath(hostSessionId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain("[total-recall] transcripts backfill: stored path of " + hostSessionId, ex);
            return null;
        }
    }

    private sealed class Counts
    {
        public int New, Updated, Unchanged, Excluded, Empty, Skipped, Changed, Retry, Failed;
        public int ChunksNew, ChunksChanged, ChunksUnchanged, ChunksRemoved, SkippedLines;
        private readonly List<string> _changedSessions = new();

        public void Add(SessionIngestResult r, string host, string id)
        {
            switch (r.Status)
            {
                case SessionIngestStatus.New: New++; break;
                case SessionIngestStatus.Updated: Updated++; break;
                case SessionIngestStatus.Unchanged: Unchanged++; break;
                case SessionIngestStatus.Excluded: Excluded++; break;
                case SessionIngestStatus.Empty: Empty++; break;
                case SessionIngestStatus.Skipped: Skipped++; break;
                case SessionIngestStatus.Changed:
                    Changed++;
                    _changedSessions.Add(host + ":" + id);
                    break;
                case SessionIngestStatus.Retry: Retry++; break;
            }

            ChunksNew += r.ChunksNew;
            ChunksChanged += r.ChunksChanged;
            ChunksUnchanged += r.ChunksUnchanged;
            ChunksRemoved += r.ChunksRemoved;
            SkippedLines += r.SkippedLines;
        }

        public BackfillSummary ToSummary(int seen, bool cancelled) =>
            new(seen, New, Updated, Unchanged, Excluded, Empty, Skipped, Changed, Retry, Failed,
                ChunksNew, ChunksChanged, ChunksUnchanged, ChunksRemoved, SkippedLines, _changedSessions.ToArray(), cancelled);
    }
}
