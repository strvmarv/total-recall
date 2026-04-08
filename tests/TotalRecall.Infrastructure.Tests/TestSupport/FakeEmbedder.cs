using System;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

/// <summary>
/// Deterministic 384-dim embedder for importer/ingestion tests. Avoids
/// loading the real ONNX model (covered by OnnxEmbedderIntegrationTests).
/// The vector is a stable function of the input text length so different
/// inputs reliably produce different vectors.
/// </summary>
public sealed class FakeEmbedder : IEmbedder
{
    public float[] Embed(string text)
    {
        var v = new float[384];
        var len = text?.Length ?? 0;
        for (var i = 0; i < 384; i++)
        {
            v[i] = (float)Math.Sin(len * (i + 1) / 384.0);
        }
        return v;
    }
}
