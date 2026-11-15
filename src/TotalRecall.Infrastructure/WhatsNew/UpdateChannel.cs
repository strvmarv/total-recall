// src/TotalRecall.Infrastructure/WhatsNew/UpdateChannel.cs
//
// Release channel of a running version and validation of a release version read
// from the network or from a state file. Pure, no I/O.

using System;

namespace TotalRecall.Infrastructure.WhatsNew;

public static class UpdateChannel
{
    public const string Stable = "stable";
    public const string Pre = "pre";
    public const int MaxVersionLength = 64;

    /// <summary>"pre" when the version parses with a prerelease part; "stable" otherwise (also for null or garbage).</summary>
    public static string Of(string? runningVersion) =>
        SemVer.TryParse(runningVersion, out var v) && v.Prerelease.Length > 0 ? Pre : Stable;

    public static bool IsKnown(string? channel) =>
        string.Equals(channel, Stable, StringComparison.Ordinal)
        || string.Equals(channel, Pre, StringComparison.Ordinal);

    /// <summary>At most 64 characters, no '+', and accepted by <see cref="SemVer.TryParse"/>.</summary>
    public static bool IsValidReleaseVersion(string? version) =>
        version is not null
        && version.Length <= MaxVersionLength
        && version.IndexOf('+') < 0
        && SemVer.TryParse(version, out _);
}
