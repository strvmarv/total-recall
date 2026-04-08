using System;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

/// <summary>
/// Embedder that throws on the Nth <see cref="Embed"/> call. Used to
/// exercise per-file error handling in importer tests. All other calls
/// produce a deterministic 384-dim vector matching <see cref="FakeEmbedder"/>.
/// </summary>
public sealed class ThrowingEmbedder : IEmbedder
{
    private readonly int _throwOnCall;
    private int _calls;

    public ThrowingEmbedder(int throwOnCall = 2)
    {
        _throwOnCall = throwOnCall;
    }

    public float[] Embed(string text)
    {
        _calls++;
        if (_calls == _throwOnCall)
            throw new InvalidOperationException("synthetic embed failure");
        var v = new float[384];
        var len = text?.Length ?? 0;
        for (var i = 0; i < 384; i++)
            v[i] = (float)Math.Sin(len * (i + 1) / 384.0);
        return v;
    }
}
