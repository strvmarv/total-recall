using System;
using System.Collections.Generic;
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

/// <summary>A clock that moves only when told to.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
