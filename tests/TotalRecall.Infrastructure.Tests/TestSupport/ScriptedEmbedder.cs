using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

/// <summary>
/// An embedder whose answer per text is scripted: a vector, a wrong-length vector or an exception, optionally a
/// different step per call of the same text (the last step repeats). Every call is logged in order. Unscripted texts
/// get a deterministic 384-float vector derived from the text.
/// </summary>
public sealed class ScriptedEmbedder : IEmbedder
{
    private readonly Dictionary<string, List<Func<float[]>>> _scripts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _callsPerText = new(StringComparer.Ordinal);

    public ScriptedEmbedder(EmbedderDescriptor? descriptor = null)
    {
        Descriptor = descriptor ?? new EmbedderDescriptor("local", "bge-small-en-v1.5", "rev1", 384);
    }

    public EmbedderDescriptor Descriptor { get; }

    /// <summary>Every text passed to <see cref="Embed"/>, in call order.</summary>
    public List<string> Calls { get; } = new();

    /// <summary>Runs after every call is logged and before it answers (a test can advance a clock here).</summary>
    public Action<string>? OnCall { get; set; }

    /// <summary>Scripts the answers for <paramref name="text"/>: one step per call, the last step repeating.</summary>
    public ScriptedEmbedder On(string text, params Func<float[]>[] steps)
    {
        if (steps.Length == 0) throw new ArgumentException("at least one step", nameof(steps));
        _scripts[text] = new List<Func<float[]>>(steps);
        return this;
    }

    /// <summary>Every call with <paramref name="text"/> throws <paramref name="ex"/>.</summary>
    public ScriptedEmbedder Throw(string text, Exception ex) => On(text, () => throw ex);

    public float[] Embed(string text)
    {
        Calls.Add(text);
        OnCall?.Invoke(text);
        _callsPerText.TryGetValue(text, out var n);
        _callsPerText[text] = n + 1;
        if (_scripts.TryGetValue(text, out var steps))
            return steps[Math.Min(n, steps.Count - 1)]();
        return Vector(text);
    }

    /// <summary>A deterministic, non-zero vector of <paramref name="length"/> floats derived from the text.</summary>
    public static float[] Vector(string text, int length = 384)
    {
        var seed = StringComparer.Ordinal.GetHashCode(text) & 0x7fffffff;
        var v = new float[length];
        var x = (uint)(seed | 1);
        for (var i = 0; i < length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            v[i] = (x % 2000) / 1000f - 1f + 0.0001f;
        }
        return v;
    }
}

/// <summary>
/// A clock that moves only when told to. Its timers (for example a <c>CancellationTokenSource</c> built with this
/// provider) fire on the thread that calls <see cref="Advance"/>, once the clock reaches their due time; never by
/// wall-clock time.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    /// <summary>Moves the clock and runs, in due order, every timer callback that came due (periodic ones repeatedly).</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_lock)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next = null;
            lock (_lock)
            {
                foreach (var t in _timers)
                {
                    if (t.Due is { } due && due <= target && (next is null || due < next.Due))
                        next = t;
                }
                if (next is null)
                {
                    _now = target;
                    break;
                }
                _now = next.Due!.Value;
                next.Due = next.Period is { } p && p > TimeSpan.Zero ? _now + p : null;
            }
            next.Fire(); // outside the lock: a callback may read the clock or change timers
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_lock)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        /// <summary>When it fires next; null when it is stopped. Guarded by the owner's lock.</summary>
        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; private set; }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._lock)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
            }
            if (dueTime == TimeSpan.Zero)
                _owner.Advance(TimeSpan.Zero); // due now: fire as a real timer would, without moving the clock
            return true;
        }

        public void Dispose()
        {
            lock (_owner._lock)
            {
                Due = null;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
