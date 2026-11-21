using System;
using System.Collections.Generic;
using System.IO;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Usage;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>A session's project: its key, where the key came from, the directory its sessions are excluded under, and the cwd it was derived from.</summary>
internal sealed record ProjectIdentity(string Key, string KeySource, string? Root, string? Cwd);

/// <summary>
/// Derives the project key, key source and <c>project_root</c> of a session from its working directory (spec section 6,
/// items 1 to 6). Item 7 (a <c>path</c> key never replaces a stored <c>remote</c> key) belongs to the ingester.
/// </summary>
internal static class ProjectIdentityRule
{
    public const string Unknown = "unknown";
    public const string SourceRemote = "remote";
    public const string SourcePath = "path";

    private static readonly string[] WorktreeParents = { ".claude", ".zoid" };

    /// <param name="headCwd">The <c>cwd</c> of the file's head, if it has one.</param>
    /// <param name="projectDirName">The encoded project directory name, decoded when there is no head cwd.</param>
    /// <param name="userHome">The user's home directory; an empty string means it is unknown and only then is the home check skipped.</param>
    public static ProjectIdentity Derive(string? headCwd, string projectDirName, ProjectResolver resolver, string userHome)
    {
        ArgumentNullException.ThrowIfNull(projectDirName);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(userHome);

        var cwd = !string.IsNullOrWhiteSpace(headCwd)
            ? headCwd
            : projectDirName.Length == 0 ? null : ClaudeCodeUsageImporter.DecodeProjectDirName(projectDirName);
        if (string.IsNullOrWhiteSpace(cwd))
            return new ProjectIdentity(Unknown, SourcePath, null, null);

        string? key;
        string? root;
        var folderOf = cwd; // the path whose last segment names the project when the resolver gives no key
        if (IsAbsolute(cwd) && DirectoryExists(cwd))
        {
            // An existing directory: the resolver's walk gives the key and the root; no repository means no root.
            key = resolver.Resolve(cwd);
            root = resolver.ResolveRoot(cwd);
        }
        else
        {
            // A missing directory (a deleted worktree is the common case): the repository that held it, else the cwd.
            // A path that is not absolute names no directory of its own (it would resolve against this process's
            // working directory), so it has no root and is never looked up; its last segment still gives the key.
            var candidate = CutBackWorktree(cwd);
            folderOf = candidate;
            if (!IsAbsolute(candidate))
            {
                key = null;
                root = null;
            }
            else if (DirectoryExists(candidate))
            {
                key = resolver.Resolve(candidate);
                root = resolver.ResolveRoot(candidate);
            }
            else
            {
                key = null;
                root = TrimSeparators(candidate);
            }
        }

        if (string.IsNullOrEmpty(key))
            key = LastSegment(folderOf)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
            key = Unknown;

        if (root is not null && !IsAcceptableRoot(root, userHome))
            root = null;

        return new ProjectIdentity(key, key.Contains('/') ? SourceRemote : SourcePath, root, cwd);
    }

    /// <summary>The part of <paramref name="cwd"/> before a <c>/.claude/worktrees/&lt;name&gt;</c> or <c>/.zoid/worktrees/&lt;name&gt;</c> segment, else the cwd.</summary>
    private static string CutBackWorktree(string cwd)
    {
        var segments = Segments(cwd);
        for (var i = 0; i + 2 < segments.Count; i++)
        {
            // ".claude" or ".zoid", then "worktrees", then the worktree's name (a path that stops at "worktrees" has none).
            if (Array.IndexOf(WorktreeParents, segments[i].Value) < 0) continue;
            if (segments[i + 1].Value != "worktrees") continue;
            // The root is everything before ".claude"/".zoid", in the original text and separators (empty at the top).
            // A drive root keeps its separator: "C:" alone would be relative to the drive's current directory.
            if (i == 0) return string.Empty;
            var end = segments[i - 1].End;
            return i == 1 && IsDrive(segments[0].Value) ? cwd[..(end + 1)] : cwd[..end];
        }

        return cwd;
    }

    private readonly record struct Segment(string Value, int Start, int End);

    /// <summary>The non-empty segments of a path split on both <c>/</c> and <c>\</c>, with their positions in the text.</summary>
    private static List<Segment> Segments(string path)
    {
        var list = new List<Segment>();
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/' && path[i] != '\\') continue;
            if (i > start) list.Add(new Segment(path[start..i], start, i));
            start = i + 1;
        }

        return list;
    }

    private static bool IsDrive(string segment) =>
        segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':';

    /// <summary>
    /// A path that names one directory whatever the current directory: a leading <c>/</c> (on every system: a Windows
    /// session's cwd always carries a drive), a UNC <c>\\server</c> or a drive followed by a separator. A relative path,
    /// a drive-relative <c>C:dir</c> and a rooted <c>\dir</c> are not.
    /// </summary>
    private static bool IsAbsolute(string path) =>
        path.StartsWith('/')
        || path.StartsWith(@"\\", StringComparison.Ordinal)
        || (path.Length >= 3 && IsDrive(path[..2]) && path[2] is '/' or '\\');

    private static string? LastSegment(string path)
    {
        var segments = Segments(path);
        return segments.Count == 0 ? null : segments[^1].Value;
    }

    private static string TrimSeparators(string path)
    {
        var end = path.Length;
        while (end > 0 && (path[end - 1] == '/' || path[end - 1] == '\\')) end--;
        return path[..end];
    }

    /// <summary>False for a filesystem root, the home directory or an ancestor of it.</summary>
    private static bool IsAcceptableRoot(string root, string userHome)
    {
        var trimmed = TrimSeparators(root);
        var segments = Segments(trimmed);
        if (segments.Count == 0) return false;
        // A drive root such as "C:" (the trailing separator is gone by now).
        if (segments.Count == 1 && segments[0].Value.Length == 2 && segments[0].Value[1] == ':') return false;

        if (userHome.Length == 0) return true;
        var home = TrimSeparators(userHome);
        // A home such as "/" trims to nothing: the filesystem-root check above is all that applies.
        if (home.Length == 0) return true;
        // Both separators count, so a Windows home and root compare alike whichever separators they use.
        return !TranscriptStore.IsUnder(home.Replace('\\', '/'), trimmed.Replace('\\', '/'));
    }

    private static bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }
}
