using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class SessionTitleTests
{
    [Fact]
    public void Title_FoldsAndCuts120()
    {
        Assert.Equal("fix the build now", SessionTitle.From("  fix the\n\nbuild\tnow \n"));

        var title = SessionTitle.From(new string('a', 200) + "\nmore");
        Assert.Equal(new string('a', 120) + "...", title);
        Assert.Equal(120, SessionTitle.Limit);

        Assert.Equal(new string('b', 120), SessionTitle.From(new string('b', 120)));
    }

    [Fact]
    public void Title_MarkerAtLimit_CutBeforeMarker()
    {
        var marker = "[redacted:token]";
        var text = new string('a', 115) + marker + " tail";
        var title = SessionTitle.From(text);
        Assert.Equal(new string('a', 115) + "...", title);
        Assert.DoesNotContain("[redacted", title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void Title_NullOrBlank_IsNull(string? text) => Assert.Null(SessionTitle.From(text));
}
