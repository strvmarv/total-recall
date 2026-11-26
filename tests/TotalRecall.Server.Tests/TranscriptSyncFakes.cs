// tests/TotalRecall.Server.Tests/TranscriptSyncFakes.cs
//
// Fakes of ITranscriptSync for the transcript tool handler tests (spec 2026-11-24, section 3): one that records each
// call (its embed plan) and answers what the test sets, and one that fails the test if it is ever called.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using TotalRecall.Infrastructure.Transcripts;

internal sealed class RecordingSync : ITranscriptSync
{
    public List<SyncEmbedPlan?> Plans { get; } = new();

    /// <summary>Runs before the answer (to record the order of calls, or to throw).</summary>
    public Action<CancellationToken>? OnCall { get; set; }

    public SyncResult Answer { get; set; } = new(CurrentSession.None, true, null, null);

    public SyncResult SyncCurrent(SyncEmbedPlan? embed, CancellationToken ct)
    {
        Plans.Add(embed);
        OnCall?.Invoke(ct);
        return Answer;
    }
}

internal sealed class UntouchableSync : ITranscriptSync
{
    public int Calls;

    public SyncResult SyncCurrent(SyncEmbedPlan? embed, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        throw new InvalidOperationException("the sync must not be called here");
    }
}
