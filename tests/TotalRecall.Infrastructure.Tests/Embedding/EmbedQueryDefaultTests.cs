using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Embedding;

public sealed class EmbedQueryDefaultTests
{
    [Fact]
    public void EmbedQuery_DefaultImplementation_DelegatesToEmbed()
    {
        // FakeEmbedder does not override EmbedQuery, so the interface default
        // (=> Embed(text)) must apply: query and document vectors are identical.
        // Default interface members dispatch through the interface, so call via IEmbedder.
        IEmbedder e = new FakeEmbedder();
        var q = e.EmbedQuery("hello");
        var d = e.Embed("hello");
        Assert.Equal(d, q);
    }
}
