// src/TotalRecall.Infrastructure/WhatsNew/WhatsNewDecider.cs
//
// Pure decision for the what's-new notice: given the running version, the
// packaged notes, the version this host was last told and the user's setting,
// return the notice to show or null. No I/O, no clock.

using System;

namespace TotalRecall.Infrastructure.WhatsNew;

public enum NoticeKind { FirstSeen, Updated, Available }

/// <summary>For <see cref="NoticeKind.Available"/>: Version is the newer release, From the bare running version, Channel the running channel (what to mark).</summary>
public sealed record Notice(NoticeKind Kind, string Version, string? From, string Summary, string LinkLabel, string Url, string? Channel = null);

/// <summary>The newest release of the running channel, as read from the latest-release state. Untrusted: validated by the decider.</summary>
public sealed record LatestRelease(string Version, string Summary);

public static class WhatsNewDecider
{
    public const string ReleasesUrl = "https://github.com/strvmarv/total-recall/releases";

    private const string MoreLabel = "More";
    private const string AllReleasesLabel = "All releases";

    public static Notice? Decide(string? runningVersion, PackagedNotes? notes, string? seenVersion, bool noticeEnabled,
        LatestRelease? latest = null, string? availableSeen = null)
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

        if (SemVer.Compare(running, seen) <= 0) return DecideAvailable(version, running, latest, availableSeen);

        var from = StripBuild(seenVersion);
        return usable is not null && usable.Previous == from
            ? new Notice(NoticeKind.Updated, version, from, summary, MoreLabel, ReleasesUrl + "/tag/v" + version)
            : new Notice(NoticeKind.Updated, version, from, summary, AllReleasesLabel, ReleasesUrl);
    }

    private static Notice? DecideAvailable(string runningBare, ParsedVersion running, LatestRelease? latest, string? availableSeen)
    {
        if (latest is null || !UpdateChannel.IsValidReleaseVersion(latest.Version)) return null;
        if (!SemVer.TryParse(latest.Version, out var newer)) return null;

        var channel = UpdateChannel.Of(runningBare);
        // A stable host is never nudged toward a prerelease.
        if (channel == UpdateChannel.Stable && newer.Prerelease.Length > 0) return null;
        if (SemVer.Compare(newer, running) <= 0) return null;
        if (string.Equals(availableSeen, latest.Version, StringComparison.Ordinal)) return null;

        return new Notice(NoticeKind.Available, latest.Version, runningBare, SummarySanitizer.Sanitize(latest.Summary),
            MoreLabel, ReleasesUrl + "/tag/v" + latest.Version, channel);
    }

    private static string StripBuild(string version)
    {
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
