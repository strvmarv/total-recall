using TotalRecall.Infrastructure.Memory;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

public sealed class PreviewTextTests
{
    [Fact]
    public void Collapse_CollapsesWhitespace_AndTrims()
    {
        Assert.Equal("a b c", PreviewText.Collapse("  a\n\tb   c  ", 100));
    }

    [Fact]
    public void Collapse_Truncates_WithEllipsis()
    {
        var result = PreviewText.Collapse("abcdefghij", 5);
        Assert.Equal("abcde…", result);
    }

    [Fact]
    public void Collapse_ShortString_NoEllipsis()
    {
        Assert.Equal("hello", PreviewText.Collapse("hello", 50));
    }

    [Fact]
    public void Collapse_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal("", PreviewText.Collapse("", 50));
        Assert.Equal("", PreviewText.Collapse(null, 50));
    }
}
