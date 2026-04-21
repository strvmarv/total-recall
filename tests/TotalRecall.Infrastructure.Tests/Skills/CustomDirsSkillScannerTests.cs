using TotalRecall.Infrastructure.Skills;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Skills;

public class CustomDirsSkillScannerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { }
    }

    private string MakeTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tr-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _tempDirs.Add(d);
        return d;
    }

    [Fact]
    public async Task ScanAsync_SingleFileSkill_Returned()
    {
        var temp = MakeTempDir();
        var dir = Path.Combine(temp, "skills");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "my-skill.md"),
            "---\nname: my-skill\ndescription: A skill.\n---\n\nBody.\n");

        var scanner = new CustomDirsSkillScanner(dirs: [dir]);
        var result = await scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(result.Errors);
        var skill = Assert.Single(result.Skills);
        Assert.Equal("my-skill", skill.Name);
        Assert.Equal("user", skill.SuggestedScope);
    }

    [Fact]
    public async Task ScanAsync_TildePath_ExpandsToHome()
    {
        var temp = MakeTempDir();
        var home = Path.Combine(temp, "home");
        var dir = Path.Combine(home, "my-skills");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tilde-skill.md"),
            "---\nname: tilde-skill\ndescription: Tilde.\n---\n\nBody.\n");

        var scanner = new CustomDirsSkillScanner(dirs: ["~/my-skills"], homeOverride: home);
        var result = await scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(result.Errors);
        var skill = Assert.Single(result.Skills);
        Assert.Equal("tilde-skill", skill.Name);
    }

    [Fact]
    public async Task ScanAsync_NonExistentDir_ReturnsEmpty()
    {
        var temp = MakeTempDir();
        var scanner = new CustomDirsSkillScanner(
            dirs: [Path.Combine(temp, "does-not-exist")]);

        var result = await scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Skills);
    }

    [Fact]
    public async Task ScanAsync_BareTilde_ReturnsError()
    {
        var scanner = new CustomDirsSkillScanner(dirs: ["~"]);
        var result = await scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(result.Skills);
        var error = Assert.Single(result.Errors);
        Assert.Equal("~", error.SourcePath);
        Assert.Contains("tilde-slash", error.Error);
    }

    [Fact]
    public async Task ScanAsync_MultipleDirs_SkillsFromAllReturned()
    {
        var temp = MakeTempDir();
        var dir1 = Path.Combine(temp, "skills-a");
        var dir2 = Path.Combine(temp, "skills-b");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        File.WriteAllText(Path.Combine(dir1, "skill-a.md"),
            "---\nname: skill-a\n---\n\nA.\n");
        File.WriteAllText(Path.Combine(dir2, "skill-b.md"),
            "---\nname: skill-b\n---\n\nB.\n");

        var scanner = new CustomDirsSkillScanner(dirs: [dir1, dir2]);
        var result = await scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Skills.Count);
        Assert.Contains(result.Skills, s => s.Name == "skill-a");
        Assert.Contains(result.Skills, s => s.Name == "skill-b");
    }
}
