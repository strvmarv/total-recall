namespace TotalRecall.Infrastructure.Skills;

public interface ICustomDirsSkillScanner
{
    Task<ClaudeCodeScanResult> ScanAsync(CancellationToken ct);
}

public sealed class CustomDirsSkillScanner : ICustomDirsSkillScanner
{
    private readonly string[] _dirs;
    private readonly string _home;

    public CustomDirsSkillScanner(string[] dirs, string? homeOverride = null)
    {
        _dirs = dirs;
        _home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public Task<ClaudeCodeScanResult> ScanAsync(CancellationToken ct)
    {
        var skills = new List<ImportedSkill>();
        var errors = new List<ScanError>();

        foreach (var raw in _dirs)
        {
            ct.ThrowIfCancellationRequested();
            var expanded = raw.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(_home, raw[2..])
                : raw;
            SkillScannerCore.ScanRoot(expanded, tagProject: null, skills, errors, ct);
        }

        return Task.FromResult(new ClaudeCodeScanResult(skills, errors));
    }
}
