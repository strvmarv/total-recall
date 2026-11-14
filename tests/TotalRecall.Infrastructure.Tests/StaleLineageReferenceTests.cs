using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Guards against comments that point at the deleted TypeScript implementation
/// (its source directory and its SQLite runtime). The names are assembled from
/// pieces so that this file does not match its own scan.
/// </summary>
public class StaleLineageReferenceTests
{
    private static readonly string[] Forbidden =
    {
        "src" + "-ts",
        "bun" + ":sqlite",
    };

    private static readonly string[] SkippedDirectories = { "bin", "obj" };

    [Fact]
    public void NoSourceOrTestFileNamesTheDeletedTypeScriptImplementation()
    {
        var root = FindRepoRoot();
        var hits = new List<string>();
        var scanned = 0;

        foreach (var top in new[] { "src", "tests" })
        {
            var topDir = Path.Combine(root, top);
            Assert.True(Directory.Exists(topDir), $"expected directory {topDir}");

            foreach (var file in Directory.EnumerateFiles(topDir, "*", SearchOption.AllDirectories))
            {
                if (!IsSourceFile(file)) continue;

                // Skip build output by path segment relative to the repository root, so a
                // checkout that happens to live under a directory called bin or obj is still scanned.
                var relative = Path.GetRelativePath(root, file);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Take(segments.Length - 1).Any(s => SkippedDirectories.Contains(s, StringComparer.Ordinal)))
                    continue;

                scanned++;
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    foreach (var word in Forbidden)
                    {
                        if (line.Contains(word, StringComparison.OrdinalIgnoreCase))
                        {
                            hits.Add($"{relative.Replace('\\', '/')}:{lineNumber}: {line.Trim()}");
                            break;
                        }
                    }
                }
            }
        }

        Assert.True(scanned > 0, "the scan found no .cs or .fs file, so it proves nothing");
        Assert.True(
            hits.Count == 0,
            $"{hits.Count} line(s) name the deleted TypeScript implementation or its SQLite runtime:\n  "
            + string.Join("\n  ", hits));
    }

    private static bool IsSourceFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fs", StringComparison.OrdinalIgnoreCase);
    }

    // Throws instead of returning null: a guard that cannot find the repository must fail, not skip.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "repository root not found (looked for global.json next to a src directory above " + AppContext.BaseDirectory + ")");
    }
}
