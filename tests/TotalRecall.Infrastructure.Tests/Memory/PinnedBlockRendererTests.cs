using System;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Memory;

public sealed class PinnedBlockRendererTests
{
    private static Entry Make(string id, string content) =>
        new(
            id,
            content,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            ListModule.Empty<string>(),
            0L,
            0L,
            0L,
            0,
            1.0,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            "",
            EntryType.Preference,
            "{}", 0);

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        var (block, ids) = PinnedBlockRenderer.Render(Array.Empty<Entry>(), Array.Empty<Entry>());
        Assert.Equal(string.Empty, block);
        Assert.Empty(ids);
    }

    [Fact]
    public void RendersVerbatim_NoTruncation()
    {
        var longContent = new string('x', 10_000);
        var (block, ids) = PinnedBlockRenderer.Render(new[] { Make("p1", longContent) }, Array.Empty<Entry>());
        Assert.Contains(longContent, block);
        Assert.Single(ids);
        Assert.Equal((Tier.Pinned, ContentType.Memory, "p1"), ids[0]);
    }

    [Fact]
    public void IncludesKnowledge_AfterMemories()
    {
        var (block, ids) = PinnedBlockRenderer.Render(new[] { Make("m1", "mem") }, new[] { Make("k1", "know") });
        Assert.Contains("mem", block);
        Assert.Contains("know", block);
        Assert.Equal(2, ids.Count);
        Assert.Equal(ContentType.Memory, ids[0].Item2);
        Assert.Equal(ContentType.Knowledge, ids[1].Item2);
    }

    [Fact]
    public void StartsWithDirectiveHeader()
    {
        var (block, _) = PinnedBlockRenderer.Render(new[] { Make("m1", "rule") }, Array.Empty<Entry>());
        Assert.StartsWith("## Pinned directives (always follow)", block);
        Assert.Equal("## Pinned directives (always follow)", PinnedBlockRenderer.Header);
    }

    [Fact]
    public void Render_NullMemories_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            PinnedBlockRenderer.Render(null!, Array.Empty<Entry>()));
}
