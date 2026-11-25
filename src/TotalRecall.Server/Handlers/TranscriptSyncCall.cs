// src/TotalRecall.Server/Handlers/TranscriptSyncCall.cs
//
// The sync-on-read call the three archive tools make (spec 2026-11-24, section 3): after the sentinel check and the
// handler's own argument parsing, before the read service. The current session's new tail is brought into the archive
// (and, for transcript_search, embedded within a small budget through the embed plan). The sync never throws by
// contract; anything it throws anyway (or an embed plan that cannot be built) is logged to stderr and the read goes on
// without the current session, with a note that its newest text may be missing. The call's own cancellation passes.

using System;
using System.Threading;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Server.Handlers;

internal static class TranscriptSyncCall
{
    /// <summary>The note of a catch-up that threw: the sync's own note for a failed catch-up.</summary>
    public const string FailedNote = TranscriptSyncService.NoteFailed;

    /// <summary>
    /// Runs <paramref name="sync"/> (nothing when null) with the plan of <paramref name="embedPlan"/> (none when null)
    /// and returns the current session as a search sees it and the note of an incomplete catch-up. A throw maps to
    /// <c>none</c> with <see cref="FailedNote"/>; an <see cref="OperationCanceledException"/> of <paramref name="ct"/> is
    /// rethrown.
    /// </summary>
    public static (CurrentSessionInfo? Current, string? Note) Run(
        ITranscriptSync? sync, Func<SyncEmbedPlan?>? embedPlan, string tool, CancellationToken ct)
    {
        if (sync is null) return (null, null);
        try
        {
            var plan = embedPlan?.Invoke();
            var result = sync.SyncCurrent(plan, ct);
            return (new CurrentSessionInfo(result.Current.Wire, result.Current.Ref, result.ProjectKey), result.Note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            try { ExceptionLogger.LogChain("[total-recall] " + tool + ": the catch-up of the current session", ex); }
            catch { /* logging must not throw */ }
            return (new CurrentSessionInfo("none", null, null), FailedNote);
        }
    }
}
