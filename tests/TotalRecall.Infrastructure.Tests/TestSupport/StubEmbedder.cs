using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

/// <summary>IEmbedder whose Descriptor is fully controllable; Embed returns zeros.</summary>
public sealed class StubEmbedder : IEmbedder
{
    public StubEmbedder(string provider, string model, string revision, int dims)
        => Descriptor = new EmbedderDescriptor(provider, model, revision, dims);
    public EmbedderDescriptor Descriptor { get; }
    public float[] Embed(string text) => new float[Descriptor.Dimensions];
}
