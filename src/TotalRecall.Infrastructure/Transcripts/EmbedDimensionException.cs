using System;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// The embed worker throws this when a returned vector (the probe's included) does not have the archive's width, before
/// anything is stored, so a wrong length is classed as "the embedder cannot run" (spec section 3, rule 3), never as a
/// failure of one chunk.
/// </summary>
public sealed class EmbedDimensionException : Exception
{
    public EmbedDimensionException(int actual, int expected)
        : base($"the embedder returned a vector of {actual} floats; the archive holds {expected}")
    {
        Actual = actual;
        Expected = expected;
    }

    /// <summary>The length of the vector the embedder returned.</summary>
    public int Actual { get; }

    /// <summary>The archive's vector width.</summary>
    public int Expected { get; }
}
