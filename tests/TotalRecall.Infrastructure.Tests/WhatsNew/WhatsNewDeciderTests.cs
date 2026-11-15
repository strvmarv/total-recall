using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public class WhatsNewDeciderTests
{
    private const string Releases = "https://github.com/strvmarv/total-recall/releases";

    private static PackagedNotes Notes(string version, string? previous = "4.7.0", string summary = "Adds a notice.") =>
        new(version, previous, summary);

    [Fact]
    public void ReleasesUrl_IsTheGitHubReleasesList()
    {
        Assert.Equal(Releases, WhatsNewDecider.ReleasesUrl);
    }

    [Fact]
    public void Decide_NoticeDisabled_ReturnsNull()
    {
        Assert.Null(WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), null, noticeEnabled: false));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("junk")]
    public void Decide_DevBuildRunningVersion_ReturnsNull(string? running)
    {
        Assert.Null(WhatsNewDecider.Decide(running, Notes("4.8.0"), null, true));
    }

    [Fact]
    public void Decide_NeverTold_ReturnsFirstSeenWithSummaryAndReleasesList()
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), null, true);

        Assert.NotNull(n);
        Assert.Equal(NoticeKind.FirstSeen, n!.Kind);
        Assert.Equal("4.8.0", n.Version);
        Assert.Null(n.From);
        Assert.Equal("Adds a notice.", n.Summary);
        Assert.Equal("All releases", n.LinkLabel);
        Assert.Equal(Releases, n.Url);
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("v4.7.0")]
    [InlineData("")]
    [InlineData("   ")]
    public void Decide_SeenNotValidSemVer_CountsAsNeverTold(string seen)
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), seen, true);

        Assert.NotNull(n);
        Assert.Equal(NoticeKind.FirstSeen, n!.Kind);
        Assert.Null(n.From);
        Assert.Equal(Releases, n.Url);
    }

    [Fact]
    public void Decide_RunningNewerAndNotesPreviousMatchesSeen_ReturnsUpdatedWithReleasePage()
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0", previous: "4.7.0"), "4.7.0", true);

        Assert.NotNull(n);
        Assert.Equal(NoticeKind.Updated, n!.Kind);
        Assert.Equal("4.8.0", n.Version);
        Assert.Equal("4.7.0", n.From);
        Assert.Equal("Adds a notice.", n.Summary);
        Assert.Equal("More", n.LinkLabel);
        Assert.Equal(Releases + "/tag/v4.8.0", n.Url);
    }

    [Fact]
    public void Decide_RunningNewerButNotesPreviousDiffers_LinksToReleasesList()
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0", previous: "4.7.0"), "4.5.0", true);

        Assert.NotNull(n);
        Assert.Equal(NoticeKind.Updated, n!.Kind);
        Assert.Equal("4.5.0", n.From);
        Assert.Equal("All releases", n.LinkLabel);
        Assert.Equal(Releases, n.Url);
    }

    [Fact]
    public void Decide_RunningNewerAndNotesHaveNoPrevious_LinksToReleasesList()
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0", previous: null), "4.7.0", true);

        Assert.Equal("All releases", n!.LinkLabel);
        Assert.Equal(Releases, n.Url);
    }

    [Fact]
    public void Decide_SeenPrereleaseOfRunning_ReturnsUpdated()
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.8.0-beta.2", true);

        Assert.NotNull(n);
        Assert.Equal(NoticeKind.Updated, n!.Kind);
        Assert.Equal("4.8.0-beta.2", n.From);
    }

    [Fact]
    public void Decide_NumericOrderingNotLexical_4_10_IsNewerThan4_9()
    {
        var n = WhatsNewDecider.Decide("4.10.0", Notes("4.10.0", previous: "4.9.0"), "4.9.0", true);

        Assert.NotNull(n);
        Assert.Equal(NoticeKind.Updated, n!.Kind);
        Assert.Equal(Releases + "/tag/v4.10.0", n.Url);
    }

    [Fact]
    public void Decide_RunningIsPrereleaseOfSeen_ReturnsNull()
    {
        Assert.Null(WhatsNewDecider.Decide("4.8.0-beta.2", Notes("4.8.0-beta.2"), "4.8.0", true));
    }

    [Fact]
    public void Decide_SeenEqualsRunning_ReturnsNull()
    {
        Assert.Null(WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.8.0", true));
    }

    [Fact]
    public void Decide_SeenEqualsRunningIgnoringBuildMetadata_ReturnsNull()
    {
        Assert.Null(WhatsNewDecider.Decide("4.8.0+abc", Notes("4.8.0"), "4.8.0", true));
    }

    [Fact]
    public void Decide_SeenNewerThanRunning_ReturnsNull()
    {
        Assert.Null(WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.9.0", true));
    }

    [Fact]
    public void Decide_NotesForAnotherVersion_AreIgnored()
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.7.0", previous: "4.7.0", summary: "Old news."), "4.7.0", true);

        Assert.NotNull(n);
        Assert.Equal("", n!.Summary);
        Assert.Equal("All releases", n.LinkLabel);
        Assert.Equal(Releases, n.Url);
    }

    [Fact]
    public void Decide_NotesNull_HasEmptySummaryAndReleasesList()
    {
        var n = WhatsNewDecider.Decide("4.8.0", null, null, true);

        Assert.NotNull(n);
        Assert.Equal("", n!.Summary);
        Assert.Equal(Releases, n.Url);
    }

    [Fact]
    public void Decide_RunningWithBuildMetadata_UsesBareVersionForNotesAndUrl()
    {
        var n = WhatsNewDecider.Decide("4.8.0+abc", Notes("4.8.0", previous: "4.7.0"), "4.7.0", true);

        Assert.NotNull(n);
        Assert.Equal("4.8.0", n!.Version);
        Assert.Equal("Adds a notice.", n.Summary);
        Assert.Equal("More", n.LinkLabel);
        Assert.Equal(Releases + "/tag/v4.8.0", n.Url);
        Assert.DoesNotContain("+", n.Url);
    }

    [Fact]
    public void Decide_SeenWithBuildMetadata_IsStrippedInFromAndMatchesPrevious()
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0", previous: "4.7.0"), "4.7.0+sha", true);

        Assert.Equal("4.7.0", n!.From);
        Assert.Equal("More", n.LinkLabel);
    }

    [Fact]
    public void Decide_SummaryWithAnsiAndLinks_IsSanitised()
    {
        var raw = "\u001b[31mFixes\u001b[0m [the thing](https://evil.example/x) now.";
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0", summary: raw), null, true);

        Assert.Equal("Fixes the thing now.", n!.Summary);
    }
}
