using TotalRecall.Infrastructure.Skills;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Skills;

/// <summary>
/// Exercises <see cref="ClaudeCodeSkillScanner"/> against the copy-on-disk fixture
/// set under <c>tests/fixtures/skills/</c>. Each test isolates its own temp project
/// root so fixtures can be composed (e.g. the malformed test drops both
/// malformed-skill and bundle-skill under a single scan root to assert partial
/// success).
/// </summary>
public class ClaudeCodeSkillScannerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_BundleWithSupportingFiles_ReturnsCompleteBundle()
    {
        var (scanner, projectRoot) = BuildScannerOver("bundle-skill");
        var result = await scanner.ScanAsync("u1", projectRoot, CancellationToken.None);
        Assert.Empty(result.Errors);
        var skill = Assert.Single(result.Skills);
        Assert.Equal("bundle-skill", skill.Name);
        Assert.Equal("Multi-file bundled skill.", skill.Description);
        Assert.Contains(skill.Files, f => f.RelativePath == "references/tools.md");
        Assert.Contains(skill.Files, f => f.RelativePath == "scripts/setup.sh");
        Assert.Equal("user", skill.SuggestedScope);
        Assert.Equal("user:u1", skill.SuggestedScopeId);
    }

    [Fact]
    public async Task ScanAsync_BinaryOverLimit_RecordsPlaceholder()
    {
        // Build a synthetic bundle under a temp project: SKILL.md with frontmatter
        // + a 2 MB big.bin sibling.
        var temp = MakeTempDir();
        var bundle = Path.Combine(temp, "project-root", ".claude", "skills", "binary-skill");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "SKILL.md"),
            "---\nname: binary-skill\ndescription: Has a big binary.\n---\n\nBody.\n");
        var bigPath = Path.Combine(bundle, "big.bin");
        // 2 MB of zeros — exceeds the 1 MiB cap.
        File.WriteAllBytes(bigPath, new byte[2 * 1024 * 1024]);

        var scanner = new ClaudeCodeSkillScanner(homeOverride: Path.Combine(temp, "home"));
        var projectRoot = Path.Combine(temp, "project-root");
        var result = await scanner.ScanAsync("u1", projectRoot, CancellationToken.None);

        var skill = Assert.Single(result.Skills);
        var big = Assert.Single(skill.Files, f => f.RelativePath == "big.bin");
        Assert.Equal("binary-ignored", big.ContentType);
        Assert.Equal(string.Empty, big.Content);
        Assert.True(big.SizeBytes > 1_048_576, $"SizeBytes={big.SizeBytes}");
    }

    [Fact]
    public async Task ScanAsync_MalformedFrontmatter_ReportsErrorContinues()
    {
        // Compose a scan root with BOTH malformed-skill AND bundle-skill so we
        // assert the scanner keeps going after a failure.
        var (scanner, projectRoot) = BuildScannerOver("bundle-skill", "malformed-skill");
        var result = await scanner.ScanAsync("u1", projectRoot, CancellationToken.None);

        Assert.Single(result.Skills, s => s.Name == "bundle-skill");
        Assert.DoesNotContain(result.Skills, s => s.Name == "malformed-skill");
        Assert.Single(result.Errors, e => e.SourcePath.EndsWith("malformed-skill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanAsync_SkillIgnore_HonoursPatterns()
    {
        var (scanner, projectRoot) = BuildScannerOver("ignored-skill");
        var result = await scanner.ScanAsync("u1", projectRoot, CancellationToken.None);
        var skill = Assert.Single(result.Skills);
        Assert.Equal("ignored-skill", skill.Name);
        Assert.DoesNotContain(skill.Files, f => f.RelativePath == "secret.txt");
    }

    [Fact]
    public async Task ScanAsync_ProjectPath_AppendsProjectTag()
    {
        // projectPath must be a directory named "my-project" whose name is
        // picked up into the tag.
        var temp = MakeTempDir();
        var projectRoot = Path.Combine(temp, "my-project");
        var target = Path.Combine(projectRoot, ".claude", "skills", "bundle-skill");
        Directory.CreateDirectory(target);
        CopyDirectory(Path.Combine(FixturesRoot(), "bundle-skill"), target);

        var scanner = new ClaudeCodeSkillScanner(homeOverride: Path.Combine(temp, "home"));
        var result = await scanner.ScanAsync("u1", projectRoot, CancellationToken.None);

        var skill = Assert.Single(result.Skills);
        Assert.Contains("project:my-project", skill.SuggestedTags);
    }

    [Fact]
    public async Task ScanAsync_HomeBundle_HasNoProjectTag()
    {
        // Copy bundle-skill under a fake $HOME/.claude/skills/ and scan with no project path.
        var temp = MakeTempDir();
        var homeRoot = Path.Combine(temp, "home");
        var target = Path.Combine(homeRoot, ".claude", "skills", "bundle-skill");
        Directory.CreateDirectory(target);
        CopyDirectory(Path.Combine(FixturesRoot(), "bundle-skill"), target);

        var scanner = new ClaudeCodeSkillScanner(homeOverride: homeRoot);
        var result = await scanner.ScanAsync("u1", projectPath: null, CancellationToken.None);

        var skill = Assert.Single(result.Skills);
        Assert.Empty(skill.SuggestedTags);
    }

    // ---------- helpers ----------

    /// <summary>
    /// Copies the named fixture(s) under a fresh temp structure
    /// <c>{temp}/project-root/.claude/skills/{fixture}</c> and returns a scanner
    /// whose user-home points at a separate, empty directory so the home-dir
    /// scan is a no-op.
    /// </summary>
    private (ClaudeCodeSkillScanner Scanner, string ProjectRoot) BuildScannerOver(params string[] fixtureNames)
    {
        var temp = MakeTempDir();
        var projectRoot = Path.Combine(temp, "project-root");
        var skillsRoot = Path.Combine(projectRoot, ".claude", "skills");
        Directory.CreateDirectory(skillsRoot);

        foreach (var name in fixtureNames)
        {
            var src = Path.Combine(FixturesRoot(), name);
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(skillsRoot, Path.GetFileName(src)));
            }
            else if (Directory.Exists(src))
            {
                var dst = Path.Combine(skillsRoot, name);
                Directory.CreateDirectory(dst);
                CopyDirectory(src, dst);
            }
            else
            {
                throw new InvalidOperationException($"Fixture not found: {src}");
            }
        }

        var scanner = new ClaudeCodeSkillScanner(homeOverride: Path.Combine(temp, "home"));
        return (scanner, projectRoot);
    }

    private string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "trskills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private static string FixturesRoot()
    {
        // bin/Debug/net8.0/ → ../../.. → tests/TotalRecall.Infrastructure.Tests → .. → tests
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "skills");
        return Path.GetFullPath(path);
    }
}
