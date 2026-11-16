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

    private static LatestRelease L(string v, string s = "") => new(v, s);

    [Fact]
    public void Decide_SeenEqualsRunning_NewerLatest_IsAvailable()
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.8.0", true, L("4.9.0", "Faster sync."));

        Assert.NotNull(n);
        Assert.Equal(NoticeKind.Available, n!.Kind);
        Assert.Equal("4.9.0", n.Version);
        Assert.Equal("4.8.0", n.From);
        Assert.Equal("Faster sync.", n.Summary);
        Assert.Equal("More", n.LinkLabel);
        Assert.Equal(Releases + "/tag/v4.9.0", n.Url);
        Assert.Equal("stable", n.Channel);
    }

    [Fact]
    public void Decide_AvailableSeenEqualsLatest_IsNull()
    {
        Assert.Null(WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.8.0", true, L("4.9.0", "x"), "4.9.0"));
    }

    [Fact]
    public void Decide_EvenNewerThanAvailableSeen_IsAvailable()
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.8.0", true, L("4.10.0", "x"), "4.9.0");

        Assert.NotNull(n);
        Assert.Equal(NoticeKind.Available, n!.Kind);
        Assert.Equal("4.10.0", n.Version);
    }

    [Fact]
    public void Decide_NotSeen_WithNewerLatest_IsFirstSeenNotAvailable()
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), null, true, L("4.9.0", "x"));

        Assert.Equal(NoticeKind.FirstSeen, n!.Kind);
    }

    [Fact]
    public void Decide_UpdatedAndNewerLatest_IsUpdatedOnly()
    {
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.7.0", true, L("4.9.0", "x"));

        Assert.Equal(NoticeKind.Updated, n!.Kind);
    }

    [Fact]
    public void Decide_Downgrade_WithNewerLatest_IsAvailable()
    {
        var n = WhatsNewDecider.Decide("4.7.0", Notes("4.7.0", previous: "4.6.0"), "4.8.0", true, L("4.9.0", "x"));

        Assert.Equal(NoticeKind.Available, n!.Kind);
        Assert.Equal("4.7.0", n.From);
    }

    [Theory]
    [InlineData("4.7.0")]
    [InlineData("4.8.0")]
    [InlineData("4.8.0-rc.1")]
    public void Decide_LatestLowerOrEqual_IsNull(string latest)
    {
        Assert.Null(WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.8.0", true, L(latest, "x")));
    }

    [Fact]
    public void Decide_StableRunning_PrereleaseLatest_IsNull()
    {
        Assert.Null(WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.8.0", true, L("4.9.0-beta.1", "x")));
    }

    [Theory]
    [InlineData("4.9.0-beta.2")]
    [InlineData("4.9.0")]
    public void Decide_PrereleaseRunning_StableOrPrereleaseLatestNewer_IsAvailable(string latest)
    {
        var n = WhatsNewDecider.Decide("4.9.0-beta.1", Notes("4.9.0-beta.1"), "4.9.0-beta.1", true, L(latest, "x"));

        Assert.NotNull(n);
        Assert.Equal(NoticeKind.Available, n!.Kind);
        Assert.Equal(latest, n.Version);
        Assert.Equal("4.9.0-beta.1", n.From);
        Assert.Equal("pre", n.Channel);
    }

    [Theory]
    [InlineData("v4.9.0")]
    [InlineData("4.9.0+b")]
    [InlineData("4.9.0-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("")]
    [InlineData("x")]
    public void Decide_InvalidLatestVersion_IsNull(string latest)
    {
        Assert.Null(WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.8.0", true, L(latest, "x")));
    }

    [Fact]
    public void Decide_LatestNull_KeepsOldBehaviour()
    {
        Assert.Null(WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.8.0", true, null));
    }

    [Theory]
    [InlineData("4.8.0", false)]
    [InlineData("1.0.0", true)]
    [InlineData("unknown", true)]
    public void Decide_NoticeDisabledOrDevBuild_IgnoresLatest(string running, bool noticeEnabled)
    {
        Assert.Null(WhatsNewDecider.Decide(running, Notes("4.8.0"), "4.8.0", noticeEnabled, L("4.9.0", "x")));
    }

    [Fact]
    public void Decide_Available_LinkIsReleasePage_SummarySanitised()
    {
        var raw = "\u001b[31mNew\u001b[0m [docs](https://evil.example) here\nnext";
        var n = WhatsNewDecider.Decide("4.8.0", Notes("4.8.0"), "4.8.0", true, L("4.9.0", raw));

        Assert.Equal("New docs here next", n!.Summary);
        Assert.Equal(Releases + "/tag/v4.9.0", n.Url);
    }

    [Fact]
    public void Decide_Available_RunningWithBuildMetadata_FromIsBare()
    {
        var n = WhatsNewDecider.Decide("4.8.0+abc", Notes("4.8.0"), "4.8.0", true, L("4.9.0", "x"));

        Assert.Equal("4.8.0", n!.From);
        Assert.Equal("stable", n.Channel);
    }
}
