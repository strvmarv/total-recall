using System;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

/// <summary>IEmbedder returning a normalized constant 384-vector (value-tagged in the descriptor).</summary>
public sealed class ConstantEmbedder : IEmbedder
{
    private readonly float _v;

    public ConstantEmbedder(float v)
    {
        _v = v;
        Descriptor = new EmbedderDescriptor(
            "test",
            "const-" + v.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "",
            384);
    }

    public EmbedderDescriptor Descriptor { get; }

    public float[] Embed(string text)
    {
        var a = new float[384];
        for (int i = 0; i < 384; i++)
        {
            a[i] = _v;
        }

        // L2 normalize so it's a valid unit vector.
        double s = 0;
        foreach (var x in a)
        {
            s += (double)x * x;
        }
        var n = (float)Math.Sqrt(s);
        if (n > 0)
        {
            for (int i = 0; i < 384; i++)
            {
                a[i] /= n;
            }
        }

        return a;
    }
}
