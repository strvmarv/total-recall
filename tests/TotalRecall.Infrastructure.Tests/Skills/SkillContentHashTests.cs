using TotalRecall.Infrastructure.Skills;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Skills;

public class SkillContentHashTests
{
    [Fact]
    public void Compute_IsStableAcrossWhitespaceNormalization()
    {
        var a = SkillContentHash.Compute("hello\r\nworld\n");
        var b = SkillContentHash.Compute("hello\nworld\n");
        Assert.Equal(a, b);
        Assert.NotEmpty(a);
    }

    [Fact]
    public void Compute_DifferentForDifferentContent()
    {
        Assert.NotEqual(SkillContentHash.Compute("a"), SkillContentHash.Compute("b"));
    }
}
