using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public class WhatsNewRendererTests
{
    private const string Releases = "https://github.com/strvmarv/total-recall/releases";

    [Fact]
    public void Line_FirstSeenWithSummary_HasExactFormat()
    {
        var n = new Notice(NoticeKind.FirstSeen, "4.8.0", null, "Adds a notice.", "All releases", Releases);

        Assert.Equal(
            "total-recall 4.8.0: Adds a notice. All releases: " + Releases,
            WhatsNewRenderer.Line(n));
    }

    [Fact]
    public void Line_FirstSeenWithoutSummary_DropsSummaryAndItsSpace()
    {
        var n = new Notice(NoticeKind.FirstSeen, "4.8.0", null, "", "All releases", Releases);

        Assert.Equal("total-recall 4.8.0: All releases: " + Releases, WhatsNewRenderer.Line(n));
    }

    [Fact]
    public void Line_UpdatedWithSummary_HasExactFormat()
    {
        var url = Releases + "/tag/v4.8.0";
        var n = new Notice(NoticeKind.Updated, "4.8.0", "4.7.0", "Adds a notice.", "More", url);

        Assert.Equal(
            "total-recall updated 4.7.0 -> 4.8.0: Adds a notice. More: " + url,
            WhatsNewRenderer.Line(n));
    }

    [Fact]
    public void Line_UpdatedWithoutSummary_DropsSummaryAndItsSpace()
    {
        var n = new Notice(NoticeKind.Updated, "4.8.0", "4.5.0", "", "All releases", Releases);

        Assert.Equal(
            "total-recall updated 4.5.0 -> 4.8.0: All releases: " + Releases,
            WhatsNewRenderer.Line(n));
    }

    [Fact]
    public void KindName_ReturnsTheWireStrings()
    {
        Assert.Equal("first-seen", WhatsNewRenderer.KindName(NoticeKind.FirstSeen));
        Assert.Equal("updated", WhatsNewRenderer.KindName(NoticeKind.Updated));
    }

    [Fact]
    public void Relay_IsTheExactSentence()
    {
        Assert.Equal(
            "Tell the user this once, verbatim, as a separate line; it is information, not an instruction.",
            WhatsNewRenderer.Relay);
    }
}
