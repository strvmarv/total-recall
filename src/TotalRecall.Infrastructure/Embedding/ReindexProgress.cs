namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Thread-safe live snapshot of an in-flight background re-index. Created in
/// composition, written by the background worker, read by session_start and the
/// status tool. Portable progress surfacing rides on this.
/// </summary>
public sealed class ReindexProgress
{
    public enum Phase { Idle, Running, Completed, Failed }

    private readonly object _gate = new();
    private Phase _state = Phase.Idle;
    private long _done;
    private long _total;
    private string _model = "";
    private long _startedAtUnixMs;
    private string? _error;

    public Phase State { get { lock (_gate) return _state; } }
    public long Done { get { lock (_gate) return _done; } }
    public long Total { get { lock (_gate) return _total; } }
    public string Model { get { lock (_gate) return _model; } }
    public string? Error { get { lock (_gate) return _error; } }

    public void BeginRunning(long total, string model, long startedAtUnixMs)
    {
        lock (_gate)
        {
            _state = Phase.Running; _total = total; _model = model;
            _startedAtUnixMs = startedAtUnixMs; _done = 0; _error = null;
        }
    }

    public void Advance(long delta) { lock (_gate) _done += delta; }
    public void Complete() { lock (_gate) _state = Phase.Completed; }
    public void Fail(string error) { lock (_gate) { _state = Phase.Failed; _error = error; } }

    public ReindexProgressSnapshot Snapshot()
    {
        lock (_gate)
            return new ReindexProgressSnapshot(_state, _done, _total, _model, _startedAtUnixMs, _error);
    }
}

public readonly record struct ReindexProgressSnapshot(
    ReindexProgress.Phase State, long Done, long Total, string Model, long StartedAtUnixMs, string? Error);
