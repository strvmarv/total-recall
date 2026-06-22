// src/TotalRecall.Infrastructure/Memory/ProjectKey.cs
//
// Pure reducer from a git remote URL to a lowercase "owner/repo" slug.
// Used to key a pinned directive to the repository it belongs to.

using System;

namespace TotalRecall.Infrastructure.Memory;

public static class ProjectKey
{
    /// <summary>
    /// Reduces a git remote URL to a lowercase <c>owner/repo</c> slug, or
    /// <c>null</c> when the URL cannot be parsed into at least two path
    /// segments. Handles scp-style (<c>git@host:owner/repo.git</c>),
    /// https, and ssh:// forms; strips a trailing <c>.git</c> and slash.
    /// </summary>
    public static string? FromRemoteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var s = url.Trim();

        // Normalize scp-style "git@host:owner/repo" → "owner/repo" tail.
        // For URL forms, drop scheme and host by taking the path component.
        string path;
        var scpColon = s.IndexOf(':');
        if (!s.Contains("://") && scpColon >= 0)
        {
            // scp-style: everything after the first ':' is the path.
            path = s[(scpColon + 1)..];
        }
        else
        {
            // scheme://[user@]host[:port]/owner/repo
            var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx < 0) return null;
            var afterScheme = s[(schemeIdx + 3)..];
            var firstSlash = afterScheme.IndexOf('/');
            if (firstSlash < 0) return null;
            path = afterScheme[(firstSlash + 1)..];
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        path = path.Trim('/');
        if (path.Length == 0) return null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        // owner = second-to-last segment, repo = last segment. This handles
        // GitLab subgroups by taking the immediate parent group as "owner".
        var owner = segments[^2];
        var repo = segments[^1];
        return $"{owner}/{repo}".ToLowerInvariant();
    }
}
