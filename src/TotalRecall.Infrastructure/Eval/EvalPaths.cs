// src/TotalRecall.Infrastructure/Eval/EvalPaths.cs
//
// Locates the bundled eval corpus / benchmark files. The eval/ directory
// ships at the package root (alongside README, models/, binaries/), so it
// must be resolved relative to the BINARY location, not the process CWD:
// the Web UI server and MCP host are launched with arbitrary working
// directories, and a CWD-relative default fails with "corpus not found"
// (the eval_benchmark Web UI failure this consolidates the fix for).
//
// Walking up from AppContext.BaseDirectory mirrors what EmbedderFactory does
// to find models/ and what EvalGrowHandler/GrowCommand previously did inline
// for the benchmark file; this is the single shared home for that logic.

using System;
using System.IO;

namespace TotalRecall.Infrastructure.Eval;

/// <summary>
/// Resolves bundled eval data files (corpus, benchmark) relative to the binary
/// location rather than the process working directory.
/// </summary>
public static class EvalPaths
{
    /// <summary>
    /// Resolve <c>eval/&lt;segments&gt;</c> by walking up from the running
    /// binary's directory (<see cref="AppContext.BaseDirectory"/>).
    /// </summary>
    public static string Resolve(params string[] segments)
        => ResolveFrom(AppContext.BaseDirectory, segments);

    /// <summary>
    /// Resolve <c>eval/&lt;segments&gt;</c> by walking up from
    /// <paramref name="startDir"/>. Returns the first existing match; if none
    /// is found anywhere up the tree, returns the CWD-relative
    /// <c>eval/&lt;segments&gt;</c> as a last resort (preserving the historical
    /// fallback behavior).
    /// </summary>
    public static string ResolveFrom(string startDir, params string[] segments)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Combine(dir.FullName, segments);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Combine(root: null, segments);
    }

    private static string Combine(string? root, string[] segments)
    {
        var parts = new string[(root is null ? 1 : 2) + segments.Length];
        var i = 0;
        if (root is not null) parts[i++] = root;
        parts[i++] = "eval";
        foreach (var s in segments) parts[i++] = s;
        return Path.Combine(parts);
    }
}
