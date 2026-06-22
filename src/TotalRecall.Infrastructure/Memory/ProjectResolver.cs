// src/TotalRecall.Infrastructure/Memory/ProjectResolver.cs
//
// Detects the current project key (lowercase owner/repo slug, or lowercase
// repo-root folder name) by walking up from a start directory to the git
// root. Pure filesystem — no `git` subprocess. Fail-soft: any error yields
// null (callers treat null as "globals only"). Results are memoized in
// memory keyed by the normalized absolute start directory; null is a valid
// cached answer. There is no persistent cross-process cache.

using System;
using System.Collections.Concurrent;
using System.IO;

namespace TotalRecall.Infrastructure.Memory;

public sealed class ProjectResolver
{
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);

    public string? Resolve(string startDir)
    {
        string key;
        try { key = Path.GetFullPath(startDir); }
        catch { return null; }
        return _cache.GetOrAdd(key, ResolveUncached);
    }

    private static string? ResolveUncached(string startDir)
    {
        try
        {
            if (!Directory.Exists(startDir)) return null;

            var dotGit = FindDotGit(startDir);
            if (dotGit is null) return null;

            var commonDir = ResolveCommonDir(dotGit);
            if (commonDir is null) return null;

            var slug = SlugFromConfig(Path.Combine(commonDir, "config"));
            if (slug is not null) return slug;

            // No remote: repo-root folder = parent of the common git dir.
            var repoRoot = Path.GetDirectoryName(commonDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var folder = repoRoot is null ? null : Path.GetFileName(repoRoot);
            return string.IsNullOrEmpty(folder) ? null : folder.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Walk upward for a <c>.git</c> dir or file; return its path.</summary>
    private static string? FindDotGit(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var asDir = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(asDir) || File.Exists(asDir)) return asDir;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Resolves the common git directory (where the canonical <c>config</c>
    /// lives). For a normal <c>.git</c> directory that is the directory
    /// itself. For a <c>.git</c> FILE (linked worktree/submodule), follow
    /// <c>gitdir:</c> and then a <c>commondir</c> file if present.
    /// </summary>
    private static string? ResolveCommonDir(string dotGitPath)
    {
        if (Directory.Exists(dotGitPath)) return dotGitPath;

        // .git is a file: "gitdir: <path>"
        var text = File.ReadAllText(dotGitPath).Trim();
        const string prefix = "gitdir:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var gitDir = text[prefix.Length..].Trim();
        if (!Path.IsPathRooted(gitDir))
        {
            var baseDir = Path.GetDirectoryName(dotGitPath)!;
            gitDir = Path.GetFullPath(Path.Combine(baseDir, gitDir));
        }

        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDirFile))
        {
            var common = File.ReadAllText(commonDirFile).Trim();
            if (!Path.IsPathRooted(common))
                common = Path.GetFullPath(Path.Combine(gitDir, common));
            return common;
        }
        return gitDir;
    }

    /// <summary>
    /// Reads <c>[remote "origin"] url</c> from a git config file and reduces
    /// it via <see cref="ProjectKey.FromRemoteUrl"/>. Minimal INI parse.
    /// </summary>
    private static string? SlugFromConfig(string configPath)
    {
        if (!File.Exists(configPath)) return null;
        var inOriginRemote = false;
        foreach (var raw in File.ReadAllLines(configPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            if (line[0] == '[')
            {
                // Section header, e.g. [remote "origin"] or [core]
                inOriginRemote = line.Replace(" ", "")
                    .StartsWith("[remote\"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inOriginRemote) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var k = line[..eq].Trim();
            if (!k.Equals("url", StringComparison.OrdinalIgnoreCase)) continue;
            var v = line[(eq + 1)..].Trim();
            return ProjectKey.FromRemoteUrl(v);
        }
        return null;
    }
}
