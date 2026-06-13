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

public sealed class EmbedderFingerprintCheckTests
{
    private static StubEmbedder MiniLm() => new("local", "all-MiniLM-L6-v2", "main", 384);
    private static StubEmbedder Bge() => new("local", "bge-small-en-v1.5", "5c38ec7", 384);

    [Fact]
    public void Check_EmptyMeta_IsUnstamped_StoredNull()
    {
        var meta = new InMemoryMetaStore();

        var state = EmbedderFingerprint.Check(meta, Bge(), out var stored);

        Assert.Equal(EmbedderFingerprint.FingerprintState.Unstamped, state);
        Assert.Null(stored);
    }

    [Fact]
    public void Check_SameEmbedderAfterStamp_IsMatch()
    {
        var meta = new InMemoryMetaStore();
        EmbedderFingerprint.EnsureMatches(meta, Bge()); // stamps bge

        var state = EmbedderFingerprint.Check(meta, Bge(), out var stored);

        Assert.Equal(EmbedderFingerprint.FingerprintState.Match, state);
        Assert.NotNull(stored);
        Assert.Equal("bge-small-en-v1.5", stored!.Model);
    }

    [Fact]
    public void Check_DifferentEmbedderAfterStamp_IsMismatch_StoredIsOriginal()
    {
        var meta = new InMemoryMetaStore();
        EmbedderFingerprint.EnsureMatches(meta, MiniLm()); // stamps MiniLM

        var state = EmbedderFingerprint.Check(meta, Bge(), out var stored);

        Assert.Equal(EmbedderFingerprint.FingerprintState.Mismatch, state);
        Assert.NotNull(stored);
        Assert.Equal("all-MiniLM-L6-v2", stored!.Model); // stored = the MiniLM descriptor
        Assert.Equal("main", stored.Revision);
        Assert.Equal(384, stored.Dimensions);
    }

    // Regression guards: the EnsureMatches refactor over Check must preserve behavior.
    [Fact]
    public void EnsureMatches_Unstamped_StampsConfiguredEmbedder()
    {
        var meta = new InMemoryMetaStore();

        EmbedderFingerprint.EnsureMatches(meta, Bge());

        Assert.Equal("bge-small-en-v1.5", meta.GetMeta("embed.model"));
        Assert.Equal("local", meta.GetMeta("embed.provider"));
        Assert.Equal("5c38ec7", meta.GetMeta("embed.revision"));
        Assert.Equal("384", meta.GetMeta("embed.dimensions"));
    }

    [Fact]
    public void EnsureMatches_Mismatch_Throws()
    {
        var meta = new InMemoryMetaStore();
        EmbedderFingerprint.EnsureMatches(meta, MiniLm());

        Assert.Throws<EmbedderFingerprintMismatchException>(
            () => EmbedderFingerprint.EnsureMatches(meta, Bge()));
    }

    [Fact]
    public void EnsureMatches_Match_DoesNotThrow_AndLeavesStampIntact()
    {
        var meta = new InMemoryMetaStore();
        EmbedderFingerprint.EnsureMatches(meta, Bge());

        EmbedderFingerprint.EnsureMatches(meta, Bge()); // no throw

        Assert.Equal("bge-small-en-v1.5", meta.GetMeta("embed.model"));
    }
}
