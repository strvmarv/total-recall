// src/TotalRecall.Infrastructure/WhatsNew/WhatsNewDecider.cs
//
// Pure decision for the what's-new notice: given the running version, the
// packaged notes, the version this host was last told and the user's setting,
// return the notice to show or null. No I/O, no clock.

namespace TotalRecall.Infrastructure.WhatsNew;

public enum NoticeKind { FirstSeen, Updated }

public sealed record Notice(NoticeKind Kind, string Version, string? From, string Summary, string LinkLabel, string Url);

public static class WhatsNewDecider
{
    public const string ReleasesUrl = "https://github.com/strvmarv/total-recall/releases";

    private const string MoreLabel = "More";
    private const string AllReleasesLabel = "All releases";

    public static Notice? Decide(string? runningVersion, PackagedNotes? notes, string? seenVersion, bool noticeEnabled)
    {
        if (!noticeEnabled) return null;
        if (SemVer.IsDevBuild(runningVersion)) return null;
        if (!SemVer.TryParse(runningVersion, out var running)) return null;

        var version = StripBuild(runningVersion!);
        var usable = notes is not null && notes.Version == version ? notes : null;
        var summary = usable is null ? "" : SummarySanitizer.Sanitize(usable.Summary);

        // A seen version that is empty or not valid SemVer counts as never told.
        if (string.IsNullOrEmpty(seenVersion) || !SemVer.TryParse(seenVersion, out var seen))
        {
            return new Notice(NoticeKind.FirstSeen, version, null, summary, AllReleasesLabel, ReleasesUrl);
        }

        if (SemVer.Compare(running, seen) <= 0) return null;

        var from = StripBuild(seenVersion);
        return usable is not null && usable.Previous == from
            ? new Notice(NoticeKind.Updated, version, from, summary, MoreLabel, ReleasesUrl + "/tag/v" + version)
            : new Notice(NoticeKind.Updated, version, from, summary, AllReleasesLabel, ReleasesUrl);
    }

    private static string StripBuild(string version)
    {
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
