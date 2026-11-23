using System;
using System.Collections.Generic;
using System.Text;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.TestSupport.Transcripts;

/// <summary>
/// A fake <see cref="IEmbedder"/> that records every text passed to <see cref="Embed"/> and <see cref="EmbedQuery"/>
/// (in order, in <see cref="Calls"/>) and returns deterministic, L2-normalised vectors. The provider name and the
/// descriptor's dimension count are configurable, and the returned vector length defaults to the descriptor's, so a
/// remote provider that ignores the requested size is <c>new("openai", 384, 768)</c>. Uses only public API.
/// </summary>
public sealed class RecordingEmbedder : IEmbedder
{
    private readonly object _gate = new();
    private readonly List<string> _calls = new();
    private readonly int _returned;

    public RecordingEmbedder(string provider, int descriptorDimensions, int? returnedDimensions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        _returned = returnedDimensions ?? descriptorDimensions;
        if (descriptorDimensions <= 0 || _returned <= 0) throw new ArgumentOutOfRangeException(nameof(descriptorDimensions));
        Descriptor = new EmbedderDescriptor(provider, "recording-model", "1", descriptorDimensions);
    }

    public EmbedderDescriptor Descriptor { get; }

    /// <summary>A snapshot of every text embedded so far, documents and queries alike.</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return _calls.ToArray(); }
    }

    public float[] Embed(string text) => Record(text, query: false);

    public float[] EmbedQuery(string text) => Record(text, query: true);

    private float[] Record(string text, bool query)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate) _calls.Add(text);
        return Vector(text, query, _returned);
    }

    /// <summary>The same text always gives the same vector; a query and a document of the same text differ.</summary>
    private static float[] Vector(string text, bool query, int length)
    {
        // FNV-1a over the UTF-8 bytes seeds an xorshift generator.
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        var state = hash ^ (query ? 0x9E3779B97F4A7C15UL : 0UL);
        if (state == 0) state = 1;

        var v = new float[length];
        double sum = 0;
        for (var i = 0; i < length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            var x = (float)(((state >> 11) / (double)(1UL << 53)) * 2.0 - 1.0);
            v[i] = x;
            sum += (double)x * x;
        }
        var norm = (float)Math.Sqrt(sum);
        if (norm > 0)
            for (var i = 0; i < length; i++) v[i] /= norm;
        return v;
    }
}
