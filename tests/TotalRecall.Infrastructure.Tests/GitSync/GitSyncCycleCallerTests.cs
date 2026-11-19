using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-i-4, AC 17: nothing but the cycle and its tests calls the cycle, and nothing but the cycle (and the
/// pusher's own files) names the pusher. The cycle runs the push, so until the secret scan of 1b-iii has merged it
/// must have no caller. A source scan over the code files, in the style of <c>StaleLineageReferenceTests</c>; the
/// contributor guides are markdown and are not scanned.
/// </summary>
public sealed class GitSyncCycleCallerTests
{
    private static readonly string[] SkippedDirectories = ["bin", "obj"];

    private static IEnumerable<(string FullPath, string Relative)> SourceFiles()
    {
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "src");
        Assert.True(Directory.Exists(directory), $"expected directory {directory}");
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".fs", StringComparison.OrdinalIgnoreCase))
                continue;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/')[..^1].Any(segment => SkippedDirectories.Contains(segment, StringComparer.Ordinal)))
                continue;
            yield return (file, relative);
        }
    }

    private static List<string> FilesNaming(string word) =>
        [.. SourceFiles().Where(f => File.ReadAllText(f.FullPath).Contains(word, StringComparison.Ordinal)).Select(f => f.Relative)];

    [Fact]
    public void NothingOutsideTheCycleNamesTheCycle()
    {
        Assert.NotEmpty(SourceFiles());
        var offenders = FilesNaming("GitSyncCycle").Where(f => !Path.GetFileName(f).StartsWith("GitSyncCycle", StringComparison.Ordinal)).ToList();
        Assert.True(offenders.Count == 0, "code outside GitSyncCycle*.cs names the cycle:\n  " + string.Join("\n  ", offenders));
        Assert.NotEmpty(FilesNaming("GitSyncCycle")); // the scan sees the cycle's own files, so it proves something
    }

    [Fact]
    public void NothingButTheCycleAndThePushersOwnFilesNamesThePusher()
    {
        var offenders = FilesNaming("GitSyncPusher")
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return !name.StartsWith("GitSyncPusher", StringComparison.Ordinal) && !name.StartsWith("GitSyncCycle", StringComparison.Ordinal);
            })
            .ToList();
        Assert.True(offenders.Count == 0, "code outside the pusher and the cycle names the pusher:\n  " + string.Join("\n  ", offenders));
        Assert.Contains(FilesNaming("GitSyncPusher"), f => Path.GetFileName(f).StartsWith("GitSyncCycle", StringComparison.Ordinal));
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
