using System;
using System.Collections.Generic;
using System.Threading;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.TestSupport.Transcripts;

/// <summary>
/// Wraps an <see cref="ITranscriptAdapter"/>, counts the calls to each member and runs a hook when a
/// <see cref="ReadRecords"/> enumeration starts, before its first record is read or yielded: once (the first read only)
/// or on every read. This is how a test makes something happen "during a pass".
/// </summary>
public sealed class HookAdapter : ITranscriptAdapter
{
    private readonly ITranscriptAdapter _inner;
    private readonly Action<SessionSource>? _hook;
    private readonly bool _everyTime;
    private readonly List<long> _readOffsets = new();

    public HookAdapter(ITranscriptAdapter inner, Action<SessionSource>? hook = null, bool everyTime = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _hook = hook;
        _everyTime = everyTime;
    }

    public int DetectCalls { get; private set; }
    public int EnumerateCalls { get; private set; }
    public int ReadHeadCalls { get; private set; }
    public int IsLineStartCalls { get; private set; }
    public int ReadRecordsCalls { get; private set; }

    /// <summary>How many times the hook ran.</summary>
    public int HookFired { get; private set; }

    /// <summary>The start offset of every <see cref="ReadRecords"/> call, in order.</summary>
    public IReadOnlyList<long> ReadOffsets => _readOffsets;

    public string Host => _inner.Host;

    public string Root => _inner.Root;

    public bool Detect()
    {
        DetectCalls++;
        return _inner.Detect();
    }

    public SessionEnumeration EnumerateSessions()
    {
        EnumerateCalls++;
        return _inner.EnumerateSessions();
    }

    public SessionHead ReadHead(SessionSource source)
    {
        ReadHeadCalls++;
        return _inner.ReadHead(source);
    }

    public bool IsLineStart(SessionSource source, long offset)
    {
        IsLineStartCalls++;
        return _inner.IsLineStart(source, offset);
    }

    public IEnumerable<TranscriptRecord> ReadRecords(SessionSource source, long startOffset, ReadStats stats, CancellationToken ct)
    {
        ReadRecordsCalls++;
        _readOffsets.Add(startOffset);
        return Core(source, startOffset, stats, ct);
    }

    private IEnumerable<TranscriptRecord> Core(SessionSource source, long startOffset, ReadStats stats, CancellationToken ct)
    {
        if (_hook is not null && (_everyTime || HookFired == 0))
        {
            HookFired++;          // counted first, so a hook that re-enters the pass does not run again
            _hook(source);
        }

        foreach (var record in _inner.ReadRecords(source, startOffset, stats, ct))
            yield return record;
    }
}
