using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Embedding;

public sealed class EmbedderFingerprintRestampTests
{
    [Fact]
    public void Restamp_OverwritesExistingFingerprint()
    {
        var meta = new InMemoryMetaStore();
        EmbedderFingerprint.EnsureMatches(meta, new StubEmbedder("local", "all-MiniLM-L6-v2", "main", 384));
        // Different model now — EnsureMatches would throw; Restamp must not.
        EmbedderFingerprint.Restamp(meta, new StubEmbedder("local", "bge-small-en-v1.5", "5c38ec7", 384));
        // A subsequent EnsureMatches against bge must now succeed (no mismatch).
        EmbedderFingerprint.EnsureMatches(meta, new StubEmbedder("local", "bge-small-en-v1.5", "5c38ec7", 384));
        Assert.Equal("bge-small-en-v1.5", meta.GetMeta("embed.model"));
    }
}
